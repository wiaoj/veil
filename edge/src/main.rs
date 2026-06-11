use std::sync::Arc;

use tokio::net::TcpListener;
use tracing::{info, warn};
use tracing_subscriber::EnvFilter;

use veil_edge::analytics;
use veil_edge::config::{cache, sync, Config};
use veil_edge::proxy::{self, AppState};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    // Load environment variables from .env file if it exists
    dotenvy::dotenv().ok();

    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_env("VEIL_LOG_LEVEL")
                .or_else(|_| EnvFilter::try_from_default_env())
                .unwrap_or_else(|_| EnvFilter::new("info")),
        )
        .init();

    let config = load_startup_config().await?;

    let listen_addr =
        std::env::var("VEIL_LISTEN_HTTP").unwrap_or_else(|_| "127.0.0.1:8080".to_owned());
    let listener = TcpListener::bind(&listen_addr).await?;

    info!(
        addr = %listen_addr,
        zones = config.zones.len(),
        "veil-edge listening"
    );

    let state = Arc::new(AppState::new(config));

    // TLS termination (Phase 2.2) — enabled by VEIL_TLS_CERT + VEIL_TLS_KEY.
    if let Some(tls) = veil_edge::tls::settings_from_env()? {
        let acceptor = veil_edge::tls::build_acceptor(&tls.cert_pem, &tls.key_pem)?;
        let tls_listener = TcpListener::bind(&tls.listen_addr).await?;
        info!(addr = %tls.listen_addr, "veil-edge listening (https)");
        let tls_state = Arc::clone(&state);
        tokio::spawn(async move {
            if let Err(err) = proxy::serve_tls(tls_listener, acceptor, tls_state).await {
                warn!(error = %err, "https listener terminated");
            }
        });
    }

    // Request log shipping (Phase 2.6) — enabled by VEIL_ANALYTICS_URL.
    if let (Some(buffer), Some(settings)) =
        (state.analytics.clone(), analytics::shipper::settings_from_env())
    {
        info!(ingest = %settings.ingest_url, "analytics log shipping enabled");
        tokio::spawn(analytics::shipper::run(buffer, settings));
    }

    proxy::serve(listener, state).await?;
    Ok(())
}

/// Two explicit modes, no crossover:
///
/// **Control plane mode** (`VEIL_CONTROL_PLANE_URL` + `VEIL_NODE_ID` +
/// `VEIL_NODE_TOKEN` all set): pull with retry/backoff; on failure fall back
/// to the last-known-good cache (`VEIL_CONFIG_CACHE`, opt-in) — and if there
/// is none, refuse to start. A protection node must never silently come up
/// with the local dev file instead of its real rule set.
///
/// **Local file mode** (control plane not configured): `VEIL_CONFIG_PATH`.
async fn load_startup_config() -> Result<Config, Box<dyn std::error::Error>> {
    let Some(settings) = sync::settings_from_env() else {
        let config_path =
            std::env::var("VEIL_CONFIG_PATH").unwrap_or_else(|_| "veil.json".to_owned());
        let config = Config::from_file(&config_path)?;
        info!(config = %config_path, zones = config.zones.len(), "config loaded from local file");
        return Ok(config);
    };

    info!(
        control_plane = %settings.control_plane_url,
        node_id = %settings.node_id,
        "pulling config from control plane"
    );

    match sync::fetch_with_retry(&settings).await {
        Ok((config, raw)) => {
            info!(zones = config.zones.len(), "config pulled from control plane");
            if let Some(path) = cache::path_from_env() {
                cache::store(&path, &raw);
            }
            Ok(config)
        }
        Err(err) => {
            warn!(error = %err, "control plane unreachable after retries");

            let Some(path) = cache::path_from_env() else {
                return Err(
                    "control plane unreachable and VEIL_CONFIG_CACHE is not set; \
                     refusing to start without a trusted config"
                        .into(),
                );
            };

            let config = cache::load(&path).map_err(|cache_err| {
                format!(
                    "control plane unreachable and config cache at '{}' is unusable ({cache_err}); \
                     refusing to start without a trusted config",
                    path.display()
                )
            })?;

            warn!(
                cache = %path.display(),
                zones = config.zones.len(),
                "starting from last-known-good config cache; config may be stale"
            );
            Ok(config)
        }
    }
}
