//! Rate limiting — in-memory (per-process) and Redis-backed (shared across
//! edge nodes).
//!
//! Both implement the same approximate sliding window: two adjacent fixed
//! windows, where the previous window's count is weighted by the remaining
//! overlap fraction and added to the current count. This approximates a true
//! sliding window without storing per-request timestamps.
//!
//! The in-memory limiter is per-process state — correct only for a single
//! node. The Redis limiter (Phase 2.5) shares counters across every edge in
//! the fleet, so a per-IP limit holds no matter which node a request lands on.
//! It is selected via `VEIL_RATELIMIT_REDIS_URL`; without it the node falls
//! back to in-memory. Redis errors fail open (the request is allowed) so a
//! cache outage degrades rate limiting rather than dropping all traffic.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use redis::aio::ConnectionManager;
use redis::Script;
use tracing::{debug, info, warn};

/// Counter map is capped; on overflow, entries from expired windows are evicted.
const MAX_TRACKED_KEYS: usize = 100_000;

/// Env var selecting the shared Redis counter store. Unset → in-memory.
const REDIS_URL_ENV: &str = "VEIL_RATELIMIT_REDIS_URL";

/// Rate limiter backend. The in-memory variant is always available; the Redis
/// variant shares counters across the fleet.
pub enum RateLimiter {
    InMemory(InMemoryLimiter),
    // Boxed: the Redis variant is far larger than the in-memory one and this
    // enum lives once per process — an extra deref on the (network-bound)
    // Redis path is free next to the round-trip it precedes.
    Redis(Box<RedisLimiter>),
}

impl RateLimiter {
    /// In-memory limiter — per-process counters. Used as the fallback and by
    /// tests.
    pub fn in_memory() -> Self {
        RateLimiter::InMemory(InMemoryLimiter::new())
    }

    /// Builds the limiter from the environment: a working Redis connection if
    /// `VEIL_RATELIMIT_REDIS_URL` is set and reachable, otherwise in-memory.
    /// A misconfigured or unreachable Redis at startup falls back to in-memory
    /// with a warning rather than failing the node.
    pub async fn from_env() -> Self {
        let Ok(url) = std::env::var(REDIS_URL_ENV) else {
            return Self::in_memory();
        };
        if url.is_empty() {
            return Self::in_memory();
        }
        match RedisLimiter::connect(&url).await {
            Ok(limiter) => {
                info!(%url, "rate limiting uses shared Redis counters");
                RateLimiter::Redis(Box::new(limiter))
            }
            Err(err) => {
                warn!(
                    %url,
                    error = %err,
                    "{REDIS_URL_ENV} set but Redis is unreachable; using per-process counters"
                );
                Self::in_memory()
            }
        }
    }

    /// Records a hit for `key` and returns whether it is within `limit`
    /// requests per `window_secs`. Redis failures fail open (return `true`).
    pub async fn allow(&self, key: &str, limit: u32, window_secs: u64) -> bool {
        match self {
            RateLimiter::InMemory(l) => l.allow(key, limit, window_secs),
            RateLimiter::Redis(l) => l.allow(key, limit, window_secs).await,
        }
    }
}

impl Default for RateLimiter {
    fn default() -> Self {
        Self::in_memory()
    }
}

/// Per-process sliding-window limiter.
#[derive(Debug, Default)]
pub struct InMemoryLimiter {
    counters: Mutex<HashMap<String, Counter>>,
}

#[derive(Debug)]
struct Counter {
    window_idx: u64,
    current: u32,
    previous: u32,
}

impl InMemoryLimiter {
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

/// Atomic sliding-window limiter over a shared Redis store.
///
/// The window math runs server-side in a single Lua script so the
/// increment, expiry and decision are one round-trip and one atomic step —
/// concurrent requests across nodes can never interleave a check between
/// another's increment.
pub struct RedisLimiter {
    conn: ConnectionManager,
    script: Script,
}

/// Mirrors [`InMemoryLimiter::allow_at`] server-side.
///
/// `KEYS[1]` is the per-rule base key; `ARGV` carries the limit, window and
/// current unix time. Counters live in two per-window keys (`base:idx`); each
/// gets a TTL of two windows so they self-evict. Returns 1 (allow) / 0 (deny).
const SLIDING_WINDOW_LUA: &str = r"
local limit = tonumber(ARGV[1])
local window = tonumber(ARGV[2])
local now = tonumber(ARGV[3])
if window < 1 then window = 1 end
local idx = math.floor(now / window)
local cur_key = KEYS[1] .. ':' .. idx
local prev_key = KEYS[1] .. ':' .. (idx - 1)
local current = redis.call('INCR', cur_key)
if current == 1 then
  redis.call('EXPIRE', cur_key, window * 2)
end
local previous = tonumber(redis.call('GET', prev_key) or '0')
local elapsed = (now % window) / window
local estimate = previous * (1 - elapsed) + current
if estimate <= limit then return 1 else return 0 end
";

impl RedisLimiter {
    /// Opens a multiplexed, auto-reconnecting connection to `url`. The initial
    /// connection is established eagerly so a bad URL surfaces at startup.
    pub async fn connect(url: &str) -> redis::RedisResult<Self> {
        let client = redis::Client::open(url)?;
        let conn = ConnectionManager::new(client).await?;
        Ok(Self { conn, script: Script::new(SLIDING_WINDOW_LUA) })
    }

    async fn allow(&self, key: &str, limit: u32, window_secs: u64) -> bool {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_secs();
        let mut conn = self.conn.clone();
        let result: redis::RedisResult<i64> = self
            .script
            .key(key)
            .arg(limit)
            .arg(window_secs)
            .arg(now)
            .invoke_async(&mut conn)
            .await;
        match result {
            Ok(allowed) => allowed == 1,
            Err(err) => {
                // Fail open: a Redis outage must not turn every rate-limit
                // rule into a blanket block.
                debug!(key, error = %err, "redis rate-limit check failed; allowing");
                true
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn allows_up_to_limit_within_window() {
        let limiter = InMemoryLimiter::new();
        assert!(limiter.allow_at("k", 3, 60, 600));
        assert!(limiter.allow_at("k", 3, 60, 601));
        assert!(limiter.allow_at("k", 3, 60, 602));
        assert!(!limiter.allow_at("k", 3, 60, 603));
    }

    #[test]
    fn keys_are_isolated() {
        let limiter = InMemoryLimiter::new();
        assert!(limiter.allow_at("a", 1, 60, 600));
        assert!(!limiter.allow_at("a", 1, 60, 601));
        assert!(limiter.allow_at("b", 1, 60, 601));
    }

    #[test]
    fn previous_window_weight_decays_over_time() {
        let limiter = InMemoryLimiter::new();
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
        let limiter = InMemoryLimiter::new();
        assert!(limiter.allow_at("k", 1, 60, 600));
        assert!(!limiter.allow_at("k", 1, 60, 601));
        // Two windows later both counters are stale.
        assert!(limiter.allow_at("k", 1, 60, 740));
    }

    /// Exercises the Lua sliding-window script against a real Redis. Ignored
    /// by default; run with a Redis at `VEIL_TEST_REDIS_URL`, e.g.
    /// `VEIL_TEST_REDIS_URL=redis://127.0.0.1:6390 cargo test redis_ -- --ignored`.
    #[tokio::test]
    #[ignore = "requires a running Redis (VEIL_TEST_REDIS_URL)"]
    async fn redis_allows_up_to_limit_then_blocks() {
        let url = std::env::var("VEIL_TEST_REDIS_URL").expect("VEIL_TEST_REDIS_URL");
        let limiter = RedisLimiter::connect(&url).await.expect("connect");
        // Unique key per run so repeated test runs don't collide on counters.
        let key = format!("test:rl:{}", std::process::id());
        assert!(limiter.allow(&key, 3, 60).await);
        assert!(limiter.allow(&key, 3, 60).await);
        assert!(limiter.allow(&key, 3, 60).await);
        assert!(!limiter.allow(&key, 3, 60).await);
        // A different key keeps its own counter.
        let other = format!("{key}:other");
        assert!(limiter.allow(&other, 1, 60).await);
        assert!(!limiter.allow(&other, 1, 60).await);
    }
}
