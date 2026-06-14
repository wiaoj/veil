//! End-to-end test: a real upstream server, a real proxy, real TCP.

use std::convert::Infallible;
use std::net::SocketAddr;
use std::sync::Arc;

use http_body_util::{BodyExt, Empty, Full};
use hyper::body::{Bytes, Incoming};
use hyper::header::{COOKIE, SET_COOKIE};
use hyper::service::service_fn;
use hyper::{Request, Response, StatusCode};
use hyper_util::client::legacy::Client;
use hyper_util::rt::{TokioExecutor, TokioIo};
use hyper_util::server::conn::auto;
use tokio::net::TcpListener;

use veil_edge::config::Config;
use veil_edge::proxy::{self, AppState};

/// Upstream that answers 200 and echoes the X-Forwarded-For it received.
async fn upstream_handler(req: Request<Incoming>) -> Result<Response<Full<Bytes>>, Infallible> {
    let xff = req
        .headers()
        .get("x-forwarded-for")
        .and_then(|v| v.to_str().ok())
        .unwrap_or("")
        .to_owned();
    Ok(Response::builder()
        .header("x-test-received-xff", xff)
        .body(Full::new(Bytes::from("upstream-ok")))
        .unwrap())
}

async fn spawn_upstream() -> SocketAddr {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move {
        loop {
            let (stream, _) = listener.accept().await.unwrap();
            tokio::spawn(async move {
                let _ = auto::Builder::new(TokioExecutor::new())
                    .serve_connection(TokioIo::new(stream), service_fn(upstream_handler))
                    .await;
            });
        }
    });
    addr
}

async fn spawn_proxy(upstream: SocketAddr) -> SocketAddr {
    let config = Config::from_json(&format!(
        r#"{{
            "zones": [{{
                "name": "test",
                "hosts": ["*"],
                "upstream": "http://{upstream}",
                "rules": [
                    {{"id": "block-admin", "priority": 10, "action": "block",
                      "conditions": [{{"type": "path_prefix", "value": "/admin"}}]}},
                    {{"id": "challenge-login", "priority": 20, "action": "challenge",
                      "conditions": [{{"type": "path_exact", "value": "/login"}}]}},
                    {{"id": "api-rl", "priority": 30, "action": "rate_limit",
                      "conditions": [{{"type": "path_prefix", "value": "/api"}}],
                      "rate_limit": {{"requests": 3, "window_secs": 60}}}}
                ]
            }}]
        }}"#
    ))
    .unwrap();

    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(proxy::serve(listener, Arc::new(AppState::new(config))));
    addr
}

type TestClient = Client<hyper_util::client::legacy::connect::HttpConnector, Empty<Bytes>>;

fn client() -> TestClient {
    Client::builder(TokioExecutor::new()).build_http()
}

async fn get(client: &TestClient, addr: SocketAddr, path: &str) -> Response<Incoming> {
    let req = Request::builder()
        .uri(format!("http://{addr}{path}"))
        .body(Empty::new())
        .unwrap();
    client.request(req).await.unwrap()
}

#[tokio::test]
async fn proxies_allowed_requests_to_upstream() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy(upstream).await;
    let client = client();

    let response = get(&client, proxy, "/hello?x=1").await;
    assert_eq!(response.status(), StatusCode::OK);

    let xff = response
        .headers()
        .get("x-test-received-xff")
        .unwrap()
        .to_str()
        .unwrap()
        .to_owned();
    assert_eq!(xff, "127.0.0.1", "proxy must append the client IP");

    let body = response.into_body().collect().await.unwrap().to_bytes();
    assert_eq!(&body[..], b"upstream-ok");
}

#[tokio::test]
async fn blocks_matching_path() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy(upstream).await;
    let client = client();

    let response = get(&client, proxy, "/admin/users").await;
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
}

#[tokio::test]
async fn rate_limits_after_threshold() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy(upstream).await;
    let client = client();

    for i in 1..=3 {
        let response = get(&client, proxy, "/api/data").await;
        assert_eq!(response.status(), StatusCode::OK, "request {i} should pass");
    }
    let response = get(&client, proxy, "/api/data").await;
    assert_eq!(response.status(), StatusCode::TOO_MANY_REQUESTS);
    assert!(response.headers().contains_key("retry-after"));

    // Other paths are unaffected by the exhausted /api counter.
    let response = get(&client, proxy, "/other").await;
    assert_eq!(response.status(), StatusCode::OK);
}

#[tokio::test]
async fn challenge_blocks_then_passes_with_cookie() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy(upstream).await;
    let client = client();

    // First visit: interstitial with the nonce embedded in the JS. Send
    // browser-like headers so the risk score is 0 and the difficulty stays
    // at the base (Phase 4.2 scales it up for suspicious fingerprints).
    let challenge_req = Request::builder()
        .uri(format!("http://{proxy}/login"))
        .header(hyper::header::USER_AGENT, "Mozilla/5.0 (Windows NT 10.0; rv:130.0)")
        .header(hyper::header::ACCEPT, "text/html")
        .header(hyper::header::ACCEPT_LANGUAGE, "en-US")
        .header(hyper::header::ACCEPT_ENCODING, "gzip")
        .body(Empty::<Bytes>::new())
        .unwrap();
    let response = client.request(challenge_req).await.unwrap();
    assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
    assert!(response.headers().get(SET_COOKIE).is_none(), "token is not set directly anymore");

    let body = response.into_body().collect().await.unwrap().to_bytes();
    let html = String::from_utf8(body.to_vec()).unwrap();
    let nonce = html
        .split("var NONCE     = \"")
        .nth(1)
        .and_then(|s| s.split('"').next())
        .expect("challenge page must embed the nonce");

    let nonce_bytes = veil_edge::challenge::pow::from_hex(nonce).unwrap();

    // Solve at the difficulty the page embeds (base 20 here, since the
    // request scored risk 0). Parsing it keeps the test honest if the base
    // or risk scaling changes.
    let difficulty: u32 = html
        .split("var DIFFICULTY = ")
        .nth(1)
        .and_then(|s| s.split(';').next())
        .and_then(|s| s.trim().parse().ok())
        .expect("challenge page must embed the difficulty");
    assert_eq!(difficulty, 20, "browser-like request should not be risk-scaled");

    let mut counter = 0;
    while !veil_edge::challenge::pow::verify_pow(&nonce_bytes, counter, difficulty) {
        counter += 1;
    }

    let payload = format!(r#"{{"nonce":"{}","counter":"{:016x}"}}"#, nonce, counter);
    
    let req = Request::builder()
        .method(hyper::Method::POST)
        .uri(format!("http://{proxy}/_veil/challenge/verify"))
        .header(hyper::header::CONTENT_TYPE, "application/json")
        .body(Full::new(Bytes::from(payload)))
        .unwrap();
    let post_client: Client<hyper_util::client::legacy::connect::HttpConnector, Full<Bytes>> = 
        Client::builder(hyper_util::rt::TokioExecutor::new()).build_http();
    
    let verify_resp = post_client.request(req).await.unwrap();
    assert_eq!(verify_resp.status(), StatusCode::OK);
    
    let cookie_header = verify_resp.headers().get(SET_COOKIE).unwrap().to_str().unwrap();
    let token = cookie_header
        .split("veil_pass=")
        .nth(1)
        .and_then(|s| s.split(';').next())
        .unwrap();

    // Second visit with the issued signed cookie.
    let req = Request::builder()
        .uri(format!("http://{proxy}/login"))
        .header(COOKIE, format!("veil_pass={token}"))
        .body(Empty::new())
        .unwrap();
    let response = client.request(req).await.unwrap();
    assert_eq!(response.status(), StatusCode::OK);

    let body = response.into_body().collect().await.unwrap().to_bytes();
    assert_eq!(&body[..], b"upstream-ok");
}

// ── Config push receiver ─────────────────────────────────────────────

const NODE_TOKEN: &str = "test-node-token";

async fn spawn_proxy_with_token(upstream: SocketAddr) -> SocketAddr {
    let config = Config::from_json(&format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}", "rules": []}}]}}"#
    ))
    .unwrap();

    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let state = AppState::with_node_token(config, Some(NODE_TOKEN.to_owned()));
    tokio::spawn(proxy::serve(listener, Arc::new(state)));
    addr
}

async fn push_config(
    proxy: SocketAddr,
    token: Option<&str>,
    body: &str,
) -> Response<Incoming> {
    let mut builder = Request::builder()
        .method(hyper::Method::POST)
        .uri(format!("http://{proxy}/_veil/internal/config"))
        .header(hyper::header::CONTENT_TYPE, "application/json");
    if let Some(token) = token {
        builder = builder.header("x-veil-node-token", token);
    }
    let req = builder.body(Full::new(Bytes::from(body.to_owned()))).unwrap();

    let post_client: Client<hyper_util::client::legacy::connect::HttpConnector, Full<Bytes>> =
        Client::builder(TokioExecutor::new()).build_http();
    post_client.request(req).await.unwrap()
}

#[tokio::test]
async fn config_push_swaps_active_config() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_with_token(upstream).await;
    let client = client();

    // No rules yet: /blocked passes through.
    let response = get(&client, proxy, "/blocked").await;
    assert_eq!(response.status(), StatusCode::OK);

    let new_config = format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}",
            "rules": [{{"id": "b", "priority": 1, "action": "block",
                        "conditions": [{{"type": "path_prefix", "value": "/blocked"}}]}}]}}]}}"#
    );
    let response = push_config(proxy, Some(NODE_TOKEN), &new_config).await;
    assert_eq!(response.status(), StatusCode::OK);

    // The pushed rule is now enforced; other paths still pass.
    let response = get(&client, proxy, "/blocked").await;
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
    let response = get(&client, proxy, "/open").await;
    assert_eq!(response.status(), StatusCode::OK);
}

#[tokio::test]
async fn config_push_rejects_bad_token_and_keeps_old_config() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_with_token(upstream).await;
    let client = client();

    let new_config = format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}",
            "rules": [{{"id": "b", "priority": 1, "action": "block",
                        "conditions": []}}]}}]}}"#
    );

    let response = push_config(proxy, Some("wrong-token"), &new_config).await;
    assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    let response = push_config(proxy, None, &new_config).await;
    assert_eq!(response.status(), StatusCode::UNAUTHORIZED);

    // Old (empty) rule set still serving.
    let response = get(&client, proxy, "/anything").await;
    assert_eq!(response.status(), StatusCode::OK);
}

#[tokio::test]
async fn config_push_rejects_invalid_config() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_with_token(upstream).await;
    let client = client();

    // rate_limit action without params fails config validation.
    let bad_config = r#"{"zones": [{"name": "t", "hosts": ["*"], "upstream": "http://127.0.0.1:1",
        "rules": [{"id": "r", "priority": 1, "action": "rate_limit", "conditions": []}]}]}"#;
    let response = push_config(proxy, Some(NODE_TOKEN), bad_config).await;
    assert_eq!(response.status(), StatusCode::BAD_REQUEST);

    // Old config untouched.
    let response = get(&client, proxy, "/anything").await;
    assert_eq!(response.status(), StatusCode::OK);
}

#[tokio::test]
async fn config_push_updates_last_known_good_cache() {
    let upstream = spawn_upstream().await;

    let cache_path = std::env::temp_dir().join(format!(
        "veil-push-cache-test-{}.json",
        std::process::id()
    ));
    std::fs::remove_file(&cache_path).ok();

    let config = Config::from_json(&format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}", "rules": []}}]}}"#
    ))
    .unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let proxy = listener.local_addr().unwrap();
    let state = AppState::with_options(
        config,
        Some(NODE_TOKEN.to_owned()),
        Some(cache_path.clone()),
        None,
    );
    tokio::spawn(proxy::serve(listener, Arc::new(state)));

    let pushed = format!(
        r#"{{"zones": [{{"name": "pushed", "hosts": ["*"],
            "upstream": "http://{upstream}", "rules": []}}]}}"#
    );
    let response = push_config(proxy, Some(NODE_TOKEN), &pushed).await;
    assert_eq!(response.status(), StatusCode::OK);

    // The cache now holds exactly the pushed snapshot.
    let cached = veil_edge::config::cache::load(&cache_path).unwrap();
    assert_eq!(cached.zones[0].name, "pushed");

    std::fs::remove_file(&cache_path).ok();
}

// ── HMAC-signed pushes (ConfigSync worker path) ──────────────────────

const PUSH_KEY: [u8; 32] = [0x11; 32];

fn sign_body(body: &str) -> String {
    use hmac::{Hmac, Mac};
    let mut mac = <Hmac<sha2::Sha256> as Mac>::new_from_slice(&PUSH_KEY).unwrap();
    mac.update(body.as_bytes());
    veil_edge::challenge::pow::to_hex(&mac.finalize().into_bytes())
}

async fn spawn_proxy_with_push_key(upstream: SocketAddr) -> SocketAddr {
    let config = Config::from_json(&format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}", "rules": []}}]}}"#
    ))
    .unwrap();

    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    // No node token: signature is the only accepted credential.
    let state = AppState::with_options(config, None, None, Some(PUSH_KEY));
    tokio::spawn(proxy::serve(listener, Arc::new(state)));
    addr
}

async fn push_signed(proxy: SocketAddr, signature: &str, body: &str) -> Response<Incoming> {
    let req = Request::builder()
        .method(hyper::Method::POST)
        .uri(format!("http://{proxy}/_veil/internal/config"))
        .header(hyper::header::CONTENT_TYPE, "application/json")
        .header("x-veil-signature", signature)
        .body(Full::new(Bytes::from(body.to_owned())))
        .unwrap();
    let post_client: Client<hyper_util::client::legacy::connect::HttpConnector, Full<Bytes>> =
        Client::builder(TokioExecutor::new()).build_http();
    post_client.request(req).await.unwrap()
}

#[tokio::test]
async fn config_push_accepts_valid_hmac_signature() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_with_push_key(upstream).await;
    let client = client();

    let new_config = format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}",
            "rules": [{{"id": "b", "priority": 1, "action": "block",
                        "conditions": [{{"type": "path_prefix", "value": "/signed"}}]}}]}}]}}"#
    );

    let response = push_signed(proxy, &sign_body(&new_config), &new_config).await;
    assert_eq!(response.status(), StatusCode::OK);

    let response = get(&client, proxy, "/signed").await;
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
}

#[tokio::test]
async fn config_push_rejects_invalid_hmac_signature() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_with_push_key(upstream).await;
    let client = client();

    let new_config = format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}",
            "rules": [{{"id": "b", "priority": 1, "action": "block", "conditions": []}}]}}]}}"#
    );

    // Signature over different bytes than the body that arrives.
    let response = push_signed(proxy, &sign_body("something-else"), &new_config).await;
    assert_eq!(response.status(), StatusCode::UNAUTHORIZED);

    // Old (empty) rule set still serving.
    let response = get(&client, proxy, "/anything").await;
    assert_eq!(response.status(), StatusCode::OK);
}

#[tokio::test]
async fn config_push_disabled_without_node_token() {
    let upstream = spawn_upstream().await;
    // spawn_proxy uses AppState::new — node token comes from the environment,
    // which is unset in tests → push receiver disabled.
    let proxy = spawn_proxy(upstream).await;

    let response = push_config(proxy, Some(NODE_TOKEN), r#"{"zones": []}"#).await;
    assert_eq!(response.status(), StatusCode::FORBIDDEN);
}

#[tokio::test]
async fn emits_analytics_records_for_proxied_and_blocked_requests() {
    use veil_edge::analytics::LogBuffer;

    let upstream = spawn_upstream().await;
    let config = Config::from_json(&format!(
        r#"{{"zones": [{{"name": "test", "hosts": ["*"],
            "upstream": "http://{upstream}",
            "rules": [{{"id": "block-admin", "priority": 10, "action": "block",
                        "conditions": [{{"type": "path_prefix", "value": "/admin"}}]}}]}}]}}"#
    ))
    .unwrap();

    let buffer = Arc::new(LogBuffer::default());
    let mut state = AppState::with_node_token(config, None);
    state.analytics = Some(Arc::clone(&buffer));

    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let proxy = listener.local_addr().unwrap();
    tokio::spawn(proxy::serve(listener, Arc::new(state)));
    let client = client();

    assert_eq!(get(&client, proxy, "/hello").await.status(), StatusCode::OK);
    assert_eq!(
        get(&client, proxy, "/admin/x").await.status(),
        StatusCode::FORBIDDEN
    );

    let records = buffer.drain(10);
    assert_eq!(records.len(), 2);

    assert_eq!(records[0].zone, "test");
    assert_eq!(records[0].path, "/hello");
    assert_eq!(records[0].status, 200);
    assert_eq!(records[0].verdict, "allow");
    assert_eq!(records[0].rule_id, None);

    assert_eq!(records[1].path, "/admin/x");
    assert_eq!(records[1].status, 403);
    assert_eq!(records[1].verdict, "block");
    assert_eq!(records[1].rule_id.as_deref(), Some("block-admin"));
}

async fn push_acme(proxy: SocketAddr, token: Option<&str>, body: &str) -> Response<Incoming> {
    let mut builder = Request::builder()
        .method(hyper::Method::POST)
        .uri(format!("http://{proxy}/_veil/internal/acme-challenge"))
        .header(hyper::header::CONTENT_TYPE, "application/json");
    if let Some(token) = token {
        builder = builder.header("x-veil-node-token", token);
    }
    let req = builder.body(Full::new(Bytes::from(body.to_owned()))).unwrap();

    let post_client: Client<hyper_util::client::legacy::connect::HttpConnector, Full<Bytes>> =
        Client::builder(TokioExecutor::new()).build_http();
    post_client.request(req).await.unwrap()
}

#[tokio::test]
async fn acme_push_then_http01_served_before_rules() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_with_token(upstream).await;
    let client = client();

    // Unknown token → 404 (not proxied to upstream).
    let response = get(&client, proxy, "/.well-known/acme-challenge/tok").await;
    assert_eq!(response.status(), StatusCode::NOT_FOUND);

    // Bad credentials are rejected.
    let body = r#"{"challenges":[{"token":"tok","keyAuthorization":"tok.thumbprint"}]}"#;
    let response = push_acme(proxy, Some("wrong-token"), body).await;
    assert_eq!(response.status(), StatusCode::UNAUTHORIZED);

    // Publish, then the key authorization is served as plain text.
    let response = push_acme(proxy, Some(NODE_TOKEN), body).await;
    assert_eq!(response.status(), StatusCode::OK);

    let response = get(&client, proxy, "/.well-known/acme-challenge/tok").await;
    assert_eq!(response.status(), StatusCode::OK);
    let bytes = response.into_body().collect().await.unwrap().to_bytes();
    assert_eq!(&bytes[..], b"tok.thumbprint");

    // An empty publish clears the set.
    let response = push_acme(proxy, Some(NODE_TOKEN), r#"{"challenges":[]}"#).await;
    assert_eq!(response.status(), StatusCode::OK);
    let response = get(&client, proxy, "/.well-known/acme-challenge/tok").await;
    assert_eq!(response.status(), StatusCode::NOT_FOUND);
}

#[tokio::test]
async fn health_probes_respond_without_zone_resolution() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy(upstream).await;
    let client = client();

    // Both liveness and readiness are green: spawn_proxy loads a zone.
    let response = get(&client, proxy, "/healthz").await;
    assert_eq!(response.status(), StatusCode::OK);
    let response = get(&client, proxy, "/readyz").await;
    assert_eq!(response.status(), StatusCode::OK);
}

#[tokio::test]
async fn readyz_unavailable_without_zones() {
    let config = Config::from_json(r#"{"zones": []}"#).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(proxy::serve(listener, Arc::new(AppState::new(config))));
    let client = client();

    // Liveness is still green; readiness reports no servable config.
    let response = get(&client, addr, "/healthz").await;
    assert_eq!(response.status(), StatusCode::OK);
    let response = get(&client, addr, "/readyz").await;
    assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
}

#[tokio::test]
async fn metrics_endpoint_reports_request_counters() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy(upstream).await;
    let client = client();

    // Generate one allow and one block (spawn_proxy blocks /admin).
    let _ = get(&client, proxy, "/ok").await;
    let _ = get(&client, proxy, "/admin/secret").await;

    let response = get(&client, proxy, "/metrics").await;
    assert_eq!(response.status(), StatusCode::OK);
    let body = response.into_body().collect().await.unwrap().to_bytes();
    let text = String::from_utf8(body.to_vec()).unwrap();

    assert!(text.contains("veil_requests_total{verdict=\"allow\"} 1"));
    assert!(text.contains("veil_requests_total{verdict=\"block\"} 1"));
    assert!(text.contains("veil_request_duration_seconds_count"));
}

#[tokio::test]
async fn graceful_shutdown_drains_then_stops_accepting() {
    use tokio::sync::watch;

    let upstream = spawn_upstream().await;
    let config = Config::from_json(&format!(
        r#"{{"zones": [{{"name": "t", "hosts": ["*"], "upstream": "http://{upstream}", "rules": []}}]}}"#
    ))
    .unwrap();

    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    let (tx, mut rx) = watch::channel(false);
    let server = tokio::spawn(async move {
        proxy::serve_with_shutdown(listener, Arc::new(AppState::new(config)), async move {
            let _ = rx.changed().await;
        })
        .await
    });

    let client = client();
    // In-flight request before shutdown succeeds.
    assert_eq!(get(&client, addr, "/ok").await.status(), StatusCode::OK);

    // Signal shutdown; the serve future returns once drained.
    tx.send(true).unwrap();
    let result = tokio::time::timeout(std::time::Duration::from_secs(5), server)
        .await
        .expect("serve did not return after shutdown");
    assert!(result.unwrap().is_ok());

    // The listener is dropped, so new connections are refused.
    let refused = client.get(format!("http://{addr}/ok").parse().unwrap()).await;
    assert!(refused.is_err(), "listener should be closed after shutdown");
}

// ── Managed signature rule set (WAF) ─────────────────────────────────

async fn spawn_proxy_managed(upstream: SocketAddr) -> SocketAddr {
    let config = Config::from_json(&format!(
        r#"{{
            "zones": [{{
                "name": "waf",
                "hosts": ["*"],
                "upstream": "http://{upstream}",
                "managed_rules": {{
                    "sql_injection": true,
                    "xss": true,
                    "path_traversal": true,
                    "inspect_body": true,
                    "action": "block"
                }}
            }}]
        }}"#
    ))
    .unwrap();

    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(proxy::serve(listener, Arc::new(AppState::new(config))));
    addr
}

#[tokio::test]
async fn managed_rules_block_sqli_in_query() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_managed(upstream).await;
    let client = client();

    // Clean request passes through to the upstream.
    assert_eq!(get(&client, proxy, "/products?id=42").await.status(), StatusCode::OK);

    // SQLi payload in the query string is blocked before forwarding.
    let blocked = get(&client, proxy, "/products?id=1%20UNION%20SELECT%20pw%20FROM%20users").await;
    assert_eq!(blocked.status(), StatusCode::FORBIDDEN);
}

#[tokio::test]
async fn managed_rules_block_xss_in_body() {
    let upstream = spawn_upstream().await;
    let proxy = spawn_proxy_managed(upstream).await;
    let body_client: Client<hyper_util::client::legacy::connect::HttpConnector, Full<Bytes>> =
        Client::builder(TokioExecutor::new()).build_http();

    let post = |payload: &'static str| {
        let req = Request::builder()
            .method("POST")
            .uri(format!("http://{proxy}/comment"))
            .header(hyper::header::CONTENT_TYPE, "application/x-www-form-urlencoded")
            .body(Full::new(Bytes::from(payload)))
            .unwrap();
        body_client.request(req)
    };

    // Clean body is forwarded.
    assert_eq!(post("text=hello+world").await.unwrap().status(), StatusCode::OK);

    // XSS in the body is blocked once buffered and inspected.
    let blocked = post("text=<script>steal(document.cookie)</script>").await.unwrap();
    assert_eq!(blocked.status(), StatusCode::FORBIDDEN);
}
