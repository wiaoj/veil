use std::net::IpAddr;
use std::sync::Arc;
use std::time::Duration;

use hyper::header::{HeaderValue, COOKIE};
use hyper::{HeaderMap, StatusCode};

use super::*;
use behavior::BehaviorTelemetry;
use nonce_store::{InMemoryNonceStore, NonceInfo};

fn t1(difficulty: u32) -> NonceInfo {
    NonceInfo { difficulty, tier: Tier::One }
}

fn human_behavior() -> BehaviorTelemetry {
    BehaviorTelemetry {
        event_count: 40,
        path_length: 520.0,
        straight_line: 180.0,
        duration_ms: 1400,
        time_to_first_ms: 220,
        timing_jitter_ms: 9.0,
    }
}

fn ip() -> IpAddr {
    "203.0.113.7".parse().unwrap()
}

fn other_ip() -> IpAddr {
    "198.51.100.1".parse().unwrap()
}

fn ctx() -> crate::pipeline::RequestContext {
    crate::pipeline::RequestContext {
        client_ip: ip(),
        host: "example.com".to_owned(),
        method: hyper::Method::GET,
        path: "/".to_owned(),
        query: None,
        user_agent: Some("Mozilla/5.0".to_owned()),
        headers: HeaderMap::new(),
        country: None,
        asn: None,
        ja3: None,
    }
}

fn test_engine() -> ChallengeEngine {
    let hmac_key = [0xABu8; 32];
    let store = Arc::new(InMemoryNonceStore::new(Duration::from_secs(60)));
    ChallengeEngine::with_key_and_store(hmac_key, 8, "veil_pass".to_string(), 600, store)
}

// ── HMAC token tests ─────────────────────────────────────────────────

#[test]
fn token_roundtrip_same_ip() {
    let engine = test_engine();
    let token = engine.create_token(ip(), Tier::One);
    assert!(engine.validate_token(&token, ip()));
}

#[test]
fn token_rejected_for_different_ip() {
    let engine = test_engine();
    let token = engine.create_token(ip(), Tier::One);
    assert!(!engine.validate_token(&token, other_ip()));
}

#[test]
fn token_rejected_when_tampered() {
    let engine = test_engine();
    let token = engine.create_token(ip(), Tier::One);
    // Flip a character in the signature part
    let tampered = format!("{}X", &token[..token.len() - 1]);
    assert!(!engine.validate_token(&tampered, ip()));
}

#[test]
fn token_rejected_when_malformed() {
    let engine = test_engine();
    assert!(!engine.validate_token("not-a-token", ip()));
    assert!(!engine.validate_token("", ip()));
    assert!(!engine.validate_token("abc.def", ip()));
}

#[test]
fn verify_token_from_cookie_header() {
    let engine = test_engine();
    let token = engine.create_token(ip(), Tier::One);
    let mut headers = HeaderMap::new();
    headers.insert(
        COOKIE,
        HeaderValue::from_str(&format!("other=x; veil_pass={token}; foo=bar")).unwrap(),
    );
    assert!(engine.verify_token(&headers, ip()));
}

#[test]
fn verify_token_rejects_missing_cookie() {
    let engine = test_engine();
    assert!(!engine.verify_token(&HeaderMap::new(), ip()));
}

// ── PoW verification tests ───────────────────────────────────────────

#[test]
fn verify_solution_accepts_valid_pow() {
    let engine = test_engine();

    // Generate a nonce via issue_challenge (which inserts it into the store)
    let response = engine.issue_challenge(&ctx(), None);
    assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);

    // Extract nonce from the nonce store — we need to brute-force a solution
    // For testing, let's manually insert a known nonce and solve it
    let nonce = pow::generate_nonce();
    let nonce_hex = pow::to_hex(&nonce);
    engine.nonce_store.insert(&nonce_hex, t1(8));

    // Find a valid solution at difficulty 8
    let counter = (0..100_000u64)
        .find(|&c| pow::verify_pow(&nonce, c, 8))
        .expect("should find solution at difficulty 8");

    let solution = VerifySolutionRequest {
        nonce: nonce_hex,
        counter: format!("{counter:016x}"),
        behavior: None,
    };

    let response = engine.verify_solution(&solution, ip());
    assert_eq!(response.status(), StatusCode::OK);
}

#[test]
fn verify_solution_rejects_wrong_counter() {
    let engine = test_engine();
    let nonce = pow::generate_nonce();
    let nonce_hex = pow::to_hex(&nonce);
    engine.nonce_store.insert(&nonce_hex, t1(8));

    let solution = VerifySolutionRequest {
        nonce: nonce_hex,
        counter: "ffffffffffffffff".to_string(), // almost certainly wrong
        behavior: None,
    };

    let response = engine.verify_solution(&solution, ip());
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
}

#[test]
fn verify_solution_rejects_unknown_nonce() {
    let engine = test_engine();
    let solution = VerifySolutionRequest {
        nonce: "deadbeefcafebabe0102030405060708".to_string(),
        counter: "0000000000000000".to_string(),
        behavior: None,
    };

    let response = engine.verify_solution(&solution, ip());
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
}

#[test]
fn verify_solution_prevents_nonce_replay() {
    let engine = test_engine();
    let nonce = pow::generate_nonce();
    let nonce_hex = pow::to_hex(&nonce);
    engine.nonce_store.insert(&nonce_hex, t1(8));

    let counter = (0..100_000u64)
        .find(|&c| pow::verify_pow(&nonce, c, 8))
        .expect("should find solution");

    let solution = VerifySolutionRequest {
        nonce: nonce_hex.clone(),
        counter: format!("{counter:016x}"),
        behavior: None,
    };

    // First verification succeeds
    let r1 = engine.verify_solution(&solution, ip());
    assert_eq!(r1.status(), StatusCode::OK);

    // Replay with same nonce is rejected (nonce was consumed)
    let solution2 = VerifySolutionRequest {
        nonce: nonce_hex,
        counter: format!("{counter:016x}"),
        behavior: None,
    };
    let r2 = engine.verify_solution(&solution2, ip());
    assert_eq!(r2.status(), StatusCode::FORBIDDEN);
}

// ── Challenge page tests ─────────────────────────────────────────────

#[test]
fn issue_challenge_returns_503_with_nonce() {
    let engine = test_engine();
    let response = engine.issue_challenge(&ctx(), None);
    assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
}

// ── Tier 2 interaction challenge tests ───────────────────────────────

/// Inserts a Tier 2 nonce at difficulty 8 and returns (nonce_hex, counter).
fn tier2_solved(engine: &ChallengeEngine) -> (String, u64) {
    let nonce = pow::generate_nonce();
    let nonce_hex = pow::to_hex(&nonce);
    engine
        .nonce_store
        .insert(&nonce_hex, NonceInfo { difficulty: 8, tier: Tier::Two });
    let counter = (0..100_000u64)
        .find(|&c| pow::verify_pow(&nonce, c, 8))
        .expect("should find solution at difficulty 8");
    (nonce_hex, counter)
}

#[test]
fn tier2_rejects_missing_behavior() {
    let engine = test_engine();
    let (nonce_hex, counter) = tier2_solved(&engine);
    let solution = VerifySolutionRequest {
        nonce: nonce_hex,
        counter: format!("{counter:016x}"),
        behavior: None,
    };
    assert_eq!(engine.verify_solution(&solution, ip()).status(), StatusCode::FORBIDDEN);
}

#[test]
fn tier2_rejects_non_human_behavior() {
    let engine = test_engine();
    let (nonce_hex, counter) = tier2_solved(&engine);
    let solution = VerifySolutionRequest {
        nonce: nonce_hex,
        counter: format!("{counter:016x}"),
        behavior: Some(BehaviorTelemetry::default()), // zero events
    };
    assert_eq!(engine.verify_solution(&solution, ip()).status(), StatusCode::FORBIDDEN);
}

#[test]
fn tier2_accepts_human_behavior() {
    let engine = test_engine();
    let (nonce_hex, counter) = tier2_solved(&engine);
    let solution = VerifySolutionRequest {
        nonce: nonce_hex,
        counter: format!("{counter:016x}"),
        behavior: Some(human_behavior()),
    };
    assert_eq!(engine.verify_solution(&solution, ip()).status(), StatusCode::OK);
}

#[test]
fn tier2_failed_behavior_consumes_nonce() {
    // A failed Tier 2 attempt must burn the nonce so the telemetry can't be
    // brute-forced against the same (already PoW-solved) nonce.
    let engine = test_engine();
    let (nonce_hex, counter) = tier2_solved(&engine);
    let bad = VerifySolutionRequest {
        nonce: nonce_hex.clone(),
        counter: format!("{counter:016x}"),
        behavior: Some(BehaviorTelemetry::default()),
    };
    assert_eq!(engine.verify_solution(&bad, ip()).status(), StatusCode::FORBIDDEN);

    // Retry with valid behaviour on the same nonce is now an unknown nonce.
    let retry = VerifySolutionRequest {
        nonce: nonce_hex,
        counter: format!("{counter:016x}"),
        behavior: Some(human_behavior()),
    };
    assert_eq!(engine.verify_solution(&retry, ip()).status(), StatusCode::FORBIDDEN);
}
