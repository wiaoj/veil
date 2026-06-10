use std::sync::Arc;

use tokio::net::TcpListener;
use tracing::info;
use tracing_subscriber::EnvFilter;

use veil_edge::config::Config;
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

    let config_path =
        std::env::var("VEIL_CONFIG_PATH").unwrap_or_else(|_| "veil.json".to_owned());
    let config = Config::from_file(&config_path)?;

    let listen_addr =
        std::env::var("VEIL_LISTEN_HTTP").unwrap_or_else(|_| "127.0.0.1:8080".to_owned());
    let listener = TcpListener::bind(&listen_addr).await?;

    info!(
        addr = %listen_addr,
        config = %config_path,
        zones = config.zones.len(),
        "veil-edge listening"
    );

    proxy::serve(listener, Arc::new(AppState::new(config))).await?;
    Ok(())
}
