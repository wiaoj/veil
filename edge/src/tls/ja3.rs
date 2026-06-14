//! JA3 TLS client fingerprinting.
//!
//! JA3 hashes the shape of the TLS ClientHello — protocol version, offered
//! cipher suites, extensions, supported groups (elliptic curves) and EC point
//! formats — into a stable MD5. Different client stacks (browsers, curl, Go,
//! bot frameworks) produce characteristic fingerprints, so JA3 is a far
//! stronger automated-client signal than the User-Agent header alone.
//!
//! The edge captures the ClientHello by peeking the first TLS record off the
//! socket (without consuming it, so rustls still does the real handshake),
//! then parses it here. GREASE values (RFC 8701) are excluded so the
//! fingerprint stays stable across connections from the same client.

use md5::{Digest, Md5};

/// A computed JA3 fingerprint: the canonical string and its MD5 hex hash.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Ja3 {
    pub raw: String,
    pub hash: String,
}

/// GREASE values follow the pattern `0x?a?a` (RFC 8701) and must be excluded.
fn is_grease(value: u16) -> bool {
    (value & 0x0f0f) == 0x0a0a
}

/// Reads a big-endian u16 at `pos`, bounds-checked.
fn be16(b: &[u8], pos: usize) -> Option<u16> {
    b.get(pos..pos + 2).map(|s| u16::from_be_bytes([s[0], s[1]]))
}

/// Parses a raw TLS record (starting at the record header) and computes the
/// JA3 fingerprint of its ClientHello. Returns `None` if the bytes are not a
/// well-formed TLS handshake ClientHello.
pub fn from_tls_record(record: &[u8]) -> Option<Ja3> {
    // TLS record: type(1)=22 handshake, version(2), length(2), then payload.
    if record.len() < 5 || record[0] != 0x16 {
        return None;
    }
    let handshake = &record[5..];

    // Handshake: msg_type(1)=1 ClientHello, length(3), then body.
    if handshake.len() < 4 || handshake[0] != 0x01 {
        return None;
    }
    let body = &handshake[4..];

    // client_version(2), random(32), session_id_len(1) + session_id.
    let version = be16(body, 0)?;
    let mut pos = 2 + 32;
    let session_id_len = *body.get(pos)? as usize;
    pos += 1 + session_id_len;

    // cipher_suites: len(2) then 2 bytes each.
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

    // compression_methods: len(1) then bytes.
    let compression_len = *body.get(pos)? as usize;
    pos += 1 + compression_len;

    // extensions: len(2), then type(2)+len(2)+data each.
    let mut extensions = Vec::new();
    let mut curves = Vec::new();
    let mut point_formats = Vec::new();
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
                extensions.push(ext_type);
            }

            match ext_type {
                // supported_groups (elliptic curves)
                0x000a => {
                    if data.len() >= 2 {
                        let list_len = u16::from_be_bytes([data[0], data[1]]) as usize;
                        let mut i = 2;
                        while i + 2 <= 2 + list_len && i + 2 <= data.len() {
                            let g = u16::from_be_bytes([data[i], data[i + 1]]);
                            if !is_grease(g) {
                                curves.push(g);
                            }
                            i += 2;
                        }
                    }
                }
                // ec_point_formats
                0x000b => {
                    if let Some((&list_len, rest)) = data.split_first() {
                        for &f in rest.iter().take(list_len as usize) {
                            point_formats.push(f as u16);
                        }
                    }
                }
                _ => {}
            }
            pos = data_start + ext_len;
        }
    }

    let raw = format!(
        "{},{},{},{},{}",
        version,
        join_dash(&ciphers),
        join_dash(&extensions),
        join_dash(&curves),
        join_dash(&point_formats),
    );

    let hash = format!("{:x}", Md5::digest(raw.as_bytes()));
    Some(Ja3 { raw, hash })
}

fn join_dash(values: &[u16]) -> String {
    values.iter().map(u16::to_string).collect::<Vec<_>>().join("-")
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Builds a minimal but well-formed ClientHello record from the given
    /// ciphers / extensions for deterministic JA3-string assertions.
    fn client_hello(version: u16, ciphers: &[u16], exts: &[(u16, Vec<u8>)]) -> Vec<u8> {
        let mut body = Vec::new();
        body.extend_from_slice(&version.to_be_bytes());
        body.extend_from_slice(&[0u8; 32]); // random
        body.push(0); // session_id_len
        body.extend_from_slice(&((ciphers.len() * 2) as u16).to_be_bytes());
        for c in ciphers {
            body.extend_from_slice(&c.to_be_bytes());
        }
        body.extend_from_slice(&[1, 0]); // 1 compression method: null
        let mut ext_block = Vec::new();
        for (t, data) in exts {
            ext_block.extend_from_slice(&t.to_be_bytes());
            ext_block.extend_from_slice(&(data.len() as u16).to_be_bytes());
            ext_block.extend_from_slice(data);
        }
        body.extend_from_slice(&(ext_block.len() as u16).to_be_bytes());
        body.extend_from_slice(&ext_block);

        let mut handshake = vec![0x01];
        handshake.extend_from_slice(&((body.len() as u32).to_be_bytes()[1..])); // 3-byte len
        handshake.extend_from_slice(&body);

        let mut record = vec![0x16, 0x03, 0x01];
        record.extend_from_slice(&(handshake.len() as u16).to_be_bytes());
        record.extend_from_slice(&handshake);
        record
    }

    #[test]
    fn parses_and_builds_ja3_string() {
        // supported_groups (0x000a) with curves 29, 23; ec_point_formats (0x000b) [0].
        let curves = {
            let mut d = vec![0x00, 0x04];
            d.extend_from_slice(&29u16.to_be_bytes());
            d.extend_from_slice(&23u16.to_be_bytes());
            d
        };
        let record = client_hello(
            0x0303,
            &[0x1301, 0x1302],
            &[(0x000a, curves), (0x000b, vec![0x01, 0x00])],
        );
        let ja3 = from_tls_record(&record).expect("valid client hello");
        assert_eq!(ja3.raw, "771,4865-4866,10-11,29-23,0");
        assert_eq!(ja3.hash.len(), 32);
    }

    #[test]
    fn excludes_grease_values() {
        let record = client_hello(0x0303, &[0x0a0a, 0x1301], &[(0x1a1a, vec![]), (0x0017, vec![])]);
        let ja3 = from_tls_record(&record).unwrap();
        // GREASE cipher 0x0a0a and GREASE extension 0x1a1a are dropped.
        assert_eq!(ja3.raw, "771,4865,23,,");
    }

    #[test]
    fn rejects_non_handshake() {
        assert!(from_tls_record(&[0x17, 0x03, 0x03, 0x00, 0x00]).is_none());
        assert!(from_tls_record(&[]).is_none());
    }
}
