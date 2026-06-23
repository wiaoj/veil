//! Prometheus metrics — dependency-free.
//!
//! A fixed set of counters and one latency histogram, rendered to the
//! Prometheus text exposition format on `GET /metrics`. Everything is plain
//! atomics: the hot path only does a couple of `fetch_add`s, no allocation
//! and no locking.

use std::sync::atomic::{AtomicU64, Ordering};

/// Verdict labels mirrored from the request pipeline. Order is the array
/// index — keep in sync with [`verdict_index`].
const VERDICTS: [&str; 7] = [
    "allow",
    "block",
    "challenge",
    "challenge_pass",
    "rate_limited",
    "no_zone",
    "other",
];

/// Upstream failure reasons mirrored from `response::GatewayReason::ref_code`.
/// Order is the array index — keep in sync with [`upstream_reason_index`].
/// (`edge_not_ready` is a request verdict, not an upstream error, so it is not
/// here.) Unrecognised reasons fall into the trailing `other`.
const UPSTREAM_REASONS: [&str; 8] = [
    "web_server_down",
    "origin_unreachable",
    "origin_ssl_handshake",
    "origin_ssl_invalid",
    "origin_timeout",
    "origin_bad_response",
    "bad_gateway",
    "other",
];

/// Upper bounds (seconds) for the request-duration histogram. The implicit
/// `+Inf` bucket is `request_count`.
const LATENCY_BUCKETS: [f64; 11] = [
    0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0,
];

#[derive(Debug, Default)]
pub struct Metrics {
    /// Per-verdict request counter, indexed like [`VERDICTS`].
    requests: [AtomicU64; VERDICTS.len()],
    /// Cumulative-by-construction histogram bucket counts (le bounds).
    latency_buckets: [AtomicU64; LATENCY_BUCKETS.len()],
    /// Total observations (the +Inf bucket).
    latency_count: AtomicU64,
    /// Sum of observed durations, in microseconds (integer to stay atomic).
    latency_sum_micros: AtomicU64,
    /// Upstream forward failures, indexed like [`UPSTREAM_REASONS`].
    upstream_errors: [AtomicU64; UPSTREAM_REASONS.len()],
}

impl Metrics {
    pub fn new() -> Self {
        Self::default()
    }

    /// Records one finished request: its verdict and total handling time.
    pub fn record_request(&self, verdict: &str, duration_secs: f64) {
        self.requests[verdict_index(verdict)].fetch_add(1, Ordering::Relaxed);

        self.latency_count.fetch_add(1, Ordering::Relaxed);
        self.latency_sum_micros
            .fetch_add((duration_secs * 1_000_000.0) as u64, Ordering::Relaxed);
        for (i, bound) in LATENCY_BUCKETS.iter().enumerate() {
            if duration_secs <= *bound {
                self.latency_buckets[i].fetch_add(1, Ordering::Relaxed);
            }
        }
    }

    pub fn record_upstream_error(&self, reason: &str) {
        self.upstream_errors[upstream_reason_index(reason)].fetch_add(1, Ordering::Relaxed);
    }

    /// Renders the Prometheus text exposition format (v0.0.4).
    pub fn render(&self) -> String {
        let mut out = String::with_capacity(1024);

        out.push_str("# HELP veil_requests_total Total requests handled, by verdict.\n");
        out.push_str("# TYPE veil_requests_total counter\n");
        for (i, verdict) in VERDICTS.iter().enumerate() {
            let value = self.requests[i].load(Ordering::Relaxed);
            out.push_str(&format!("veil_requests_total{{verdict=\"{verdict}\"}} {value}\n"));
        }

        out.push_str("# HELP veil_request_duration_seconds Request handling latency.\n");
        out.push_str("# TYPE veil_request_duration_seconds histogram\n");
        for (i, bound) in LATENCY_BUCKETS.iter().enumerate() {
            let count = self.latency_buckets[i].load(Ordering::Relaxed);
            out.push_str(&format!(
                "veil_request_duration_seconds_bucket{{le=\"{bound}\"}} {count}\n"
            ));
        }
        let total = self.latency_count.load(Ordering::Relaxed);
        out.push_str(&format!(
            "veil_request_duration_seconds_bucket{{le=\"+Inf\"}} {total}\n"
        ));
        let sum_secs = self.latency_sum_micros.load(Ordering::Relaxed) as f64 / 1_000_000.0;
        out.push_str(&format!("veil_request_duration_seconds_sum {sum_secs}\n"));
        out.push_str(&format!("veil_request_duration_seconds_count {total}\n"));

        out.push_str("# HELP veil_upstream_errors_total Upstream forward failures, by reason.\n");
        out.push_str("# TYPE veil_upstream_errors_total counter\n");
        for (i, reason) in UPSTREAM_REASONS.iter().enumerate() {
            let value = self.upstream_errors[i].load(Ordering::Relaxed);
            out.push_str(&format!(
                "veil_upstream_errors_total{{reason=\"{reason}\"}} {value}\n"
            ));
        }

        out
    }
}

fn verdict_index(verdict: &str) -> usize {
    VERDICTS.iter().position(|&v| v == verdict).unwrap_or(VERDICTS.len() - 1)
}

fn upstream_reason_index(reason: &str) -> usize {
    UPSTREAM_REASONS
        .iter()
        .position(|&r| r == reason)
        .unwrap_or(UPSTREAM_REASONS.len() - 1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn counts_requests_by_verdict() {
        let m = Metrics::new();
        m.record_request("allow", 0.002);
        m.record_request("allow", 0.2);
        m.record_request("block", 0.001);

        let out = m.render();
        assert!(out.contains("veil_requests_total{verdict=\"allow\"} 2"));
        assert!(out.contains("veil_requests_total{verdict=\"block\"} 1"));
        assert!(out.contains("veil_request_duration_seconds_count 3"));
    }

    #[test]
    fn unknown_verdict_falls_into_other() {
        let m = Metrics::new();
        m.record_request("surprise", 0.01);
        assert!(m.render().contains("veil_requests_total{verdict=\"other\"} 1"));
    }

    #[test]
    fn upstream_errors_counted_by_reason() {
        let m = Metrics::new();
        m.record_upstream_error("web_server_down");
        m.record_upstream_error("web_server_down");
        m.record_upstream_error("origin_timeout");
        m.record_upstream_error("totally_unknown"); // → other

        let out = m.render();
        assert!(out.contains("veil_upstream_errors_total{reason=\"web_server_down\"} 2"));
        assert!(out.contains("veil_upstream_errors_total{reason=\"origin_timeout\"} 1"));
        assert!(out.contains("veil_upstream_errors_total{reason=\"other\"} 1"));
        assert!(out.contains("veil_upstream_errors_total{reason=\"bad_gateway\"} 0"));
    }

    #[test]
    fn histogram_buckets_are_cumulative_le() {
        let m = Metrics::new();
        m.record_request("allow", 0.002); // <= 0.005, 0.01, ...
        let out = m.render();
        // Not in the 0.001 bucket, but in 0.005 and every larger bound.
        assert!(out.contains("veil_request_duration_seconds_bucket{le=\"0.001\"} 0"));
        assert!(out.contains("veil_request_duration_seconds_bucket{le=\"0.005\"} 1"));
        assert!(out.contains("veil_request_duration_seconds_bucket{le=\"+Inf\"} 1"));
    }
}
