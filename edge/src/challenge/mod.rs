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

use crate::response::{html, json_response, ProxyBody};
use nonce_store::{InMemoryNonceStore, NonceStore};
use pow::{from_hex, to_hex};

type HmacSha256 = Hmac<Sha256>;

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
}

pub struct ChallengeEngine {
    hmac_key: [u8; 32],
    pub difficulty: u32,
    pub cookie_name: String,
    pub cookie_ttl: u32,
    nonce_store: Arc<dyn NonceStore>,
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
        }
    }

    // ── Challenge issuance ────────────────────────────────────────────

    /// Generate a nonce and return the challenge page HTML. The PoW
    /// difficulty is scaled by the request's risk score (Phase 4.2) and
    /// bound to the nonce, so a client cannot solve below the level it was
    /// served.
    pub fn issue_challenge(&self, ctx: &crate::pipeline::RequestContext) -> Response<ProxyBody> {
        let risk = risk::score(ctx);
        let difficulty = risk::difficulty_for(self.difficulty, risk);

        let nonce = pow::generate_nonce();
        let nonce_hex = to_hex(&nonce);

        // Track the nonce (replay protection) together with its difficulty.
        self.nonce_store.insert(&nonce_hex, difficulty);

        let body = TEMPLATE_HTML
            .replace("{logo_svg}", LOGO_SVG)
            .replace("{nonce}", &nonce_hex)
            .replace("{difficulty}", &difficulty.to_string());

        html(StatusCode::SERVICE_UNAVAILABLE, body)
    }

    // ── Solution verification ─────────────────────────────────────────

    /// Verify the PoW solution and issue a signed token cookie.
    ///
    /// Returns `Ok(response_with_cookie)` on success, or `Err(response_403)`.
    pub fn verify_solution(
        &self,
        solution: &VerifySolutionRequest,
        client_ip: IpAddr,
    ) -> Response<ProxyBody> {
        // 1. Check nonce is pending (replay protection) and recover the
        //    difficulty it was issued at.
        let Some(required_difficulty) = self.nonce_store.difficulty(&solution.nonce) else {
            return json_response(
                StatusCode::FORBIDDEN,
                r#"{"error":"unknown_nonce","detail":"Nonce bilinmiyor veya süresi dolmuş."}"#,
            );
        };

        // 2. Decode nonce and counter
        let Some(nonce_bytes) = from_hex(&solution.nonce) else {
            return json_response(
                StatusCode::BAD_REQUEST,
                r#"{"error":"invalid_nonce","detail":"Nonce geçersiz hex formatında."}"#,
            );
        };
        let Ok(counter) = u64::from_str_radix(&solution.counter, 16) else {
            return json_response(
                StatusCode::BAD_REQUEST,
                r#"{"error":"invalid_counter","detail":"Counter geçersiz hex formatında."}"#,
            );
        };

        // 3. Verify the PoW at the difficulty bound to this nonce
        if !pow::verify_pow(&nonce_bytes, counter, required_difficulty) {
            return json_response(
                StatusCode::FORBIDDEN,
                r#"{"error":"invalid_solution","detail":"PoW çözümü geçersiz."}"#,
            );
        }

        // 4. Consume the nonce (one-time use)
        self.nonce_store.remove(&solution.nonce);

        // 5. Issue signed token cookie
        let token = self.create_token(client_ip);
        let cookie = format!(
            "{}={}; Path=/; Max-Age={}; SameSite=Lax; HttpOnly",
            self.cookie_name, token, self.cookie_ttl
        );

        Response::builder()
            .status(StatusCode::OK)
            .header("Content-Type", "application/json; charset=utf-8")
            .header("Set-Cookie", cookie)
            .header("Cache-Control", "no-store")
            .body(crate::response::full(r#"{"ok":true}"#))
            .expect("static response")
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
    /// Payload (before hex): `{"ip":"127.0.0.1","exp":1234567890}`
    fn create_token(&self, client_ip: IpAddr) -> String {
        let expiry = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_secs()
            + u64::from(self.cookie_ttl);

        let payload_json = format!(r#"{{"ip":"{}","exp":{}}}"#, client_ip, expiry);
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
