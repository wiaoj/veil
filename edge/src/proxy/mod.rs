//! Core listener and per-request orchestration:
//! accept → inspect → evaluate rules → dispatch (forward / block / challenge).

use std::convert::Infallible;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Instant;

use hmac::{Hmac, Mac};
use http_body_util::BodyExt;
use hyper::body::Incoming;
use hyper::service::service_fn;
use hyper::{Method, Request, Response, StatusCode};
use hyper_util::client::legacy::Client;
use hyper_util::rt::{TokioExecutor, TokioIo};
use hyper_util::server::conn::auto;
use sha2::Sha256;
use tokio::net::TcpListener;
use tracing::{debug, info, warn};

use crate::acme::{AcmeStore, ChallengeSet, ACME_PUSH_PATH, HTTP01_PATH_PREFIX};
use crate::analytics::{self, LogBuffer, LogRecord};
use crate::challenge::{ChallengeEngine, VerifySolutionRequest, CHALLENGE_VERIFY_PATH};
use crate::config::cache;
use crate::config::store::ConfigStore;
use crate::config::sync::NODE_TOKEN_HEADER;
use crate::config::Config;
use crate::pipeline::rate_limit::RateLimiter;
use crate::pipeline::router::UpstreamClient;
use crate::pipeline::{inspector, router, rules, Verdict};
use crate::response::{forbidden, json_response, rate_limited, text, ProxyBody};

/// Reserved path where the control plane pushes config updates at runtime.
pub const CONFIG_PUSH_PATH: &str = "/_veil/internal/config";

/// Default header carrying the HMAC-SHA256 signature of the push body.
/// Overridable via `VEIL_PUSH_SIGNATURE_HEADER` (must match the control
/// plane's `ConfigSync:SignatureHeader`).
pub const DEFAULT_SIGNATURE_HEADER: &str = "x-veil-signature";

/// Config push payloads larger than this are rejected.
const CONFIG_PUSH_MAX_BYTES: usize = 1024 * 1024;

/// Per-IP budget for the push endpoint: it is reachable on the public
/// listener, so unauthenticated callers must not be able to make the node
/// buffer large bodies at line rate.
const CONFIG_PUSH_RATE_LIMIT: u32 = 10;
const CONFIG_PUSH_RATE_WINDOW_SECS: u64 = 60;

pub struct AppState {
    pub config: ConfigStore,
    pub limiter: RateLimiter,
    pub client: UpstreamClient,
    pub challenge: ChallengeEngine,
    /// Shared secret authenticating control-plane pushes. `None` disables
    /// the push receiver entirely (local-file mode).
    pub node_token: Option<String>,
    /// Shared HMAC key (`VEIL_PUSH_HMAC_KEY`) verifying signed config
    /// pushes from the ConfigSync worker.
    pub push_hmac_key: Option<[u8; 32]>,
    /// Header carrying the push body signature.
    pub signature_header: String,
    /// Last-known-good snapshot location (`VEIL_CONFIG_CACHE`). `None`
    /// disables cache writes.
    pub config_cache_path: Option<PathBuf>,
    /// Request log buffer drained by the analytics shipper. `None` disables
    /// emission entirely (no `VEIL_ANALYTICS_URL`).
    pub analytics: Option<Arc<LogBuffer>>,
    /// Active ACME HTTP-01 challenges published by the control plane.
    pub acme: AcmeStore,
    /// SNI certificate resolver fed by config pushes. `None` when no HTTPS
    /// listener is running (nothing to update).
    pub cert_resolver: Option<Arc<crate::tls::DynamicCertResolver>>,
    /// Prometheus metrics, exposed on `GET /metrics`.
    pub metrics: crate::metrics::Metrics,
}

impl AppState {
    pub fn new(config: Config) -> Self {
        let mut state = Self::with_options(
            config,
            std::env::var("VEIL_NODE_TOKEN").ok(),
            cache::path_from_env(),
            push_key_from_env(),
        );
        state.analytics = analytics::buffer_from_env();
        state
    }

    pub fn with_node_token(config: Config, node_token: Option<String>) -> Self {
        Self::with_options(config, node_token, None, None)
    }

    pub fn with_options(
        config: Config,
        node_token: Option<String>,
        config_cache_path: Option<PathBuf>,
        push_hmac_key: Option<[u8; 32]>,
    ) -> Self {
        let cookie_name =
            std::env::var("VEIL_CHALLENGE_COOKIE").unwrap_or_else(|_| "veil_pass".to_owned());
        let cookie_ttl = std::env::var("VEIL_CHALLENGE_TTL")
            .unwrap_or_else(|_| "600".to_owned())
            .parse::<u32>()
            .unwrap_or(600);

        let signature_header = std::env::var("VEIL_PUSH_SIGNATURE_HEADER")
            .map(|h| h.to_ascii_lowercase())
            .unwrap_or_else(|_| DEFAULT_SIGNATURE_HEADER.to_owned());

        Self {
            config: ConfigStore::new(config),
            limiter: RateLimiter::new(),
            client: Client::builder(TokioExecutor::new()).build_http(),
            challenge: ChallengeEngine::new(cookie_name, cookie_ttl),
            node_token,
            push_hmac_key,
            signature_header,
            config_cache_path,
            analytics: None,
            acme: AcmeStore::new(),
            cert_resolver: None,
            metrics: crate::metrics::Metrics::new(),
        }
    }
}

/// Parses `VEIL_PUSH_HMAC_KEY` (64 hex chars). An invalid value disables
/// signature verification with a warning rather than silently truncating.
fn push_key_from_env() -> Option<[u8; 32]> {
    let hex = std::env::var("VEIL_PUSH_HMAC_KEY").ok()?;
    match crate::challenge::pow::from_hex(&hex).and_then(|b| <[u8; 32]>::try_from(b).ok()) {
        Some(key) => Some(key),
        None => {
            warn!("VEIL_PUSH_HMAC_KEY must be 64 hex chars; push signature verification disabled");
            None
        }
    }
}

/// Maximum time to wait for in-flight requests to finish after a shutdown
/// signal before forcing exit.
const DRAIN_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(30);

/// Accept loop without graceful shutdown — used by tests, which drop the
/// runtime to stop the listener.
pub async fn serve(listener: TcpListener, state: Arc<AppState>) -> std::io::Result<()> {
    serve_with_shutdown(listener, state, std::future::pending::<()>()).await
}

/// Accept loop with graceful drain. Stops accepting on `shutdown`, then waits
/// up to [`DRAIN_TIMEOUT`] for in-flight connections to complete. HTTP/1.1
/// and HTTP/2 are negotiated automatically.
pub async fn serve_with_shutdown(
    listener: TcpListener,
    state: Arc<AppState>,
    shutdown: impl std::future::Future<Output = ()>,
) -> std::io::Result<()> {
    let graceful = hyper_util::server::graceful::GracefulShutdown::new();
    let builder = auto::Builder::new(TokioExecutor::new());
    let mut shutdown = std::pin::pin!(shutdown);

    loop {
        tokio::select! {
            accepted = listener.accept() => {
                let (stream, peer) = accepted?;
                let state = Arc::clone(&state);
                let service = service_fn(move |req| {
                    let state = Arc::clone(&state);
                    async move { Ok::<_, Infallible>(handle(req, peer, state).await) }
                });
                let conn = builder.serve_connection_with_upgrades(TokioIo::new(stream), service);
                let conn = graceful.watch(conn.into_owned());
                tokio::spawn(async move {
                    if let Err(err) = conn.await {
                        debug!(%peer, error = %err, "connection closed with error");
                    }
                });
            }
            () = &mut shutdown => break,
        }
    }

    drain(graceful).await;
    Ok(())
}

/// TLS accept loop with graceful drain: same handling as
/// [`serve_with_shutdown`], with a TLS handshake in front. A failed handshake
/// only costs that connection.
pub async fn serve_tls(
    listener: TcpListener,
    acceptor: tokio_rustls::TlsAcceptor,
    state: Arc<AppState>,
    shutdown: impl std::future::Future<Output = ()>,
) -> std::io::Result<()> {
    let graceful = hyper_util::server::graceful::GracefulShutdown::new();
    let builder = auto::Builder::new(TokioExecutor::new());
    let mut shutdown = std::pin::pin!(shutdown);

    loop {
        tokio::select! {
            accepted = listener.accept() => {
                let (stream, peer) = accepted?;
                let acceptor = acceptor.clone();
                let state = Arc::clone(&state);
                // Handshake inline; a failed handshake only costs this
                // connection. Watched connections are tracked for draining.
                let tls_stream = match acceptor.accept(stream).await {
                    Ok(s) => s,
                    Err(err) => {
                        debug!(%peer, error = %err, "tls handshake failed");
                        continue;
                    }
                };
                let service = service_fn(move |req| {
                    let state = Arc::clone(&state);
                    async move { Ok::<_, Infallible>(handle(req, peer, state).await) }
                });
                let conn = builder.serve_connection_with_upgrades(TokioIo::new(tls_stream), service);
                let conn = graceful.watch(conn.into_owned());
                tokio::spawn(async move {
                    if let Err(err) = conn.await {
                        debug!(%peer, error = %err, "tls connection closed with error");
                    }
                });
            }
            () = &mut shutdown => break,
        }
    }

    drain(graceful).await;
    Ok(())
}

/// Waits for tracked connections to finish, bounded by [`DRAIN_TIMEOUT`].
async fn drain(graceful: hyper_util::server::graceful::GracefulShutdown) {
    info!("shutdown signalled; draining in-flight connections");
    tokio::select! {
        () = graceful.shutdown() => info!("all connections drained"),
        () = tokio::time::sleep(DRAIN_TIMEOUT) => {
            warn!(timeout_secs = DRAIN_TIMEOUT.as_secs(), "drain timed out; forcing shutdown");
        }
    }
}

pub async fn handle(
    req: Request<Incoming>,
    peer: SocketAddr,
    state: Arc<AppState>,
) -> Response<ProxyBody> {
    let started = Instant::now();
    let ts_ms = analytics::now_ms();
    // Snapshot for the lifetime of this request; concurrent config pushes
    // never affect a request mid-flight.
    let config = state.config.load();
    let ctx = inspector::inspect(&req, peer, config.trust_forwarded_headers);

    // ── Reserved path: Prometheus metrics scrape ─────────────────────
    if req.method() == Method::GET && ctx.path == "/metrics" {
        return text(StatusCode::OK, &state.metrics.render());
    }

    // ── Reserved paths: health probes (no zone resolution / logging) ──
    if req.method() == Method::GET && (ctx.path == "/healthz" || ctx.path == "/readyz") {
        // Liveness: the listener is accepting. Readiness: a config with at
        // least one zone is loaded (a node with no zones can serve nothing).
        if ctx.path == "/healthz" || !config.zones.is_empty() {
            return text(StatusCode::OK, "ok\n");
        }
        return text(StatusCode::SERVICE_UNAVAILABLE, "no config\n");
    }

    // ── Reserved path: PoW challenge verification ─────────────────────
    if ctx.path == CHALLENGE_VERIFY_PATH && req.method() == Method::POST {
        return handle_challenge_verify(req, &ctx, &state).await;
    }

    // ── Reserved path: control-plane config push ──────────────────────
    if ctx.path == CONFIG_PUSH_PATH && req.method() == Method::POST {
        return handle_config_push(req, &ctx, &state).await;
    }

    // ── Reserved path: control-plane ACME challenge publish ───────────
    if ctx.path == ACME_PUSH_PATH && req.method() == Method::POST {
        return handle_acme_push(req, &ctx, &state).await;
    }

    // ── ACME HTTP-01 validation — answered before any zone/rule logic so
    // a block or challenge rule can never break certificate issuance.
    if req.method() == Method::GET {
        if let Some(token) = ctx.path.strip_prefix(HTTP01_PATH_PREFIX) {
            return match state.acme.key_authorization(token) {
                Some(key_auth) => text(StatusCode::OK, &key_auth),
                None => text(StatusCode::NOT_FOUND, "404 unknown acme token\n"),
            };
        }
    }

    let Some(zone) = config.resolve_zone(&ctx.host) else {
        let response = text(StatusCode::MISDIRECTED_REQUEST, "421 unknown host\n");
        info!(
            zone = "-",
            method = %ctx.method,
            path = %ctx.path,
            status = response.status().as_u16(),
            verdict = "no_zone",
            client_ip = %ctx.client_ip,
            total_ms = started.elapsed().as_millis() as u64,
            "request"
        );
        state.metrics.record_request("no_zone", started.elapsed().as_secs_f64());
        record_request(&state, &ctx, "-", "no_zone", None, response.status().as_u16(), ts_ms, started);
        return response;
    };

    let verdict = rules::evaluate(zone, &ctx, &state.limiter);
    let mut label = verdict.label();
    let rule_id = verdict.rule_id().map(str::to_owned);

    let response = match &verdict {
        Verdict::Allow => forward_or_502(req, &ctx, &zone.upstream, &state).await,
        Verdict::Challenge { .. } => {
            if state.challenge.verify_token(req.headers(), ctx.client_ip) {
                label = "challenge_pass";
                forward_or_502(req, &ctx, &zone.upstream, &state).await
            } else {
                state.challenge.issue_challenge(&ctx)
            }
        }
        Verdict::Block { .. } => forbidden(ctx.wants_html()),
        Verdict::RateLimited { rule_id } => {
            let retry_after = zone
                .rules
                .iter()
                .find(|r| &r.id == rule_id)
                .and_then(|r| r.rate_limit)
                .map_or(60, |p| p.window_secs);
            rate_limited(retry_after, ctx.wants_html())
        }
    };

    info!(
        zone = %zone.name,
        method = %ctx.method,
        path = %ctx.path,
        status = response.status().as_u16(),
        verdict = label,
        rule_id = rule_id.as_deref().unwrap_or("-"),
        client_ip = %ctx.client_ip,
        total_ms = started.elapsed().as_millis() as u64,
        "request"
    );
    state.metrics.record_request(label, started.elapsed().as_secs_f64());
    record_request(
        &state,
        &ctx,
        &zone.name,
        label,
        rule_id,
        response.status().as_u16(),
        ts_ms,
        started,
    );
    response
}

/// Queues one analytics record. No-op when emission is disabled; reserved
/// `/_veil/*` paths never reach here (they return before zone resolution).
#[allow(clippy::too_many_arguments)]
fn record_request(
    state: &AppState,
    ctx: &crate::pipeline::RequestContext,
    zone: &str,
    verdict: &'static str,
    rule_id: Option<String>,
    status: u16,
    ts_ms: u64,
    started: Instant,
) {
    let Some(buffer) = &state.analytics else {
        return;
    };
    buffer.push(LogRecord {
        ts_ms,
        zone: zone.to_owned(),
        host: ctx.host.clone(),
        method: ctx.method.to_string(),
        path: ctx.path.clone(),
        status,
        verdict,
        rule_id,
        client_ip: ctx.client_ip.to_string(),
        user_agent: ctx.user_agent.clone(),
        duration_ms: started.elapsed().as_millis() as u64,
    });
}

/// Handle `POST /_veil/challenge/verify` — validate PoW solution and issue cookie.
async fn handle_challenge_verify(
    req: Request<Incoming>,
    ctx: &crate::pipeline::RequestContext,
    state: &AppState,
) -> Response<ProxyBody> {
    // Read body (limit to 1KB to prevent abuse)
    let body_bytes = match req.collect().await {
        Ok(collected) => collected.to_bytes(),
        Err(_) => {
            return json_response(
                StatusCode::BAD_REQUEST,
                r#"{"error":"body_read_failed","detail":"İstek gövdesi okunamadı."}"#,
            );
        }
    };

    if body_bytes.len() > 1024 {
        return json_response(
            StatusCode::PAYLOAD_TOO_LARGE,
            r#"{"error":"body_too_large","detail":"İstek gövdesi çok büyük."}"#,
        );
    }

    let solution: VerifySolutionRequest = match serde_json::from_slice(&body_bytes) {
        Ok(s) => s,
        Err(_) => {
            return json_response(
                StatusCode::BAD_REQUEST,
                r#"{"error":"invalid_json","detail":"Geçersiz JSON formatı."}"#,
            );
        }
    };

    info!(
        nonce = %solution.nonce,
        client_ip = %ctx.client_ip,
        "challenge verify attempt"
    );

    state.challenge.verify_solution(&solution, ctx.client_ip)
}

/// Handle `POST /_veil/internal/config` — authenticate the pusher (node
/// token for operators, HMAC body signature for the ConfigSync worker) and
/// atomically swap in the pushed config snapshot.
///
/// The path is reachable on the public listener, so everything before the
/// body read must be cheap: requests without a credential header are
/// rejected immediately and a per-IP budget caps how often anyone can make
/// this node buffer a payload.
async fn handle_config_push(
    req: Request<Incoming>,
    ctx: &crate::pipeline::RequestContext,
    state: &AppState,
) -> Response<ProxyBody> {
    if state.node_token.is_none() && state.push_hmac_key.is_none() {
        return json_response(
            StatusCode::FORBIDDEN,
            r#"{"error":"push_disabled","detail":"Node runs from a local config file; no push credentials configured."}"#,
        );
    }

    let provided_token = req
        .headers()
        .get(NODE_TOKEN_HEADER)
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned);
    let provided_signature = req
        .headers()
        .get(state.signature_header.as_str())
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned);

    if provided_token.is_none() && provided_signature.is_none() {
        warn!(client_ip = %ctx.client_ip, "config push without credentials rejected");
        return json_response(
            StatusCode::UNAUTHORIZED,
            r#"{"error":"missing_credentials","detail":"Node token or push signature required."}"#,
        );
    }

    let rate_key = format!("_veil:config_push:{}", ctx.client_ip);
    if !state
        .limiter
        .allow(&rate_key, CONFIG_PUSH_RATE_LIMIT, CONFIG_PUSH_RATE_WINDOW_SECS)
    {
        warn!(client_ip = %ctx.client_ip, "config push rate limit exceeded");
        return crate::response::rate_limited(CONFIG_PUSH_RATE_WINDOW_SECS, false);
    }

    // Token check is cheap and body-independent; do it before the read so a
    // valid operator token never depends on signature material.
    let token_ok = matches!(
        (&state.node_token, &provided_token),
        (Some(expected), Some(provided))
            if constant_time_eq(provided.as_bytes(), expected.as_bytes())
    );

    let body_bytes = match req.collect().await {
        Ok(collected) => collected.to_bytes(),
        Err(_) => {
            return json_response(
                StatusCode::BAD_REQUEST,
                r#"{"error":"body_read_failed","detail":"Request body could not be read."}"#,
            );
        }
    };

    if body_bytes.len() > CONFIG_PUSH_MAX_BYTES {
        return json_response(
            StatusCode::PAYLOAD_TOO_LARGE,
            r#"{"error":"body_too_large","detail":"Config payload exceeds 1 MiB."}"#,
        );
    }

    let signature_ok = matches!(
        (&state.push_hmac_key, &provided_signature),
        (Some(key), Some(signature)) if verify_push_signature(key, &body_bytes, signature)
    );

    if !token_ok && !signature_ok {
        warn!(client_ip = %ctx.client_ip, "config push with invalid credentials rejected");
        return json_response(
            StatusCode::UNAUTHORIZED,
            r#"{"error":"invalid_credentials","detail":"Node token or push signature invalid."}"#,
        );
    }

    let raw = match std::str::from_utf8(&body_bytes) {
        Ok(raw) => raw,
        Err(_) => {
            return json_response(
                StatusCode::BAD_REQUEST,
                r#"{"error":"invalid_utf8","detail":"Config payload is not valid UTF-8."}"#,
            );
        }
    };

    match Config::from_json(raw) {
        Ok(config) => {
            let zones = config.zones.len();
            if let Some(resolver) = &state.cert_resolver {
                resolver.update_from_config(&config);
            }
            state.config.swap(config);
            if let Some(path) = &state.config_cache_path {
                cache::store(path, raw);
            }
            info!(zones, client_ip = %ctx.client_ip, "config push applied");
            json_response(StatusCode::OK, &format!(r#"{{"ok":true,"zones":{zones}}}"#))
        }
        Err(err) => {
            warn!(error = %err, "config push rejected: invalid config");
            json_response(
                StatusCode::BAD_REQUEST,
                &format!(r#"{{"error":"invalid_config","detail":"{err}"}}"#).replace('\n', " "),
            )
        }
    }
}

/// Handle `POST /_veil/internal/acme-challenge` — same credentials and
/// hardening as the config push (header precheck, per-IP budget), then
/// atomically replace the active HTTP-01 challenge set.
async fn handle_acme_push(
    req: Request<Incoming>,
    ctx: &crate::pipeline::RequestContext,
    state: &AppState,
) -> Response<ProxyBody> {
    if state.node_token.is_none() && state.push_hmac_key.is_none() {
        return json_response(
            StatusCode::FORBIDDEN,
            r#"{"error":"push_disabled","detail":"No push credentials configured."}"#,
        );
    }

    let provided_token = req
        .headers()
        .get(NODE_TOKEN_HEADER)
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned);
    let provided_signature = req
        .headers()
        .get(state.signature_header.as_str())
        .and_then(|v| v.to_str().ok())
        .map(str::to_owned);

    if provided_token.is_none() && provided_signature.is_none() {
        warn!(client_ip = %ctx.client_ip, "acme push without credentials rejected");
        return json_response(
            StatusCode::UNAUTHORIZED,
            r#"{"error":"missing_credentials","detail":"Node token or push signature required."}"#,
        );
    }

    let rate_key = format!("_veil:acme_push:{}", ctx.client_ip);
    if !state
        .limiter
        .allow(&rate_key, CONFIG_PUSH_RATE_LIMIT, CONFIG_PUSH_RATE_WINDOW_SECS)
    {
        warn!(client_ip = %ctx.client_ip, "acme push rate limit exceeded");
        return crate::response::rate_limited(CONFIG_PUSH_RATE_WINDOW_SECS, false);
    }

    let token_ok = matches!(
        (&state.node_token, &provided_token),
        (Some(expected), Some(provided))
            if constant_time_eq(provided.as_bytes(), expected.as_bytes())
    );

    let body_bytes = match req.collect().await {
        Ok(collected) => collected.to_bytes(),
        Err(_) => {
            return json_response(
                StatusCode::BAD_REQUEST,
                r#"{"error":"body_read_failed","detail":"Request body could not be read."}"#,
            );
        }
    };

    // Challenge sets are tiny; anything bigger than 64 KiB is abuse.
    if body_bytes.len() > 64 * 1024 {
        return json_response(
            StatusCode::PAYLOAD_TOO_LARGE,
            r#"{"error":"body_too_large","detail":"Challenge payload exceeds 64 KiB."}"#,
        );
    }

    let signature_ok = matches!(
        (&state.push_hmac_key, &provided_signature),
        (Some(key), Some(signature)) if verify_push_signature(key, &body_bytes, signature)
    );

    if !token_ok && !signature_ok {
        warn!(client_ip = %ctx.client_ip, "acme push with invalid credentials rejected");
        return json_response(
            StatusCode::UNAUTHORIZED,
            r#"{"error":"invalid_credentials","detail":"Node token or push signature invalid."}"#,
        );
    }

    match serde_json::from_slice::<ChallengeSet>(&body_bytes) {
        Ok(set) => {
            let count = state.acme.replace(set);
            info!(challenges = count, client_ip = %ctx.client_ip, "acme challenge set applied");
            json_response(StatusCode::OK, &format!(r#"{{"ok":true,"challenges":{count}}}"#))
        }
        Err(err) => json_response(
            StatusCode::BAD_REQUEST,
            &format!(r#"{{"error":"invalid_payload","detail":"{err}"}}"#).replace('\n', " "),
        ),
    }
}

/// HMAC-SHA256 over the raw push body with the shared push key.
fn verify_push_signature(key: &[u8; 32], body: &[u8], signature_hex: &str) -> bool {
    let Some(signature) = crate::challenge::pow::from_hex(signature_hex) else {
        return false;
    };
    let mut mac =
        <Hmac<Sha256> as Mac>::new_from_slice(key).expect("hmac accepts any key length");
    mac.update(body);
    mac.verify_slice(&signature).is_ok()
}

/// Constant-time byte comparison for the node token; avoids leaking the
/// match length through response timing.
fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    left.iter().zip(right).fold(0u8, |acc, (l, r)| acc | (l ^ r)) == 0
}

async fn forward_or_502(
    req: Request<Incoming>,
    ctx: &crate::pipeline::RequestContext,
    upstream: &str,
    state: &AppState,
) -> Response<ProxyBody> {
    match router::forward(req, ctx, upstream, &state.client).await {
        Ok(response) => response,
        Err(err) => {
            warn!(upstream, error = %err, "upstream request failed");
            state.metrics.record_upstream_error();
            text(StatusCode::BAD_GATEWAY, "502 bad gateway\n")
        }
    }
}
