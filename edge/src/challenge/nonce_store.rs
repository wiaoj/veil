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
    /// Insert a nonce with the PoW difficulty bound to it. Returns `true` if
    /// it was freshly inserted, `false` if it already existed (replay).
    fn insert(&self, nonce: &str, difficulty: u32) -> bool;

    /// Remove a nonce after successful verification. Returns `true` if it
    /// existed (and was removed).
    fn remove(&self, nonce: &str) -> bool;

    /// Returns the difficulty bound to a currently-stored nonce, if any.
    /// Binding the difficulty to the nonce stops a client from solving at a
    /// lower difficulty than the one it was issued.
    fn difficulty(&self, nonce: &str) -> Option<u32>;
}

// ── in-memory impl ───────────────────────────────────────────────────

pub struct InMemoryNonceStore {
    inner: Mutex<NonceMap>,
    ttl: Duration,
}

struct NonceEntry {
    expires_at: Instant,
    difficulty: u32,
}

struct NonceMap {
    entries: HashMap<String, NonceEntry>,
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
            map.entries.retain(|_, entry| entry.expires_at > now);
        }
    }
}

impl NonceStore for InMemoryNonceStore {
    fn insert(&self, nonce: &str, difficulty: u32) -> bool {
        let mut map = self.inner.lock().expect("nonce store poisoned");
        Self::maybe_cleanup(&mut map);

        let now = Instant::now();
        // If already present and not expired, it's a replay
        if let Some(existing) = map.entries.get(nonce)
            && existing.expires_at > now {
                return false; // duplicate
            }
        map.entries.insert(nonce.to_owned(), NonceEntry {
            expires_at: now + self.ttl,
            difficulty,
        });
        true
    }

    fn remove(&self, nonce: &str) -> bool {
        let mut map = self.inner.lock().expect("nonce store poisoned");
        map.entries.remove(nonce).is_some()
    }

    fn difficulty(&self, nonce: &str) -> Option<u32> {
        let map = self.inner.lock().expect("nonce store poisoned");
        map.entries
            .get(nonce)
            .filter(|entry| entry.expires_at > Instant::now())
            .map(|entry| entry.difficulty)
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
        assert!(s.insert("abc123", 20));
    }

    #[test]
    fn duplicate_insert_returns_false() {
        let s = store();
        assert!(s.insert("abc123", 20));
        assert!(!s.insert("abc123", 20), "second insert should be rejected");
    }

    #[test]
    fn remove_existing_returns_true() {
        let s = store();
        s.insert("abc123", 20);
        assert!(s.remove("abc123"));
    }

    #[test]
    fn remove_missing_returns_false() {
        let s = store();
        assert!(!s.remove("nonexistent"));
    }

    #[test]
    fn difficulty_tracks_insertion() {
        let s = store();
        assert_eq!(s.difficulty("abc123"), None);
        s.insert("abc123", 22);
        assert_eq!(s.difficulty("abc123"), Some(22));
        s.remove("abc123");
        assert_eq!(s.difficulty("abc123"), None);
    }

    #[test]
    fn expired_nonce_can_be_reinserted() {
        let s = InMemoryNonceStore::new(Duration::from_millis(1));
        s.insert("abc123", 20);
        std::thread::sleep(Duration::from_millis(10));
        assert!(
            s.insert("abc123", 20),
            "expired nonce should be accepted as fresh"
        );
    }
}
