//! Server-side Proof-of-Work generation and verification.
//!
//! The algorithm is identical to the WASM solver (`pow_wasm/`):
//!   SHA256(nonce_bytes || counter_be_bytes)
//! must have at least `difficulty` leading zero bits.
//!
//! This module only verifies solutions — solving happens client-side.

use sha2::{Digest, Sha256};

/// Default PoW difficulty: 20 leading zero bits.
/// At this level a modern browser (WASM) solves in ~50ms.
pub const DEFAULT_DIFFICULTY: u32 = 20;

/// Generate a cryptographically random 16-byte nonce.
pub fn generate_nonce() -> [u8; 16] {
    let mut buf = [0u8; 16];
    getrandom::fill(&mut buf).expect("getrandom failed");
    buf
}

/// Verify that `SHA256(nonce || counter.to_be_bytes())` has at least
/// `difficulty` leading zero bits.
pub fn verify_pow(nonce: &[u8], counter: u64, difficulty: u32) -> bool {
    let mut hasher = Sha256::new();
    hasher.update(nonce);
    hasher.update(counter.to_be_bytes());
    let hash = hasher.finalize();
    leading_zeros(&hash) >= difficulty
}

/// Count the number of leading zero bits in a byte slice.
fn leading_zeros(data: &[u8]) -> u32 {
    let mut count = 0u32;
    for &byte in data {
        if byte == 0 {
            count += 8;
        } else {
            count += byte.leading_zeros();
            break;
        }
    }
    count
}

/// Encode bytes as lowercase hex string.
pub fn to_hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// Decode a hex string to bytes. Returns `None` on invalid hex.
pub fn from_hex(hex: &str) -> Option<Vec<u8>> {
    if !hex.len().is_multiple_of(2) {
        return None;
    }
    (0..hex.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&hex[i..i + 2], 16).ok())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generate_nonce_is_16_bytes() {
        let n = generate_nonce();
        assert_eq!(n.len(), 16);
    }

    #[test]
    fn generate_nonce_is_random() {
        let a = generate_nonce();
        let b = generate_nonce();
        assert_ne!(a, b, "two consecutive nonces should differ");
    }

    #[test]
    fn verify_pow_difficulty_zero_always_passes() {
        assert!(verify_pow(b"anything", 0, 0));
    }

    #[test]
    fn verify_pow_brute_force_low_difficulty() {
        let nonce = generate_nonce();
        let difficulty = 8;
        // At difficulty 8, expected ~256 iterations
        let counter = (0..100_000u64)
            .find(|&c| verify_pow(&nonce, c, difficulty))
            .expect("should find solution at difficulty 8");

        assert!(verify_pow(&nonce, counter, difficulty));
        // Verify wrong counter doesn't pass (very unlikely to also pass)
        assert!(!verify_pow(&nonce, u64::MAX, difficulty));
    }

    #[test]
    fn hex_roundtrip() {
        let original = [0xDE, 0xAD, 0xBE, 0xEF];
        let hex = to_hex(&original);
        assert_eq!(hex, "deadbeef");
        assert_eq!(from_hex(&hex).unwrap(), original);
    }

    #[test]
    fn from_hex_rejects_odd_length() {
        assert!(from_hex("abc").is_none());
    }

    #[test]
    fn leading_zeros_exhaustive() {
        assert_eq!(leading_zeros(&[0x00, 0x00, 0x01]), 23);
        assert_eq!(leading_zeros(&[0x00, 0x80]), 8);
        assert_eq!(leading_zeros(&[0x40]), 1);
        assert_eq!(leading_zeros(&[0xFF]), 0);
        assert_eq!(leading_zeros(&[]), 0);
    }
}
