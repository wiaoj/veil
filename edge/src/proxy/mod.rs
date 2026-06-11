//! Core listener and per-request orchestration:
//! accept → inspect → evaluate rules → dispatch (forward / block / challenge).

use std::convert::Infallible;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Instant;

use http_body_util::BodyExt;
use hyper::body::Incoming;
use hyper::service::service_fn;
use hyper::{Method, Request, Response, StatusCode};
use hyper_util::client::legacy::Client;
use hyper_util::rt::{TokioExecutor, TokioIo};
use hyper_util::server::conn::auto;
use tokio::net::TcpListener;
use tracing::{debug, info, warn};

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

/// Config push payloads larger than this are rejected.
const CONFIG_PUSH_MAX_BYTES: usize = 1024 * 1024;

pub struct AppState {
    pub config: ConfigStore,
    pub limiter: RateLimiter,
    pub client: UpstreamClient,
    pub challenge: ChallengeEngine,
    /// Shared secret authenticating control-plane pushes. `None` disables
    /// the push receiver entirely (local-file mode).
    pub node_token: Option<String>,
    /// Last-known-good snapshot location (`VEIL_CONFIG_CACHE`). `None`
    /// disables cache writes.
    pub config_cache_path: Option<PathBuf>,
}

impl AppState {
    pub fn new(config: Config) -> Self {
        Self::with_options(
            config,
            std::env::var("VEIL_NODE_TOKEN").ok(),
            cache::path_from_env(),
        )
    }

    pub fn with_node_token(config: Config, node_token: Option<String>) -> Self {
        Self::with_options(config, node_token, None)
    }

    pub fn with_options(
        config: Config,
        node_token: Option<String>,
        config_cache_path: Option<PathBuf>,
    ) -> Self {
        let cookie_name =
            std::env::var("VEIL_CHALLENGE_COOKIE").unwrap_or_else(|_| "veil_pass".to_owned());
        let cookie_ttl = std::env::var("VEIL_CHALLENGE_TTL")
            .unwrap_or_else(|_| "600".to_owned())
            .parse::<u32>()
            .unwrap_or(600);

        Self {
            config: ConfigStore::new(config),
            limiter: RateLimiter::new(),
            client: Client::builder(TokioExecutor::new()).build_http(),
            challenge: ChallengeEngine::new(cookie_name, cookie_ttl),
            node_token,
            config_cache_path,
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

pub async fn handle(
    req: Request<Incoming>,
    peer: SocketAddr,
    state: Arc<AppState>,
) -> Response<ProxyBody> {
    let started = Instant::now();
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
    response
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

/// Handle `POST /_veil/internal/config` — authenticate the control plane via
/// the shared node token and atomically swap in the pushed config snapshot.
async fn handle_config_push(
    req: Request<Incoming>,
    ctx: &crate::pipeline::RequestContext,
    state: &AppState,
) -> Response<ProxyBody> {
    let Some(expected_token) = state.node_token.as_deref() else {
        return json_response(
            StatusCode::FORBIDDEN,
            r#"{"error":"push_disabled","detail":"Node runs from a local config file; no node token configured."}"#,
        );
    };

    let provided = req
        .headers()
        .get(NODE_TOKEN_HEADER)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");
    if !constant_time_eq(provided.as_bytes(), expected_token.as_bytes()) {
        warn!(client_ip = %ctx.client_ip, "config push with invalid node token rejected");
        return json_response(
            StatusCode::UNAUTHORIZED,
            r#"{"error":"invalid_token","detail":"Node token missing or invalid."}"#,
        );
    }

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
