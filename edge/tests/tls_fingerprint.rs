//! JA3/JA4 against a **real** ClientHello.
//!
//! The unit tests in `tls::ja3` / `tls::ja4` build their ClientHello by hand, so
//! a misreading of the TLS format would be baked into both the parser and its
//! test and still pass. This test drives a real rustls client: the handshake is
//! never completed (the listener never replies), but the ClientHello is on the
//! wire, which is all the fingerprints need. It also mirrors the production path
//! — `TcpStream::peek`, non-consuming.

use std::sync::Arc;
use std::time::Duration;

use tokio::net::{TcpListener, TcpStream};
use tokio_rustls::rustls::pki_types::ServerName;
use tokio_rustls::rustls::{ClientConfig, RootCertStore};
use tokio_rustls::TlsConnector;

/// Peeks until the first TLS record is fully buffered (loopback usually
/// delivers it in one segment, but don't race the handshake).
async fn peek_client_hello(stream: &TcpStream) -> Vec<u8> {
    let mut buf = [0u8; 4096];
    for _ in 0..100 {
        stream.readable().await.unwrap();
        let n = stream.peek(&mut buf).await.unwrap();
        if n >= 5 {
            let record_len = usize::from(u16::from_be_bytes([buf[3], buf[4]])) + 5;
            if n >= record_len {
                return buf[..n].to_vec();
            }
        }
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    panic!("no complete ClientHello arrived");
}

/// Spawns a real rustls client that sends a ClientHello to `addr` for `sni`.
fn spawn_tls_client(addr: std::net::SocketAddr, sni: &'static str) {
    tokio::spawn(async move {
        let config = ClientConfig::builder()
            .with_root_certificates(RootCertStore::empty())
            .with_no_client_auth();
        let connector = TlsConnector::from(Arc::new(config));
        let stream = TcpStream::connect(addr).await.unwrap();
        let domain = ServerName::try_from(sni).unwrap();
        // Fails (we never answer / trust nothing) — the ClientHello is already sent.
        let _ = connector.connect(domain, stream).await;
    });
}

#[tokio::test]
async fn real_client_hello_produces_a_ja3_hash() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    spawn_tls_client(addr, "example.com");

    let (stream, _) = listener.accept().await.unwrap();
    let record = peek_client_hello(&stream).await;

    let ja3 = veil_edge::tls::ja3::from_tls_record(&record)
        .expect("a real ClientHello must parse as JA3");

    assert_eq!(ja3.hash.len(), 32, "JA3 is an MD5 hex digest");
    assert!(ja3.hash.chars().all(|c| c.is_ascii_hexdigit()));
    // version,ciphers,extensions,curves,point_formats
    assert_eq!(ja3.raw.split(',').count(), 5, "JA3 string has 5 fields: {}", ja3.raw);
}

#[tokio::test]
async fn real_client_hello_produces_a_well_formed_ja4() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    spawn_tls_client(addr, "example.com");

    let (stream, _) = listener.accept().await.unwrap();
    let record = peek_client_hello(&stream).await;

    let ja4 = veil_edge::tls::ja4::from_tls_record(&record)
        .expect("a real ClientHello must parse as JA4");

    let parts: Vec<&str> = ja4.value.split('_').collect();
    assert_eq!(parts.len(), 3, "JA4 is a_b_c, got {}", ja4.value);
    assert_eq!(parts[1].len(), 12, "cipher hash is 12 hex chars: {}", ja4.value);
    assert_eq!(parts[2].len(), 12, "extension hash is 12 hex chars: {}", ja4.value);

    // a-part: t | version(2) | sni(1) | ciphers(2) | exts(2) | alpn(2)
    let a = parts[0];
    assert_eq!(a.len(), 10, "a-part is 10 chars: {a}");
    assert!(a.starts_with('t'), "TLS over TCP → 't': {a}");

    // These two assertions are the point of the test: they check the parser
    // against what a real TLS stack actually sends, not against our own fixture.
    assert_eq!(&a[1..3], "13", "rustls offers TLS 1.3 via supported_versions: {a}");
    assert_eq!(&a[3..4], "d", "the client sent SNI → 'd': {a}");

    let ciphers: u32 = a[4..6].parse().expect("cipher count is 2 digits");
    let exts: u32 = a[6..8].parse().expect("extension count is 2 digits");
    assert!(ciphers > 0, "a real ClientHello offers ciphers: {a}");
    assert!(exts > 0, "a real ClientHello offers extensions: {a}");
}

/// Plaintext bytes must never be mistaken for a ClientHello — this is what keeps
/// the HTTP listener from producing bogus fingerprints.
#[tokio::test]
async fn plain_http_bytes_produce_no_fingerprint() {
    let record = b"GET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
    assert!(veil_edge::tls::ja3::from_tls_record(record).is_none());
    assert!(veil_edge::tls::ja4::from_tls_record(record).is_none());
}
