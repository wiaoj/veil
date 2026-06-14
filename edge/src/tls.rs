//! TLS termination — Phase 2.2 (static) + Phase 5 (dynamic).
//!
//! Certificate material is held in memory only. A static fallback pair can
//! come from files at startup (`VEIL_TLS_CERT` / `VEIL_TLS_KEY`); zone
//! certificates provisioned by the control plane arrive inside config
//! pushes and are picked per-connection by SNI via
//! [`DynamicCertResolver`] — no listener restart.

pub mod ja3;

use std::collections::HashMap;
use std::sync::{Arc, RwLock};

use tokio_rustls::rustls::pki_types::{CertificateDer, PrivateKeyDer};
use tokio_rustls::rustls::server::{ClientHello, ResolvesServerCert};
use tokio_rustls::rustls::sign::CertifiedKey;
use tokio_rustls::rustls::ServerConfig;
use tokio_rustls::TlsAcceptor;
use tracing::{info, warn};

use crate::config::Config;

pub struct TlsSettings {
    pub listen_addr: String,
    /// Static fallback pair (`VEIL_TLS_CERT`/`VEIL_TLS_KEY`); `None` when the
    /// listener relies purely on pushed zone certificates.
    pub fallback: Option<(Vec<u8>, Vec<u8>)>,
}

/// The HTTPS listener activates when `VEIL_TLS_CERT`+`VEIL_TLS_KEY` are set
/// (static fallback material) or `VEIL_LISTEN_HTTPS` is set explicitly
/// (SNI-only mode — handshakes fail until the control plane pushes zone
/// certificates). Returns an error when TLS is configured but the files are
/// unreadable — a protection node must not silently come up plaintext-only.
pub fn settings_from_env() -> Result<Option<TlsSettings>, String> {
    let listen_env = std::env::var("VEIL_LISTEN_HTTPS").ok();
    let cert_env = std::env::var("VEIL_TLS_CERT").ok();
    let key_env = std::env::var("VEIL_TLS_KEY").ok();

    let fallback = match (cert_env, key_env) {
        (Some(cert_path), Some(key_path)) => {
            let cert_pem = std::fs::read(&cert_path)
                .map_err(|e| format!("failed to read VEIL_TLS_CERT '{cert_path}': {e}"))?;
            let key_pem = std::fs::read(&key_path)
                .map_err(|e| format!("failed to read VEIL_TLS_KEY '{key_path}': {e}"))?;
            Some((cert_pem, key_pem))
        }
        (None, None) => None,
        _ => return Err("VEIL_TLS_CERT and VEIL_TLS_KEY must both be set".into()),
    };

    if fallback.is_none() && listen_env.is_none() {
        return Ok(None);
    }

    Ok(Some(TlsSettings {
        listen_addr: listen_env.unwrap_or_else(|| "127.0.0.1:8443".to_owned()),
        fallback,
    }))
}

/// Parses a PEM pair into a rustls [`CertifiedKey`].
pub fn certified_key(cert_pem: &[u8], key_pem: &[u8]) -> Result<CertifiedKey, String> {
    let certs: Vec<CertificateDer<'static>> = rustls_pemfile::certs(&mut &cert_pem[..])
        .collect::<Result<_, _>>()
        .map_err(|e| format!("invalid certificate PEM: {e}"))?;
    if certs.is_empty() {
        return Err("certificate PEM contains no certificates".into());
    }

    let key: PrivateKeyDer<'static> = rustls_pemfile::private_key(&mut &key_pem[..])
        .map_err(|e| format!("invalid private key PEM: {e}"))?
        .ok_or("private key PEM contains no key")?;

    let signing_key = tokio_rustls::rustls::crypto::ring::sign::any_supported_type(&key)
        .map_err(|e| format!("unsupported private key: {e}"))?;

    Ok(CertifiedKey::new(certs, signing_key))
}

/// SNI certificate resolver fed by config pushes. Exact host match first,
/// then the static fallback pair (when configured).
#[derive(Debug, Default)]
pub struct DynamicCertResolver {
    fallback: Option<Arc<CertifiedKey>>,
    by_host: RwLock<HashMap<String, Arc<CertifiedKey>>>,
}

impl DynamicCertResolver {
    pub fn new(fallback: Option<Arc<CertifiedKey>>) -> Self {
        Self {
            fallback,
            by_host: RwLock::new(HashMap::new()),
        }
    }

    /// Rebuilds the SNI map from a config snapshot. Zones without TLS
    /// material are skipped; invalid material is logged and skipped — one
    /// bad certificate must not take down the others.
    pub fn update_from_config(&self, config: &Config) {
        let mut map = HashMap::new();
        for zone in &config.zones {
            let Some(tls) = &zone.tls else { continue };
            match certified_key(tls.cert_pem.as_bytes(), tls.key_pem.as_bytes()) {
                Ok(key) => {
                    let key = Arc::new(key);
                    for host in &zone.hosts {
                        if host != "*" {
                            map.insert(host.to_ascii_lowercase(), Arc::clone(&key));
                        }
                    }
                }
                Err(err) => {
                    warn!(zone = %zone.name, error = %err, "zone tls material rejected");
                }
            }
        }
        let count = map.len();
        *self.by_host.write().expect("cert resolver lock poisoned") = map;
        if count > 0 {
            info!(hosts = count, "sni certificate map updated");
        }
    }
}

impl ResolvesServerCert for DynamicCertResolver {
    fn resolve(&self, client_hello: ClientHello<'_>) -> Option<Arc<CertifiedKey>> {
        let by_host = self.by_host.read().expect("cert resolver lock poisoned");
        client_hello
            .server_name()
            .and_then(|sni| by_host.get(&sni.to_ascii_lowercase()).cloned())
            .or_else(|| self.fallback.clone())
    }
}

/// Builds an acceptor around the dynamic resolver. ALPN advertises h2 +
/// http/1.1 so the hyper auto builder negotiates HTTP/2 over TLS.
pub fn build_resolver_acceptor(resolver: Arc<DynamicCertResolver>) -> TlsAcceptor {
    let mut config = ServerConfig::builder()
        .with_no_client_auth()
        .with_cert_resolver(resolver);
    config.alpn_protocols = vec![b"h2".to_vec(), b"http/1.1".to_vec()];
    TlsAcceptor::from(Arc::new(config))
}

/// Builds an acceptor from a single in-memory PEM pair (static mode).
pub fn build_acceptor(cert_pem: &[u8], key_pem: &[u8]) -> Result<TlsAcceptor, String> {
    let key = Arc::new(certified_key(cert_pem, key_pem)?);
    Ok(build_resolver_acceptor(Arc::new(DynamicCertResolver::new(
        Some(key),
    ))))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn self_signed(host: &str) -> (Vec<u8>, Vec<u8>) {
        let cert = rcgen::generate_simple_self_signed(vec![host.into()]).unwrap();
        (
            cert.cert.pem().into_bytes(),
            cert.key_pair.serialize_pem().into_bytes(),
        )
    }

    #[test]
    fn builds_acceptor_from_self_signed_pem() {
        let (cert, key) = self_signed("localhost");
        assert!(build_acceptor(&cert, &key).is_ok());
    }

    #[test]
    fn rejects_garbage_pem() {
        assert!(certified_key(b"not a cert", b"not a key").is_err());
    }

    #[test]
    fn resolver_maps_zone_hosts_from_config() {
        let (cert, key) = self_signed("demo.example.com");
        let config = Config::from_json(&format!(
            r#"{{"zones": [{{"name": "z", "hosts": ["Demo.Example.COM"], "upstream": "http://h:1",
                "rules": [],
                "tls": {{"cert_pem": {cert:?}, "key_pem": {key:?}}}}}]}}"#,
            cert = String::from_utf8(cert).unwrap(),
            key = String::from_utf8(key).unwrap(),
        ))
        .unwrap();

        let resolver = DynamicCertResolver::new(None);
        resolver.update_from_config(&config);

        let map = resolver.by_host.read().unwrap();
        assert!(map.contains_key("demo.example.com"));
        assert_eq!(map.len(), 1);
    }

    #[test]
    fn resolver_skips_invalid_material_keeps_valid() {
        let (cert, key) = self_signed("good.example.com");
        let config = Config::from_json(&format!(
            r#"{{"zones": [
                {{"name": "bad", "hosts": ["bad.example.com"], "upstream": "http://h:1",
                  "rules": [], "tls": {{"cert_pem": "garbage", "key_pem": "garbage"}}}},
                {{"name": "good", "hosts": ["good.example.com"], "upstream": "http://h:1",
                  "rules": [], "tls": {{"cert_pem": {cert:?}, "key_pem": {key:?}}}}}
            ]}}"#,
            cert = String::from_utf8(cert).unwrap(),
            key = String::from_utf8(key).unwrap(),
        ))
        .unwrap();

        let resolver = DynamicCertResolver::new(None);
        resolver.update_from_config(&config);

        let map = resolver.by_host.read().unwrap();
        assert!(map.contains_key("good.example.com"));
        assert!(!map.contains_key("bad.example.com"));
    }

    #[test]
    fn replaced_config_drops_stale_hosts() {
        let (cert, key) = self_signed("a.example.com");
        let cert_s = String::from_utf8(cert).unwrap();
        let key_s = String::from_utf8(key).unwrap();
        let with_tls = |host: &str| {
            Config::from_json(&format!(
                r#"{{"zones": [{{"name": "z", "hosts": [{host:?}], "upstream": "http://h:1",
                    "rules": [], "tls": {{"cert_pem": {cert_s:?}, "key_pem": {key_s:?}}}}}]}}"#,
            ))
            .unwrap()
        };

        let resolver = DynamicCertResolver::new(None);
        resolver.update_from_config(&with_tls("a.example.com"));
        resolver.update_from_config(&with_tls("b.example.com"));

        let map = resolver.by_host.read().unwrap();
        assert!(!map.contains_key("a.example.com"));
        assert!(map.contains_key("b.example.com"));
    }
}
