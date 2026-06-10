//! Core listener and per-request orchestration:
//! accept → inspect → evaluate rules → dispatch (forward / block / challenge).

use std::convert::Infallible;
use std::net::SocketAddr;
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
use crate::config::Config;
use crate::pipeline::rate_limit::RateLimiter;
use crate::pipeline::router::UpstreamClient;
use crate::pipeline::{inspector, router, rules, Verdict};
use crate::response::{forbidden, json_response, rate_limited, text, ProxyBody};

pub struct AppState {
    pub config: Config,
    pub limiter: RateLimiter,
    pub client: UpstreamClient,
    pub challenge: ChallengeEngine,
}

impl AppState {
    pub fn new(config: Config) -> Self {
        let cookie_name =
            std::env::var("VEIL_CHALLENGE_COOKIE").unwrap_or_else(|_| "veil_pass".to_owned());
        let cookie_ttl = std::env::var("VEIL_CHALLENGE_TTL")
            .unwrap_or_else(|_| "600".to_owned())
            .parse::<u32>()
            .unwrap_or(600);

        Self {
            config,
            limiter: RateLimiter::new(),
            client: Client::builder(TokioExecutor::new()).build_http(),
            challenge: ChallengeEngine::new(cookie_name, cookie_ttl),
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
    let ctx = inspector::inspect(&req, peer, state.config.trust_forwarded_headers);

    // ── Reserved path: PoW challenge verification ─────────────────────
    if ctx.path == CHALLENGE_VERIFY_PATH && req.method() == Method::POST {
        return handle_challenge_verify(req, &ctx, &state).await;
    }

    let Some(zone) = state.config.resolve_zone(&ctx.host) else {
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
