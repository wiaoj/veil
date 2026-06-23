//! Request log emission — Phase 2.6.
//!
//! Every proxied request produces a [`LogRecord`] that is pushed into an
//! in-memory ring buffer. A background shipper task drains the buffer and
//! POSTs batches to `Veil.Analytics` (`{VEIL_ANALYTICS_URL}/ingest`).
//!
//! The pipeline is strictly fire-and-forget: the buffer drops the oldest
//! records when full and the shipper drops batches the ingest endpoint
//! rejects or cannot receive. Analytics must never back-pressure the
//! data plane.

pub mod shipper;

use std::collections::VecDeque;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;
use tokio::sync::Notify;

/// Maximum records held in memory; beyond this the oldest are dropped.
pub const BUFFER_CAPACITY: usize = 100_000;

/// Buffered record count that triggers an early flush (before the 500ms tick).
pub const FLUSH_THRESHOLD: usize = 1_000;

/// One proxied request, in the shape `Veil.Analytics` ingests.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct LogRecord {
    /// Unix epoch milliseconds at request arrival.
    pub ts_ms: u64,
    /// Zone name, or `"-"` when no zone matched the host.
    pub zone: String,
    pub host: String,
    pub method: String,
    pub path: String,
    pub status: u16,
    pub verdict: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub rule_id: Option<String>,
    pub client_ip: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub user_agent: Option<String>,
    pub duration_ms: u64,
    /// Autonomous System Number from GeoIP enrichment (`None` when no ASN MMDB
    /// or the lookup missed). Lets the analytics layer detect distributed
    /// floods concentrated in one network.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub asn: Option<u32>,
}

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// Bounded drop-oldest queue between the request path and the shipper task.
///
/// `push` runs on the hot path: it takes the lock for a constant-time
/// `VecDeque` operation only. Lost records are counted, never silently
/// discarded.
pub struct LogBuffer {
    queue: Mutex<VecDeque<LogRecord>>,
    notify: Notify,
    dropped: AtomicU64,
    capacity: usize,
    flush_threshold: usize,
}

impl LogBuffer {
    pub fn new(capacity: usize, flush_threshold: usize) -> Self {
        Self {
            queue: Mutex::new(VecDeque::with_capacity(capacity.min(FLUSH_THRESHOLD * 4))),
            notify: Notify::new(),
            dropped: AtomicU64::new(0),
            capacity,
            flush_threshold,
        }
    }

    pub fn push(&self, record: LogRecord) {
        let over_threshold;
        {
            let mut queue = self.queue.lock().expect("log buffer lock poisoned");
            if queue.len() == self.capacity {
                queue.pop_front();
                self.dropped.fetch_add(1, Ordering::Relaxed);
            }
            queue.push_back(record);
            over_threshold = queue.len() >= self.flush_threshold;
        }
        if over_threshold {
            self.notify.notify_one();
        }
    }

    /// Removes and returns up to `max` records, oldest first.
    pub fn drain(&self, max: usize) -> Vec<LogRecord> {
        let mut queue = self.queue.lock().expect("log buffer lock poisoned");
        let take = queue.len().min(max);
        queue.drain(..take).collect()
    }

    /// Resolves when the buffer crosses the flush threshold.
    pub async fn wait_for_flush_signal(&self) {
        self.notify.notified().await;
    }

    pub fn len(&self) -> usize {
        self.queue.lock().expect("log buffer lock poisoned").len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Total records lost to capacity overflow since startup.
    pub fn dropped(&self) -> u64 {
        self.dropped.load(Ordering::Relaxed)
    }
}

impl Default for LogBuffer {
    fn default() -> Self {
        Self::new(BUFFER_CAPACITY, FLUSH_THRESHOLD)
    }
}

/// `Some` when `VEIL_ANALYTICS_URL` is configured — emission is opt-in so a
/// node without an analytics endpoint pays nothing on the request path.
pub fn buffer_from_env() -> Option<Arc<LogBuffer>> {
    std::env::var("VEIL_ANALYTICS_URL")
        .is_ok()
        .then(|| Arc::new(LogBuffer::default()))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn record(path: &str) -> LogRecord {
        LogRecord {
            ts_ms: 1_770_000_000_000,
            zone: "example".to_owned(),
            host: "example.com".to_owned(),
            method: "GET".to_owned(),
            path: path.to_owned(),
            status: 200,
            verdict: "allow",
            rule_id: None,
            client_ip: "192.0.2.1".to_owned(),
            user_agent: Some("test".to_owned()),
            duration_ms: 3,
            asn: None,
        }
    }

    #[test]
    fn drops_oldest_when_full() {
        let buffer = LogBuffer::new(2, 100);
        buffer.push(record("/a"));
        buffer.push(record("/b"));
        buffer.push(record("/c"));

        assert_eq!(buffer.dropped(), 1);
        let drained = buffer.drain(10);
        assert_eq!(drained.len(), 2);
        assert_eq!(drained[0].path, "/b");
        assert_eq!(drained[1].path, "/c");
    }

    #[test]
    fn drain_respects_max_and_preserves_order() {
        let buffer = LogBuffer::new(10, 100);
        for i in 0..5 {
            buffer.push(record(&format!("/{i}")));
        }

        let first = buffer.drain(3);
        assert_eq!(
            first.iter().map(|r| r.path.as_str()).collect::<Vec<_>>(),
            ["/0", "/1", "/2"]
        );
        assert_eq!(buffer.len(), 2);

        let rest = buffer.drain(3);
        assert_eq!(rest.len(), 2);
        assert!(buffer.is_empty());
    }

    #[tokio::test]
    async fn threshold_push_wakes_waiter() {
        let buffer = Arc::new(LogBuffer::new(10, 2));
        let waiter = Arc::clone(&buffer);
        let waited = tokio::spawn(async move { waiter.wait_for_flush_signal().await });

        buffer.push(record("/a"));
        buffer.push(record("/b")); // crosses threshold → notify

        tokio::time::timeout(std::time::Duration::from_secs(1), waited)
            .await
            .expect("flush signal not raised")
            .unwrap();
    }

    #[test]
    fn serializes_in_ingest_shape() {
        let json = serde_json::to_value(record("/a")).unwrap();
        assert_eq!(json["ts_ms"], 1_770_000_000_000u64);
        assert_eq!(json["zone"], "example");
        assert_eq!(json["status"], 200);
        assert_eq!(json["verdict"], "allow");
        assert_eq!(json["client_ip"], "192.0.2.1");
        assert_eq!(json["duration_ms"], 3);
        // absent optionals are omitted, not null
        assert!(json.get("rule_id").is_none());
    }
}
