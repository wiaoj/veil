//! Attack-threshold webhooks.
//!
//! Rather than firing per blocked request (which would amplify an attack into
//! a flood of outbound calls), the edge counts enforced attack verdicts per
//! zone in a fixed window and fires a single webhook when the count crosses a
//! threshold — then stays quiet for a cooldown. Delivery is fire-and-forget;
//! a failed POST is logged and dropped.
//!
//! Enabled by `VEIL_WEBHOOK_URL`. The body is optionally HMAC-SHA256 signed
//! (`VEIL_WEBHOOK_SECRET`, 64 hex chars) in the `x-veil-signature` header.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use hmac::{Hmac, Mac};
use http_body_util::Full;
use hyper::body::Bytes;
use hyper::header::CONTENT_TYPE;
use hyper::Request;
use hyper_rustls::HttpsConnector;
use hyper_util::client::legacy::connect::HttpConnector;
use hyper_util::client::legacy::Client;
use hyper_util::rt::TokioExecutor;
use sha2::Sha256;
use tracing::{info, warn};

const DEFAULT_THRESHOLD: u32 = 50;
const DEFAULT_WINDOW_SECS: u64 = 60;
const DEFAULT_COOLDOWN_SECS: u64 = 300;

type WebhookClient = Client<HttpsConnector<HttpConnector>, Full<Bytes>>;

pub struct WebhookNotifier {
    url: String,
    threshold: u32,
    window_secs: u64,
    cooldown_secs: u64,
    secret: Option<[u8; 32]>,
    client: WebhookClient,
    zones: Mutex<HashMap<String, ZoneState>>,
}

#[derive(Default)]
struct ZoneState {
    window_idx: u64,
    count: u32,
    last_fired: u64,
}

/// Only enforced attack verdicts count toward the threshold.
fn is_attack(verdict: &str) -> bool {
    matches!(verdict, "block" | "challenge" | "rate_limited")
}

impl WebhookNotifier {
    /// Builds from the environment; `None` when `VEIL_WEBHOOK_URL` is unset.
    pub fn from_env() -> Option<Self> {
        let url = std::env::var("VEIL_WEBHOOK_URL").ok()?;
        let threshold = env_parse("VEIL_WEBHOOK_THRESHOLD", DEFAULT_THRESHOLD);
        let window_secs = env_parse("VEIL_WEBHOOK_WINDOW_SECS", DEFAULT_WINDOW_SECS);
        let cooldown_secs = env_parse("VEIL_WEBHOOK_COOLDOWN_SECS", DEFAULT_COOLDOWN_SECS);
        let secret = std::env::var("VEIL_WEBHOOK_SECRET")
            .ok()
            .and_then(|hex| crate::challenge::pow::from_hex(&hex))
            .and_then(|b| <[u8; 32]>::try_from(b).ok());

        let https = hyper_rustls::HttpsConnectorBuilder::new()
            .with_webpki_roots()
            .https_or_http()
            .enable_http1()
            .build();
        let client = Client::builder(TokioExecutor::new()).build(https);

        info!(url, threshold, window_secs, cooldown_secs, "attack webhooks enabled");
        Some(Self {
            url,
            threshold,
            window_secs,
            cooldown_secs,
            secret,
            client,
            zones: Mutex::new(HashMap::new()),
        })
    }

    /// Records an attack verdict for `zone` and fires a webhook if this crossed
    /// the threshold (subject to cooldown). Non-attack verdicts are ignored.
    pub fn record(&self, zone: &str, verdict: &str, client_ip: std::net::IpAddr) {
        if !is_attack(verdict) {
            return;
        }
        let now = now_secs();
        let Some(count) = self.decide(zone, now) else {
            return;
        };
        self.fire(zone, verdict, client_ip, count);
    }

    /// Window/threshold/cooldown decision, separated from I/O for testing.
    /// Returns the in-window count when a webhook should fire.
    fn decide(&self, zone: &str, now: u64) -> Option<u32> {
        let idx = now / self.window_secs.max(1);
        let mut zones = self.zones.lock().expect("webhook state poisoned");
        let state = zones.entry(zone.to_owned()).or_default();

        if state.window_idx != idx {
            state.window_idx = idx;
            state.count = 0;
        }
        state.count += 1;

        if state.count >= self.threshold && now - state.last_fired >= self.cooldown_secs {
            state.last_fired = now;
            Some(state.count)
        } else {
            None
        }
    }

    fn fire(&self, zone: &str, verdict: &str, client_ip: std::net::IpAddr, count: u32) {
        let body = format!(
            r#"{{"type":"attack_threshold_breach","zone":"{}","verdict":"{}","sample_client_ip":"{}","count":{},"window_secs":{},"threshold":{},"ts_ms":{}}}"#,
            zone, verdict, client_ip, count, self.window_secs, self.threshold, now_secs() * 1000
        );
        let signature = self.secret.map(|key| {
            let mut mac = <Hmac<Sha256> as Mac>::new_from_slice(&key).expect("hmac any key len");
            mac.update(body.as_bytes());
            crate::challenge::pow::to_hex(&mac.finalize().into_bytes())
        });

        let client = self.client.clone();
        let url = self.url.clone();
        tokio::spawn(async move {
            let mut req = Request::post(&url).header(CONTENT_TYPE, "application/json");
            if let Some(sig) = signature {
                req = req.header("x-veil-signature", sig);
            }
            let request = match req.body(Full::new(Bytes::from(body))) {
                Ok(r) => r,
                Err(err) => {
                    warn!(error = %err, "webhook request build failed");
                    return;
                }
            };
            match client.request(request).await {
                Ok(resp) if resp.status().is_success() => {}
                Ok(resp) => warn!(status = %resp.status(), "webhook returned non-success"),
                Err(err) => warn!(error = %err, "webhook delivery failed"),
            }
        });
    }
}

fn env_parse<T: std::str::FromStr>(key: &str, default: T) -> T {
    std::env::var(key).ok().and_then(|v| v.parse().ok()).unwrap_or(default)
}

fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock before unix epoch")
        .as_secs()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn notifier(threshold: u32, window: u64, cooldown: u64) -> WebhookNotifier {
        let https = hyper_rustls::HttpsConnectorBuilder::new()
            .with_webpki_roots()
            .https_or_http()
            .enable_http1()
            .build();
        WebhookNotifier {
            url: "http://localhost:1/hook".to_owned(),
            threshold,
            window_secs: window,
            cooldown_secs: cooldown,
            secret: None,
            client: Client::builder(TokioExecutor::new()).build(https),
            zones: Mutex::new(HashMap::new()),
        }
    }

    #[test]
    fn fires_once_when_threshold_crossed() {
        let n = notifier(3, 60, 300);
        assert_eq!(n.decide("z", 1000), None); // 1
        assert_eq!(n.decide("z", 1000), None); // 2
        assert_eq!(n.decide("z", 1000), Some(3)); // 3 — fire
        assert_eq!(n.decide("z", 1001), None); // still in cooldown
    }

    #[test]
    fn window_reset_drops_count() {
        let n = notifier(3, 60, 300);
        n.decide("z", 1000);
        n.decide("z", 1000);
        // Next window: counter resets, threshold not reached.
        assert_eq!(n.decide("z", 1060), None);
    }

    #[test]
    fn cooldown_gates_repeat_fires() {
        let n = notifier(1, 60, 300);
        assert_eq!(n.decide("z", 1000), Some(1)); // fire
        assert_eq!(n.decide("z", 1030), None); // within cooldown
        // After cooldown (and into a window where count re-reaches threshold).
        assert_eq!(n.decide("z", 1400), Some(1));
    }

    #[test]
    fn zones_are_independent() {
        let n = notifier(2, 60, 300);
        assert_eq!(n.decide("a", 1000), None);
        assert_eq!(n.decide("b", 1000), None);
        assert_eq!(n.decide("a", 1000), Some(2));
    }
}
