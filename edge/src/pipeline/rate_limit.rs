//! In-memory sliding-window rate limiter.
//!
//! Two adjacent fixed windows: the previous window's count is weighted by the
//! remaining overlap fraction and added to the current count, approximating a
//! true sliding window without storing per-request timestamps.
//!
//! This is per-process state. The Redis-backed limiter (shared across edge
//! nodes, Phase 2.5) replaces this when multi-node deployments arrive.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

/// Counter map is capped; on overflow, entries from expired windows are evicted.
const MAX_TRACKED_KEYS: usize = 100_000;

#[derive(Debug, Default)]
pub struct RateLimiter {
    counters: Mutex<HashMap<String, Counter>>,
}

#[derive(Debug)]
struct Counter {
    window_idx: u64,
    current: u32,
    previous: u32,
}

impl RateLimiter {
    pub fn new() -> Self {
        Self::default()
    }

    /// Records a hit for `key` and returns whether it is within `limit`
    /// requests per `window_secs`.
    pub fn allow(&self, key: &str, limit: u32, window_secs: u64) -> bool {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_secs();
        self.allow_at(key, limit, window_secs, now)
    }

    fn allow_at(&self, key: &str, limit: u32, window_secs: u64, now_secs: u64) -> bool {
        let window_secs = window_secs.max(1);
        let idx = now_secs / window_secs;
        let mut counters = self.counters.lock().expect("rate limiter lock poisoned");

        if counters.len() >= MAX_TRACKED_KEYS && !counters.contains_key(key) {
            counters.retain(|_, c| c.window_idx + 1 >= idx);
        }

        let counter = counters.entry(key.to_owned()).or_insert(Counter {
            window_idx: idx,
            current: 0,
            previous: 0,
        });

        if idx == counter.window_idx + 1 {
            counter.previous = counter.current;
            counter.current = 0;
            counter.window_idx = idx;
        } else if idx != counter.window_idx {
            counter.previous = 0;
            counter.current = 0;
            counter.window_idx = idx;
        }

        counter.current += 1;
        let elapsed_frac = (now_secs % window_secs) as f64 / window_secs as f64;
        let estimate = counter.previous as f64 * (1.0 - elapsed_frac) + counter.current as f64;
        estimate <= limit as f64
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn allows_up_to_limit_within_window() {
        let limiter = RateLimiter::new();
        assert!(limiter.allow_at("k", 3, 60, 600));
        assert!(limiter.allow_at("k", 3, 60, 601));
        assert!(limiter.allow_at("k", 3, 60, 602));
        assert!(!limiter.allow_at("k", 3, 60, 603));
    }

    #[test]
    fn keys_are_isolated() {
        let limiter = RateLimiter::new();
        assert!(limiter.allow_at("a", 1, 60, 600));
        assert!(!limiter.allow_at("a", 1, 60, 601));
        assert!(limiter.allow_at("b", 1, 60, 601));
    }

    #[test]
    fn previous_window_weight_decays_over_time() {
        let limiter = RateLimiter::new();
        // Fill the window starting at t=600 (window 10 for window_secs=60).
        for t in 600..603 {
            limiter.allow_at("k", 3, 60, t);
        }
        // Start of next window: previous count still carries ~full weight.
        assert!(!limiter.allow_at("k", 3, 60, 660));
        // Near the end of the next window the old window has decayed
        // (3 * 0.05 + 2 = 2.15 <= 3).
        assert!(limiter.allow_at("k", 3, 60, 717));
    }

    #[test]
    fn stale_window_resets_counts() {
        let limiter = RateLimiter::new();
        assert!(limiter.allow_at("k", 1, 60, 600));
        assert!(!limiter.allow_at("k", 1, 60, 601));
        // Two windows later both counters are stale.
        assert!(limiter.allow_at("k", 1, 60, 740));
    }
}
