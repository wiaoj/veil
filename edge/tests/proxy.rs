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

    // First visit: interstitial with the nonce embedded in the JS.
    let response = get(&client, proxy, "/login").await;
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
    
    // Solve the PoW (default difficulty 20 is too slow for tests, but we can brute-force 
    // it since it's just a test, or we should maybe set the env var for difficulty in proxy spawn...
    // Let's just brute-force it, it takes ~50ms usually. Wait, default is 20. 
    // Actually, in test AppState::new(), difficulty uses env var VEIL_POW_DIFFICULTY or 20.
    // If it takes too long in tests, we could inject a lower difficulty. Let's just solve it.)
    // Wait, the difficulty is read from env var inside AppState::new(). We can set it to 8 for the test.
    // However, since we don't want to mess with env vars globally here in async test, we'll just solve it.
    // Let's see if we can find it quickly. If it takes too long we will fix the env var.
    // Actually, in `spawn_proxy` we don't set the env var, so it's 20. 20 means ~1 million iterations.
    // In Rust debug mode, 1M SHA256 might take 1-2 seconds.
    let mut counter = 0;
    while !veil_edge::challenge::pow::verify_pow(&nonce_bytes, counter, 20) {
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
