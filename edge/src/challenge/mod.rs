//! Proof-of-Work challenge engine for Veil Edge.
//!
//! Clients that trigger a `challenge` verdict must solve a SHA-256
//! proof-of-work puzzle before accessing the origin. The flow:
//!
//! 1. Edge generates a random nonce and returns the challenge page
//!    (HTML + JS solver running in a Web Worker).
//! 2. The browser finds a `counter` such that
//!    `SHA256(nonce || counter)` has ≥ `difficulty` leading zero bits.
//! 3. The browser POSTs the solution to `/_veil/challenge/verify`.
//! 4. Edge verifies, issues an HMAC-SHA256 signed cookie, and the
//!    browser reloads — subsequent requests pass through without challenge.

pub mod behavior;
pub mod nonce_store;
pub mod pow;
pub mod risk;

use std::net::IpAddr;
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use hmac::{Hmac, Mac};
use hyper::header::COOKIE;
use hyper::{HeaderMap, Response, StatusCode};
use sha2::Sha256;

use crate::config::ChallengeSettings;
use crate::response::{html, json_response, ProxyBody};
use behavior::BehaviorTelemetry;
use nonce_store::{InMemoryNonceStore, NonceInfo, NonceStore};
use pow::{from_hex, to_hex};

type HmacSha256 = Hmac<Sha256>;

/// Challenge difficulty tier bound to an issued nonce.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tier {
    /// Tier 1 — PoW only (low/medium risk).
    One,
    /// Tier 2 — elevated PoW + behavioural interaction check (high risk).
    Two,
}

impl Tier {
    pub fn as_u8(self) -> u8 {
        match self {
            Tier::One => 1,
            Tier::Two => 2,
        }
    }
}

/// Risk score (`0..=100`) at or above which Tier 2 is served, when a zone
/// does not override it.
const DEFAULT_TIER2_RISK_THRESHOLD: u8 = 70;

/// Extra PoW leading-zero bits added on top of the risk-scaled difficulty for
/// Tier 2 — each bit doubles the client's expected work.
const TIER2_EXTRA_BITS: u32 = 3;

const TEMPLATE_HTML: &str = include_str!("../../templates/challenge.html");
const LOGO_SVG: &str = include_str!("../../templates/logo.svg");

pub const DEFAULT_COOKIE_NAME: &str = "veil_pass";
pub const DEFAULT_COOKIE_TTL: u32 = 600;

/// Reserved path prefix for challenge endpoints.
pub const CHALLENGE_VERIFY_PATH: &str = "/_veil/challenge/verify";

#[derive(Debug, serde::Deserialize)]
pub struct VerifySolutionRequest {
    pub nonce: String,
    pub counter: String,
    /// Interaction telemetry — required for Tier 2 nonces, ignored otherwise.
    #[serde(default)]
    pub behavior: Option<BehaviorTelemetry>,
}

/// A freshly issued challenge nonce and the parameters bound to it. Returned by
/// [`ChallengeEngine::issue_nonce`] and consumed both by the full-page challenge
/// and the embeddable widget's `/_veil/widget/challenge` endpoint.
#[derive(Debug, Clone)]
pub struct IssuedNonce {
    pub nonce_hex: String,
    pub difficulty: u32,
    pub tier: Tier,
}

/// Widget response-token lifetime (seconds). Short by design: the token is
/// injected into a form and meant to be verified promptly by the origin backend.
pub const WIDGET_TOKEN_TTL: u32 = 300;

/// A challenge-solution verification failure. Maps to the HTTP responses the
/// full-page verify endpoint returns, and to a Turnstile-style `error-codes`
/// entry for the widget.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SolveError {
    UnknownNonce,
    InvalidNonce,
    InvalidCounter,
    InvalidSolution,
    MissingBehavior,
    BehaviorFailed,
}

impl SolveError {
    /// Stable machine code (widget `error-codes` entry).
    pub fn code(self) -> &'static str {
        match self {
            SolveError::UnknownNonce => "unknown_nonce",
            SolveError::InvalidNonce => "invalid_nonce",
            SolveError::InvalidCounter => "invalid_counter",
            SolveError::InvalidSolution => "invalid_solution",
            SolveError::MissingBehavior => "missing_behavior",
            SolveError::BehaviorFailed => "behavior_failed",
        }
    }

    /// The full-page verify endpoint's response for this failure (unchanged
    /// statuses + localised detail).
    fn response(self) -> Response<ProxyBody> {
        let (status, body) = match self {
            SolveError::UnknownNonce => (StatusCode::FORBIDDEN,
                r#"{"error":"unknown_nonce","detail":"Nonce bilinmiyor veya süresi dolmuş."}"#),
            SolveError::InvalidNonce => (StatusCode::BAD_REQUEST,
                r#"{"error":"invalid_nonce","detail":"Nonce geçersiz hex formatında."}"#),
            SolveError::InvalidCounter => (StatusCode::BAD_REQUEST,
                r#"{"error":"invalid_counter","detail":"Counter geçersiz hex formatında."}"#),
            SolveError::InvalidSolution => (StatusCode::FORBIDDEN,
                r#"{"error":"invalid_solution","detail":"PoW çözümü geçersiz."}"#),
            SolveError::MissingBehavior => (StatusCode::FORBIDDEN,
                r#"{"error":"missing_behavior","detail":"Etkileşim doğrulaması gerekli."}"#),
            SolveError::BehaviorFailed => (StatusCode::FORBIDDEN,
                r#"{"error":"behavior_failed","detail":"Etkileşim doğrulaması başarısız."}"#),
        };
        json_response(status, body)
    }
}

pub struct ChallengeEngine {
    hmac_key: [u8; 32],
    pub difficulty: u32,
    pub cookie_name: String,
    pub cookie_ttl: u32,
    nonce_store: Arc<dyn NonceStore>,
    /// Consumed widget response-token jtis, for single-use enforcement at
    /// `/_veil/siteverify` (a token can be verified at most once).
    consumed_tokens: Arc<dyn NonceStore>,
}

impl std::fmt::Debug for ChallengeEngine {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ChallengeEngine")
            .field("difficulty", &self.difficulty)
            .field("cookie_name", &self.cookie_name)
            .field("cookie_ttl", &self.cookie_ttl)
            .finish_non_exhaustive()
    }
}

impl ChallengeEngine {
    pub fn new(cookie_name: String, cookie_ttl: u32) -> Self {
        // Try VEIL_HMAC_KEY env var, otherwise generate a random key.
        // Random key means tokens don't survive restarts — fine for dev.
        let hmac_key = match std::env::var("VEIL_HMAC_KEY") {
            Ok(hex) => {
                let bytes = from_hex(&hex).expect("VEIL_HMAC_KEY must be valid hex");
                assert!(bytes.len() == 32, "VEIL_HMAC_KEY must be 32 bytes (64 hex chars)");
                let mut key = [0u8; 32];
                key.copy_from_slice(&bytes);
                key
            }
            Err(_) => {
                let mut key = [0u8; 32];
                getrandom::fill(&mut key).expect("getrandom failed");
                key
            }
        };

        let difficulty = std::env::var("VEIL_POW_DIFFICULTY")
            .ok()
            .and_then(|v| v.parse().ok())
            .unwrap_or(pow::DEFAULT_DIFFICULTY);

        let nonce_ttl = Duration::from_secs(u64::from(cookie_ttl).max(120));

        Self {
            hmac_key,
            difficulty,
            cookie_name,
            cookie_ttl,
            nonce_store: Arc::new(InMemoryNonceStore::new(nonce_ttl)),
            consumed_tokens: Arc::new(InMemoryNonceStore::new(
                Duration::from_secs(u64::from(WIDGET_TOKEN_TTL) + 60),
            )),
        }
    }

    /// Create a `ChallengeEngine` with a specific HMAC key and nonce store.
    /// Used primarily for testing.
    #[cfg(test)]
    pub fn with_key_and_store(
        hmac_key: [u8; 32],
        difficulty: u32,
        cookie_name: String,
        cookie_ttl: u32,
        nonce_store: Arc<dyn NonceStore>,
    ) -> Self {
        Self {
            hmac_key,
            difficulty,
            cookie_name,
            cookie_ttl,
            nonce_store,
            consumed_tokens: Arc::new(InMemoryNonceStore::new(
                Duration::from_secs(u64::from(WIDGET_TOKEN_TTL) + 60),
            )),
        }
    }

    // ── Challenge issuance ────────────────────────────────────────────

    /// Generate and register a challenge nonce, risk-scaling the PoW difficulty
    /// and picking the tier. Shared by the full-page challenge and the widget's
    /// JSON challenge endpoint so both bind difficulty + tier to the nonce
    /// identically (a client cannot solve below the level it was served).
    pub fn issue_nonce(
        &self,
        ctx: &crate::pipeline::RequestContext,
        settings: Option<&ChallengeSettings>,
    ) -> IssuedNonce {
        let risk = risk::score(ctx);
        let threshold = settings
            .map(|s| s.tier2_risk_threshold)
            .unwrap_or(DEFAULT_TIER2_RISK_THRESHOLD);
        let tier = if risk >= threshold { Tier::Two } else { Tier::One };

        // Per-zone base difficulty / token TTL override the engine defaults.
        let base_difficulty = settings.and_then(|s| s.base_difficulty).unwrap_or(self.difficulty);
        let token_ttl = settings.and_then(|s| s.token_ttl_secs).unwrap_or(self.cookie_ttl);

        let mut difficulty = risk::difficulty_for(base_difficulty, risk);
        if tier == Tier::Two {
            difficulty += TIER2_EXTRA_BITS;
        }

        let nonce = pow::generate_nonce();
        let nonce_hex = to_hex(&nonce);
        self.nonce_store.insert(&nonce_hex, NonceInfo { difficulty, tier, token_ttl });

        IssuedNonce { nonce_hex, difficulty, tier }
    }

    /// Generate a nonce and return the challenge page HTML. The PoW
    /// difficulty is scaled by the request's risk score (Phase 4.2) and
    /// bound to the nonce, so a client cannot solve below the level it was
    /// served. When the risk score crosses the zone's Tier 2 threshold the
    /// page is served as a Tier 2 interaction challenge: elevated PoW plus a
    /// behavioural check enforced at verification (Phase 4.3).
    pub fn issue_challenge(
        &self,
        ctx: &crate::pipeline::RequestContext,
        settings: Option<&ChallengeSettings>,
    ) -> Response<ProxyBody> {
        let issued = self.issue_nonce(ctx, settings);
        let tier = issued.tier;

        // Tier 2 can be rendered as a *visible* self-hosted interaction widget
        // (a "verify I'm human" checkbox) when the zone opts in. Verification is
        // unchanged — the elevated PoW plus the behavioural telemetry the widget's
        // click naturally produces; no third-party service is involved.
        let interactive = tier == Tier::Two && settings.is_some_and(|s| s.require_interaction);

        // Visitor-facing copy is localised from Accept-Language / ?locale.
        let lang = ctx.lang();
        let t = crate::i18n::challenge_strings(lang);

        let body = TEMPLATE_HTML
            .replace("{logo_svg}", LOGO_SVG)
            .replace("{nonce}", &issued.nonce_hex)
            .replace("{difficulty}", &issued.difficulty.to_string())
            .replace("{tier}", &tier.as_u8().to_string())
            .replace("{interactive}", if interactive { "1" } else { "0" })
            .replace("{lang}", lang.code())
            .replace("{doc_title}", t.doc_title)
            .replace("{heading}", t.heading)
            .replace("{intro}", t.intro)
            .replace("{noscript}", t.noscript)
            .replace("{status_verifying}", t.status_verifying)
            .replace("{hint}", t.hint)
            .replace("{verify_label}", t.verify_label)
            .replace("{verify_checking}", t.verify_checking)
            .replace("{verify_done}", t.verify_done)
            .replace("{footer}", t.footer)
            .replace("{status_redirecting}", t.status_redirecting)
            .replace("{status_almost}", t.status_almost)
            .replace("{status_error}", t.status_error)
            .replace("{status_failed_retry}", t.status_failed_retry)
            .replace("{status_conn_retry}", t.status_conn_retry);

        html(StatusCode::SERVICE_UNAVAILABLE, body)
    }

    // ── Solution verification ─────────────────────────────────────────

    /// Validate a PoW solution against its bound nonce and consume it. Shared by
    /// the full-page challenge (`verify_solution`, cookie) and the embeddable
    /// widget (`verify_widget`, token). Consumes the nonce before the behavioural
    /// check so a failed Tier 2 attempt can't be brute-forced against it — the
    /// client must re-solve. Returns the nonce's [`NonceInfo`] on success.
    fn check_and_consume(
        &self,
        nonce: &str,
        counter_hex: &str,
        behavior: &Option<BehaviorTelemetry>,
    ) -> Result<NonceInfo, SolveError> {
        let info = self.nonce_store.lookup(nonce).ok_or(SolveError::UnknownNonce)?;
        let nonce_bytes = from_hex(nonce).ok_or(SolveError::InvalidNonce)?;
        let counter =
            u64::from_str_radix(counter_hex, 16).map_err(|_| SolveError::InvalidCounter)?;

        if !pow::verify_pow(&nonce_bytes, counter, info.difficulty) {
            return Err(SolveError::InvalidSolution);
        }

        // One-time use — consume before scoring the interaction.
        self.nonce_store.remove(nonce);

        if info.tier == Tier::Two {
            let telemetry = behavior.as_ref().ok_or(SolveError::MissingBehavior)?;
            if !behavior::is_human(telemetry) {
                return Err(SolveError::BehaviorFailed);
            }
        }

        Ok(info)
    }

    /// Verify the PoW solution and issue a signed token cookie.
    ///
    /// Returns a `200` with the pass cookie on success, or the mapped error.
    pub fn verify_solution(
        &self,
        solution: &VerifySolutionRequest,
        client_ip: IpAddr,
    ) -> Response<ProxyBody> {
        let info = match self.check_and_consume(&solution.nonce, &solution.counter, &solution.behavior) {
            Ok(info) => info,
            Err(err) => return err.response(),
        };

        // Issue signed token cookie (lifetime bound on the nonce).
        let token = self.create_token(client_ip, info.tier, info.token_ttl);
        let cookie = format!(
            "{}={}; Path=/; Max-Age={}; SameSite=Lax; HttpOnly",
            self.cookie_name, token, info.token_ttl
        );

        Response::builder()
            .status(StatusCode::OK)
            .header("Content-Type", "application/json; charset=utf-8")
            .header("Set-Cookie", cookie)
            .header("Cache-Control", "no-store")
            .body(crate::response::full(r#"{"ok":true}"#))
            .expect("static response")
    }

    // ── Embeddable widget (self-hosted, form-embed / siteverify) ──────

    /// Verify a widget PoW solution and mint a single-use response token bound to
    /// `sitekey`. The token is returned to the widget (not set as a cookie), which
    /// injects it into the form; the origin backend later confirms it via
    /// [`ChallengeEngine::verify_widget_token`] at `/_veil/siteverify`.
    pub fn verify_widget(
        &self,
        sitekey: &str,
        nonce: &str,
        counter: &str,
        behavior: &Option<BehaviorTelemetry>,
    ) -> Result<String, SolveError> {
        self.check_and_consume(nonce, counter, behavior)?;
        Ok(self.mint_widget_token(sitekey))
    }

    /// Token format: `{payload_hex}.{signature_hex}`, payload
    /// `{"sk":"<sitekey>","exp":<unix>,"jti":"<hex>"}`. The random `jti` makes
    /// every token distinct and enables single-use enforcement at siteverify.
    fn mint_widget_token(&self, sitekey: &str) -> String {
        let exp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_secs()
            + u64::from(WIDGET_TOKEN_TTL);

        let mut jti = [0u8; 16];
        getrandom::fill(&mut jti).expect("getrandom failed");
        let jti_hex = to_hex(&jti);

        let payload_json = format!(r#"{{"sk":"{sitekey}","exp":{exp},"jti":"{jti_hex}"}}"#);
        let payload_hex = to_hex(payload_json.as_bytes());
        let sig_hex = to_hex(&self.sign(payload_json.as_bytes()));
        format!("{payload_hex}.{sig_hex}")
    }

    /// Verify a widget response token at siteverify time: signature valid, not
    /// expired, bound to `sitekey`, and not already consumed (single-use).
    pub fn verify_widget_token(&self, token: &str, sitekey: &str) -> bool {
        let Some((payload_hex, sig_hex)) = token.split_once('.') else {
            return false;
        };
        let Some(payload_bytes) = from_hex(payload_hex) else {
            return false;
        };
        let Some(sig_bytes) = from_hex(sig_hex) else {
            return false;
        };
        if !self.verify_sig(&payload_bytes, &sig_bytes) {
            return false;
        }

        #[derive(serde::Deserialize)]
        struct Payload {
            sk: String,
            exp: u64,
            jti: String,
        }
        let Ok(payload) = serde_json::from_slice::<Payload>(&payload_bytes) else {
            return false;
        };

        if payload.sk != sitekey {
            return false;
        }
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_secs();
        if payload.exp <= now {
            return false;
        }

        // Single-use: the first siteverify consumes the jti; a replay finds it
        // already present and is rejected.
        self.consumed_tokens
            .insert(&payload.jti, NonceInfo { difficulty: 0, tier: Tier::One, token_ttl: 0 })
    }

    // ── Token verification ────────────────────────────────────────────

    /// Check whether the request carries a valid, non-expired HMAC token
    /// for this client IP.
    pub fn verify_token(&self, headers: &HeaderMap, client_ip: IpAddr) -> bool {
        let token = self.extract_cookie(headers);
        match token {
            Some(t) => self.validate_token(&t, client_ip),
            None => false,
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────

    fn extract_cookie(&self, headers: &HeaderMap) -> Option<String> {
        headers
            .get_all(COOKIE)
            .iter()
            .filter_map(|v| v.to_str().ok())
            .flat_map(|v| v.split(';'))
            .filter_map(|pair| pair.trim().split_once('='))
            .find(|(name, _)| *name == self.cookie_name)
            .map(|(_, value)| value.to_owned())
    }

    /// Token format: `{payload_hex}.{signature_hex}`
    /// Payload (before hex): `{"ip":"127.0.0.1","exp":1234567890,"tier":1}`
    fn create_token(&self, client_ip: IpAddr, tier: Tier, token_ttl: u32) -> String {
        let expiry = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_secs()
            + u64::from(token_ttl);

        let payload_json =
            format!(r#"{{"ip":"{}","exp":{},"tier":{}}}"#, client_ip, expiry, tier.as_u8());
        let payload_hex = to_hex(payload_json.as_bytes());
        let sig = self.sign(payload_json.as_bytes());
        let sig_hex = to_hex(&sig);

        format!("{payload_hex}.{sig_hex}")
    }

    fn validate_token(&self, token: &str, client_ip: IpAddr) -> bool {
        let Some((payload_hex, sig_hex)) = token.split_once('.') else {
            return false;
        };

        let Some(payload_bytes) = from_hex(payload_hex) else {
            return false;
        };
        let Some(sig_bytes) = from_hex(sig_hex) else {
            return false;
        };

        // Verify HMAC signature
        if !self.verify_sig(&payload_bytes, &sig_bytes) {
            return false;
        }

        #[derive(serde::Deserialize)]
        struct Payload {
            ip: String,
            exp: u64,
        }
        
        let Ok(payload) = serde_json::from_slice::<Payload>(&payload_bytes) else {
            return false;
        };

        // Verify IP matches
        if payload.ip != client_ip.to_string() {
            return false;
        }

        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_secs();

        payload.exp > now
    }

    fn sign(&self, data: &[u8]) -> Vec<u8> {
        let mut mac =
            HmacSha256::new_from_slice(&self.hmac_key).expect("HMAC key length is always valid");
        mac.update(data);
        mac.finalize().into_bytes().to_vec()
    }

    fn verify_sig(&self, data: &[u8], signature: &[u8]) -> bool {
        let mut mac =
            HmacSha256::new_from_slice(&self.hmac_key).expect("HMAC key length is always valid");
        mac.update(data);
        mac.verify_slice(signature).is_ok()
    }
}

impl Default for ChallengeEngine {
    fn default() -> Self {
        Self::new(DEFAULT_COOKIE_NAME.to_string(), DEFAULT_COOKIE_TTL)
    }
}

#[cfg(test)]
mod tests;
