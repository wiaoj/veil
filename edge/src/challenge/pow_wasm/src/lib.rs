//! SHA-256 Proof-of-Work solver for Veil Edge challenge pages.
//!
//! Compiled to WASM via `wasm-pack build --target web --release`.
//! Runs inside a Web Worker on the client side — the main thread stays
//! unblocked while the solver iterates.
//!
//! The solver searches for a `counter` (u64 big-endian) such that
//! `SHA256(nonce_bytes || counter_bytes)` has at least `difficulty` leading
//! zero bits. This is the canonical algorithm — the server-side verifier
//! in `challenge/pow.rs` uses identical logic.

use sha2::{Digest, Sha256};
use wasm_bindgen::prelude::*;

/// Attempt to solve the PoW puzzle in a batch of `batch_size` iterations.
///
/// Returns the hex-encoded counter if a solution is found within
/// `[start_counter .. start_counter + batch_size)`, or `JsValue::NULL`
/// if no solution was found in this batch.
///
/// The caller (Web Worker JS) loops over batches and reports progress
/// between calls so the UI can update.
#[wasm_bindgen]
pub fn solve_batch(
    nonce_hex: &str,
    difficulty: u32,
    start_counter: u64,
    batch_size: u32,
) -> JsValue {
    let nonce = hex_decode(nonce_hex);
    let end = start_counter.saturating_add(batch_size as u64);

    for counter in start_counter..end {
        if check_pow(&nonce, counter, difficulty) {
            return JsValue::from_str(&format!("{:016x}", counter));
        }
    }

    JsValue::NULL
}

/// Verify a known solution. Useful for testing from JS.
#[wasm_bindgen]
pub fn verify(nonce_hex: &str, counter_hex: &str, difficulty: u32) -> bool {
    let nonce = hex_decode(nonce_hex);
    let counter = u64::from_str_radix(counter_hex, 16).unwrap_or(u64::MAX);
    check_pow(&nonce, counter, difficulty)
}

// ── internal ──────────────────────────────────────────────────────────

fn check_pow(nonce: &[u8], counter: u64, difficulty: u32) -> bool {
    let mut hasher = Sha256::new();
    hasher.update(nonce);
    hasher.update(&counter.to_be_bytes());
    let hash = hasher.finalize();
    leading_zeros(&hash) >= difficulty
}

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

fn hex_decode(hex: &str) -> Vec<u8> {
    (0..hex.len())
        .step_by(2)
        .filter_map(|i| hex.get(i..i + 2))
        .map(|pair| u8::from_str_radix(pair, 16).unwrap_or(0))
        .collect()
}

// ── native tests (run with `cargo test`, no wasm-pack needed) ────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn leading_zeros_all_zero() {
        assert_eq!(leading_zeros(&[0, 0, 0, 0]), 32);
    }

    #[test]
    fn leading_zeros_first_bit_set() {
        assert_eq!(leading_zeros(&[0x80, 0, 0, 0]), 0);
    }

    #[test]
    fn leading_zeros_mixed() {
        // 0x00 = 8 zeros, 0x0F = 4 zeros → total 12
        assert_eq!(leading_zeros(&[0x00, 0x0F, 0xFF]), 12);
    }

    #[test]
    fn hex_decode_roundtrip() {
        let input = "deadbeef01020304";
        let bytes = hex_decode(input);
        assert_eq!(bytes, vec![0xde, 0xad, 0xbe, 0xef, 0x01, 0x02, 0x03, 0x04]);
    }

    #[test]
    fn check_pow_difficulty_zero_always_passes() {
        // Any hash has ≥ 0 leading zeros
        assert!(check_pow(b"test-nonce", 0, 0));
    }

    #[test]
    fn solve_finds_known_solution() {
        // Brute-force a solution at low difficulty and verify it
        let nonce = b"veil-test-nonce!";
        let difficulty = 8; // ≤ 256 iterations on average

        let mut found = None;
        for counter in 0..10_000u64 {
            if check_pow(nonce, counter, difficulty) {
                found = Some(counter);
                break;
            }
        }

        let counter = found.expect("should find solution at difficulty 8 within 10k iterations");
        assert!(check_pow(nonce, counter, difficulty));
        // counter - 1 should NOT be a valid solution (unless it also happens to pass)
        // Just verify the found one is valid
        assert!(check_pow(nonce, counter, difficulty));
    }
}
