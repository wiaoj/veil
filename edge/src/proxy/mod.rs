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

/// Accept loop. Each connection gets its own task; HTTP/1.1 and HTTP/2 are
/// negotiated automatically.
pub async fn serve(listener: TcpListener, state: Arc<AppState>) -> std::io::Result<()> {
    loop {
        let (stream, peer) = listener.accept().await?;
        let state = Arc::clone(&state);
        tokio::spawn(async move {
            let io = TokioIo::new(stream);
            let service = service_fn(move |req| {
                let state = Arc::clone(&state);
                async move { Ok::<_, Infallible>(handle(req, peer, state).await) }
            });
            if let Err(err) = auto::Builder::new(TokioExecutor::new())
                .serve_connection_with_upgrades(io, service)
                .await
            {
                debug!(%peer, error = %err, "connection closed with error");
            }
        });
    }
}

/// TLS accept loop: same per-connection handling as [`serve`], with a TLS
/// handshake in front. A failed handshake only costs that connection.
pub async fn serve_tls(
    listener: TcpListener,
    acceptor: tokio_rustls::TlsAcceptor,
    state: Arc<AppState>,
) -> std::io::Result<()> {
    loop {
        let (stream, peer) = listener.accept().await?;
        let acceptor = acceptor.clone();
        let state = Arc::clone(&state);
        tokio::spawn(async move {
            let tls_stream = match acceptor.accept(stream).await {
                Ok(s) => s,
                Err(err) => {
                    debug!(%peer, error = %err, "tls handshake failed");
                    return;
                }
            };
            let io = TokioIo::new(tls_stream);
            let service = service_fn(move |req| {
                let state = Arc::clone(&state);
                async move { Ok::<_, Infallible>(handle(req, peer, state).await) }
            });
            if let Err(err) = auto::Builder::new(TokioExecutor::new())
                .serve_connection_with_upgrades(io, service)
                .await
            {
                debug!(%peer, error = %err, "tls connection closed with error");
            }
        });
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

    // ── Reserved path: PoW challenge verification ─────────────────────
    if ctx.path == CHALLENGE_VERIFY_PATH && req.method() == Method::POST {
        return handle_challenge_verify(req, &ctx, &state).await;
    }

    // ── Reserved path: control-plane config push ──────────────────────
    if ctx.path == CONFIG_PUSH_PATH && req.method() == Method::POST {
        return handle_config_push(req, &ctx, &state).await;
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
                state.challenge.issue_challenge(ctx.client_ip)
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
            text(StatusCode::BAD_GATEWAY, "502 bad gateway\n")
        }
    }
}
