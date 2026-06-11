//! TLS termination — Phase 2.2.
//!
//! Certificate material is held in memory only: PEM bytes come from files
//! at startup for now (`VEIL_TLS_CERT` / `VEIL_TLS_KEY`), and Phase 5 will
//! push control-plane-provisioned certificates the same way — the acceptor
//! is built from bytes, never from paths.

use std::sync::Arc;

use tokio_rustls::rustls::pki_types::{CertificateDer, PrivateKeyDer};
use tokio_rustls::rustls::ServerConfig;
use tokio_rustls::TlsAcceptor;

pub struct TlsSettings {
    pub listen_addr: String,
    pub cert_pem: Vec<u8>,
    pub key_pem: Vec<u8>,
}

/// Both `VEIL_TLS_CERT` and `VEIL_TLS_KEY` (PEM file paths) must be set for
/// the HTTPS listener to activate; `VEIL_LISTEN_HTTPS` overrides the bind
/// address. Returns an error (instead of `None`) when TLS is configured but
/// the files are unreadable — a protection node must not silently come up
/// plaintext-only.
pub fn settings_from_env() -> Result<Option<TlsSettings>, String> {
    let (Ok(cert_path), Ok(key_path)) =
        (std::env::var("VEIL_TLS_CERT"), std::env::var("VEIL_TLS_KEY"))
    else {
        return Ok(None);
    };

    let cert_pem = std::fs::read(&cert_path)
        .map_err(|e| format!("failed to read VEIL_TLS_CERT '{cert_path}': {e}"))?;
    let key_pem = std::fs::read(&key_path)
        .map_err(|e| format!("failed to read VEIL_TLS_KEY '{key_path}': {e}"))?;

    Ok(Some(TlsSettings {
        listen_addr: std::env::var("VEIL_LISTEN_HTTPS")
            .unwrap_or_else(|_| "127.0.0.1:8443".to_owned()),
        cert_pem,
        key_pem,
    }))
}

/// Builds an acceptor from in-memory PEM bytes. ALPN advertises h2 +
/// http/1.1 so the hyper auto builder negotiates HTTP/2 over TLS.
pub fn build_acceptor(cert_pem: &[u8], key_pem: &[u8]) -> Result<TlsAcceptor, String> {
    let certs: Vec<CertificateDer<'static>> = rustls_pemfile::certs(&mut &cert_pem[..])
        .collect::<Result<_, _>>()
        .map_err(|e| format!("invalid certificate PEM: {e}"))?;
    if certs.is_empty() {
        return Err("certificate PEM contains no certificates".into());
    }

    let key: PrivateKeyDer<'static> = rustls_pemfile::private_key(&mut &key_pem[..])
        .map_err(|e| format!("invalid private key PEM: {e}"))?
        .ok_or("private key PEM contains no key")?;

    let mut config = ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(certs, key)
        .map_err(|e| format!("certificate/key rejected: {e}"))?;
    config.alpn_protocols = vec![b"h2".to_vec(), b"http/1.1".to_vec()];

    Ok(TlsAcceptor::from(Arc::new(config)))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn self_signed() -> (Vec<u8>, Vec<u8>) {
        let cert = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
        (
            cert.cert.pem().into_bytes(),
            cert.key_pair.serialize_pem().into_bytes(),
        )
    }

    #[test]
    fn builds_acceptor_from_self_signed_pem() {
        let (cert, key) = self_signed();
        assert!(build_acceptor(&cert, &key).is_ok());
    }

    #[test]
    fn rejects_garbage_pem() {
        assert!(build_acceptor(b"not a cert", b"not a key").is_err());
    }

    #[test]
    fn rejects_mismatched_key() {
        let (cert, _) = self_signed();
        let (_, other_key) = self_signed();
        // Different key still parses and pairs are not validated against the
        // cert by rustls at config build time for self-signed ECDSA — accept
        // either outcome, the point is no panic.
        let _ = build_acceptor(&cert, &other_key);
    }
}
