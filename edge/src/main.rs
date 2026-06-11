use std::sync::Arc;

use tokio::net::TcpListener;
use tracing::{info, warn};
use tracing_subscriber::EnvFilter;

use veil_edge::config::{sync, Config};
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

    proxy::serve(listener, Arc::new(AppState::new(config))).await?;
    Ok(())
}

/// Control plane first (when `VEIL_CONTROL_PLANE_URL`, `VEIL_NODE_ID` and
/// `VEIL_NODE_TOKEN` are all set), local config file as fallback.
async fn load_startup_config() -> Result<Config, Box<dyn std::error::Error>> {
    if let Some(settings) = sync::settings_from_env() {
        info!(
            control_plane = %settings.control_plane_url,
            node_id = %settings.node_id,
            "pulling config from control plane"
        );
        match sync::fetch_initial(&settings).await {
            Ok(config) => {
                info!(zones = config.zones.len(), "config pulled from control plane");
                return Ok(config);
            }
            Err(err) => {
                warn!(error = %err, "control plane pull failed; falling back to local config file");
            }
        }
    }

    let config_path =
        std::env::var("VEIL_CONFIG_PATH").unwrap_or_else(|_| "veil.json".to_owned());
    let config = Config::from_file(&config_path)?;
    info!(config = %config_path, zones = config.zones.len(), "config loaded from local file");
    Ok(config)
}
