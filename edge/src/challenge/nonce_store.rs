//! Nonce lifecycle management for PoW challenge replay protection.
//!
//! The `NonceStore` trait is an interface so the backing can be swapped from
//! in-memory (current) to Redis (Phase 4.2) without touching the challenge
//! engine. The in-memory implementation uses a `Mutex<HashMap>` with TTL-based
//! expiry and periodic cleanup.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

// ── trait ─────────────────────────────────────────────────────────────

/// Abstraction over nonce storage.  Implementations must be thread-safe.
pub trait NonceStore: Send + Sync {
    /// Insert a nonce. Returns `true` if it was freshly inserted, `false` if
    /// it already existed (replay).
    fn insert(&self, nonce: &str) -> bool;

    /// Remove a nonce after successful verification. Returns `true` if it
    /// existed (and was removed).
    fn remove(&self, nonce: &str) -> bool;

    /// Check whether a nonce is currently stored.
    fn contains(&self, nonce: &str) -> bool;
}

// ── in-memory impl ───────────────────────────────────────────────────

pub struct InMemoryNonceStore {
    inner: Mutex<NonceMap>,
    ttl: Duration,
}

struct NonceMap {
    entries: HashMap<String, Instant>,
    /// Cleanup every N inserts to avoid unbounded growth.
    ops_since_cleanup: u32,
}

const CLEANUP_INTERVAL: u32 = 256;

impl InMemoryNonceStore {
    pub fn new(ttl: Duration) -> Self {
        Self {
            inner: Mutex::new(NonceMap {
                entries: HashMap::new(),
                ops_since_cleanup: 0,
            }),
            ttl,
        }
    }

    fn maybe_cleanup(map: &mut NonceMap) {
        map.ops_since_cleanup += 1;
        if map.ops_since_cleanup >= CLEANUP_INTERVAL {
            map.ops_since_cleanup = 0;
            let now = Instant::now();
            map.entries.retain(|_, &mut expires| expires > now);
        }
    }
}

impl NonceStore for InMemoryNonceStore {
    fn insert(&self, nonce: &str) -> bool {
        let mut map = self.inner.lock().expect("nonce store poisoned");
        Self::maybe_cleanup(&mut map);

        let expires_at = Instant::now() + self.ttl;
        // If already present and not expired, it's a replay
        if let Some(&existing) = map.entries.get(nonce)
            && existing > Instant::now() {
                return false; // duplicate
            }
        map.entries.insert(nonce.to_owned(), expires_at);
        true
    }

    fn remove(&self, nonce: &str) -> bool {
        let mut map = self.inner.lock().expect("nonce store poisoned");
        map.entries.remove(nonce).is_some()
    }

    fn contains(&self, nonce: &str) -> bool {
        let map = self.inner.lock().expect("nonce store poisoned");
        map.entries
            .get(nonce)
            .is_some_and(|&expires| expires > Instant::now())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn store() -> InMemoryNonceStore {
        InMemoryNonceStore::new(Duration::from_secs(60))
    }

    #[test]
    fn insert_fresh_nonce_returns_true() {
        let s = store();
        assert!(s.insert("abc123"));
    }

    #[test]
    fn duplicate_insert_returns_false() {
        let s = store();
        assert!(s.insert("abc123"));
        assert!(!s.insert("abc123"), "second insert should be rejected");
    }

    #[test]
    fn remove_existing_returns_true() {
        let s = store();
        s.insert("abc123");
        assert!(s.remove("abc123"));
    }

    #[test]
    fn remove_missing_returns_false() {
        let s = store();
        assert!(!s.remove("nonexistent"));
    }

    #[test]
    fn contains_tracks_insertion() {
        let s = store();
        assert!(!s.contains("abc123"));
        s.insert("abc123");
        assert!(s.contains("abc123"));
        s.remove("abc123");
        assert!(!s.contains("abc123"));
    }

    #[test]
    fn expired_nonce_can_be_reinserted() {
        let s = InMemoryNonceStore::new(Duration::from_millis(1));
        s.insert("abc123");
        std::thread::sleep(Duration::from_millis(10));
        assert!(
            s.insert("abc123"),
            "expired nonce should be accepted as fresh"
        );
    }
}
