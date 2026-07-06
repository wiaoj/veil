//! JA4 TLS client fingerprinting (FoxIO), the successor to JA3.
//!
//! JA4 is more structured — and more resilient to extension-order
//! randomisation — than JA3. For a TLS-over-TCP ClientHello it is
//! `ja4_a_ja4_b_ja4_c`:
//!
//! * **a** — `t` (TLS over TCP) + TLS version (`13`/`12`/…) + `d`/`i` (SNI
//!   present → domain, else IP) + 2-digit cipher count + 2-digit extension
//!   count + first/last char of the first ALPN value (`00` when none).
//! * **b** — first 12 hex of SHA-256 of the **sorted** cipher list (4-hex
//!   each, GREASE excluded); `000000000000` when there are no ciphers.
//! * **c** — first 12 hex of SHA-256 of the **sorted** extension list (GREASE,
//!   SNI and ALPN excluded) joined with `_` and the signature-algorithm list
//!   in original order.
//!
//! Like JA3, the ClientHello is peeked off the socket without consuming it, so
//! rustls still performs the real handshake. GREASE values (RFC 8701) are
//! excluded so the fingerprint is stable across connections.

use sha2::{Digest, Sha256};

/// A computed JA4 fingerprint string (e.g. `t13d1516h2_8daaf6152771_e5627efa2ab1`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Ja4 {
    pub value: String,
}

fn is_grease(value: u16) -> bool {
    (value & 0x0f0f) == 0x0a0a
}

fn be16(b: &[u8], pos: usize) -> Option<u16> {
    b.get(pos..pos + 2).map(|s| u16::from_be_bytes([s[0], s[1]]))
}

/// Two-character JA4 code for a TLS version value.
fn version_code(version: u16) -> &'static str {
    match version {
        0x0304 => "13",
        0x0303 => "12",
        0x0302 => "11",
        0x0301 => "10",
        0x0300 => "s3",
        0x0002 => "s2",
        _ => "00",
    }
}

/// One ALPN character: alphanumeric bytes pass through, anything else (GREASE /
/// non-printable) collapses to `9`, matching the JA4 spec's handling.
fn alpn_char(byte: u8) -> char {
    let c = byte as char;
    if c.is_ascii_alphanumeric() { c } else { '9' }
}

/// First 12 hex chars of the SHA-256 of `input`.
fn hash12(input: &str) -> String {
    let digest = Sha256::digest(input.as_bytes());
    let mut out = String::with_capacity(12);
    for byte in digest.iter().take(6) {
        out.push_str(&format!("{byte:02x}"));
    }
    out
}

fn join_hex(values: &[u16]) -> String {
    values.iter().map(|v| format!("{v:04x}")).collect::<Vec<_>>().join(",")
}

/// Parses a raw TLS record and computes the JA4 fingerprint of its
/// ClientHello. Returns `None` if the bytes are not a well-formed ClientHello.
pub fn from_tls_record(record: &[u8]) -> Option<Ja4> {
    if record.len() < 5 || record[0] != 0x16 {
        return None;
    }
    let handshake = &record[5..];
    if handshake.len() < 4 || handshake[0] != 0x01 {
        return None;
    }
    let body = &handshake[4..];

    let legacy_version = be16(body, 0)?;
    let mut pos = 2 + 32;
    let session_id_len = *body.get(pos)? as usize;
    pos += 1 + session_id_len;

    // cipher_suites
    let ciphers_len = be16(body, pos)? as usize;
    pos += 2;
    let ciphers_end = pos + ciphers_len;
    let mut ciphers = Vec::new();
    while pos + 2 <= ciphers_end {
        let c = be16(body, pos)?;
        if !is_grease(c) {
            ciphers.push(c);
        }
        pos += 2;
    }
    pos = ciphers_end;

    // compression_methods
    let compression_len = *body.get(pos)? as usize;
    pos += 1 + compression_len;

    // extensions
    let mut ext_count = 0usize;           // all non-GREASE extensions (incl. SNI/ALPN)
    let mut hash_exts = Vec::new();       // sorted set for ja4_c (SNI + ALPN excluded)
    let mut sig_algs = Vec::new();        // in original order
    let mut sni = false;
    let mut alpn_first: Option<Vec<u8>> = None;
    let mut negotiated_version = legacy_version;

    if let Some(ext_total) = be16(body, pos) {
        pos += 2;
        let ext_end = (pos + ext_total as usize).min(body.len());
        while pos + 4 <= ext_end {
            let ext_type = be16(body, pos)?;
            let ext_len = be16(body, pos + 2)? as usize;
            let data_start = pos + 4;
            let data_end = (data_start + ext_len).min(body.len());
            let data = body.get(data_start..data_end)?;

            if !is_grease(ext_type) {
                ext_count += 1;
                // SNI (0x0000) and ALPN (0x0010) count toward ext_count but are
                // excluded from the extension hash.
                if ext_type != 0x0000 && ext_type != 0x0010 {
                    hash_exts.push(ext_type);
                }
            }

            match ext_type {
                // server_name
                0x0000 => sni = true,
                // application_layer_protocol_negotiation
                0x0010 => {
                    if data.len() >= 3 {
                        // 2-byte list length, then 1-byte name length + name.
                        let name_len = data[2] as usize;
                        if 3 + name_len <= data.len() && name_len > 0 {
                            alpn_first = Some(data[3..3 + name_len].to_vec());
                        }
                    }
                }
                // supported_versions — pick the highest non-GREASE offered.
                0x002b => {
                    if let Some((&list_len, rest)) = data.split_first() {
                        let mut i = 0;
                        while i + 2 <= list_len as usize && i + 2 <= rest.len() {
                            let v = u16::from_be_bytes([rest[i], rest[i + 1]]);
                            if !is_grease(v) && v > negotiated_version {
                                negotiated_version = v;
                            }
                            i += 2;
                        }
                    }
                }
                // signature_algorithms — kept in order.
                0x000d if data.len() >= 2 => {
                    let list_len = u16::from_be_bytes([data[0], data[1]]) as usize;
                    let mut i = 2;
                    while i + 2 <= 2 + list_len && i + 2 <= data.len() {
                        let s = u16::from_be_bytes([data[i], data[i + 1]]);
                        if !is_grease(s) {
                            sig_algs.push(s);
                        }
                        i += 2;
                    }
                }
                _ => {}
            }
            pos = data_start + ext_len;
        }
    }

    let sni_char = if sni { 'd' } else { 'i' };
    let alpn = match &alpn_first {
        Some(bytes) if !bytes.is_empty() => {
            format!("{}{}", alpn_char(bytes[0]), alpn_char(bytes[bytes.len() - 1]))
        }
        _ => "00".to_owned(),
    };

    let a = format!(
        "t{}{}{:02}{:02}{}",
        version_code(negotiated_version),
        sni_char,
        ciphers.len().min(99),
        ext_count.min(99),
        alpn,
    );

    let b = if ciphers.is_empty() {
        "000000000000".to_owned()
    } else {
        let mut sorted = ciphers.clone();
        sorted.sort_unstable();
        hash12(&join_hex(&sorted))
    };

    let c = if hash_exts.is_empty() && sig_algs.is_empty() {
        "000000000000".to_owned()
    } else {
        let mut sorted = hash_exts.clone();
        sorted.sort_unstable();
        let input = if sig_algs.is_empty() {
            join_hex(&sorted)
        } else {
            format!("{}_{}", join_hex(&sorted), join_hex(&sig_algs))
        };
        hash12(&input)
    };

    Some(Ja4 { value: format!("{a}_{b}_{c}") })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn client_hello(version: u16, ciphers: &[u16], exts: &[(u16, Vec<u8>)]) -> Vec<u8> {
        let mut body = Vec::new();
        body.extend_from_slice(&version.to_be_bytes());
        body.extend_from_slice(&[0u8; 32]);
        body.push(0);
        body.extend_from_slice(&((ciphers.len() * 2) as u16).to_be_bytes());
        for c in ciphers {
            body.extend_from_slice(&c.to_be_bytes());
        }
        body.extend_from_slice(&[1, 0]);
        let mut ext_block = Vec::new();
        for (t, data) in exts {
            ext_block.extend_from_slice(&t.to_be_bytes());
            ext_block.extend_from_slice(&(data.len() as u16).to_be_bytes());
            ext_block.extend_from_slice(data);
        }
        body.extend_from_slice(&(ext_block.len() as u16).to_be_bytes());
        body.extend_from_slice(&ext_block);

        let mut handshake = vec![0x01];
        handshake.extend_from_slice(&((body.len() as u32).to_be_bytes()[1..]));
        handshake.extend_from_slice(&body);

        let mut record = vec![0x16, 0x03, 0x01];
        record.extend_from_slice(&(handshake.len() as u16).to_be_bytes());
        record.extend_from_slice(&handshake);
        record
    }

    /// ALPN extension data for a single protocol name (e.g. "h2").
    fn alpn(name: &[u8]) -> Vec<u8> {
        let mut d = vec![];
        d.extend_from_slice(&((name.len() + 1) as u16).to_be_bytes()); // list length
        d.push(name.len() as u8);
        d.extend_from_slice(name);
        d
    }

    #[test]
    fn builds_ja4_a_part_from_shape() {
        // TLS 1.2 legacy version, 2 ciphers, SNI + ALPN "h2" (2 exts total), ALPN h2.
        let record = client_hello(
            0x0303,
            &[0x1301, 0x1302],
            &[(0x0000, vec![]), (0x0010, alpn(b"h2"))],
        );
        let ja4 = from_tls_record(&record).expect("valid hello");
        // t + 12 (TLS1.2) + d (SNI) + 02 ciphers + 02 exts + h2 ALPN
        assert!(ja4.value.starts_with("t12d0202h2_"), "got {}", ja4.value);
        let parts: Vec<&str> = ja4.value.split('_').collect();
        assert_eq!(parts.len(), 3);
        assert_eq!(parts[1].len(), 12);
        assert_eq!(parts[2].len(), 12);
    }

    #[test]
    fn supported_versions_overrides_legacy_and_no_alpn_no_sni() {
        // supported_versions advertises TLS 1.3; no SNI, no ALPN.
        let sv = vec![0x02, 0x03, 0x04]; // list len 2, version 0x0304
        let record = client_hello(0x0303, &[0x1301], &[(0x002b, sv)]);
        let ja4 = from_tls_record(&record).unwrap();
        // t13 (from supported_versions) i (no SNI) 01 cipher 01 ext 00 (no ALPN)
        assert!(ja4.value.starts_with("t13i0101"), "got {}", ja4.value);
        assert!(ja4.value.contains("00_"), "no-ALPN → 00: {}", ja4.value);
    }

    #[test]
    fn empty_ciphers_hash_is_zeroes() {
        let record = client_hello(0x0303, &[], &[(0x002d, vec![0x01, 0x01])]);
        let ja4 = from_tls_record(&record).unwrap();
        let b = ja4.value.split('_').nth(1).unwrap();
        assert_eq!(b, "000000000000");
    }

    #[test]
    fn grease_excluded_from_counts() {
        // One GREASE cipher + one real; one GREASE ext + one real (non SNI/ALPN).
        let record = client_hello(0x0303, &[0x0a0a, 0x1301], &[(0x1a1a, vec![]), (0x002d, vec![0x01, 0x01])]);
        let ja4 = from_tls_record(&record).unwrap();
        // 01 cipher, 01 extension after GREASE removal.
        assert!(ja4.value.starts_with("t12i0101"), "got {}", ja4.value);
    }

    #[test]
    fn rejects_non_handshake() {
        assert!(from_tls_record(&[0x17, 0x03, 0x03, 0x00, 0x00]).is_none());
        assert!(from_tls_record(&[]).is_none());
    }
}
