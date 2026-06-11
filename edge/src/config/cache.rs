//! Last-known-good config snapshot on local disk.
//!
//! Opt-in via `VEIL_CONFIG_CACHE` — the operator who mounts a persistent
//! volume also sets the path, so there is no illusion of durability on
//! ephemeral filesystems. This is not a cache in the lookup sense: it is a
//! crash-recovery snapshot, written on every successful pull/push and read
//! exactly once at startup, only when the control plane is unreachable.
//!
//! The node never serves shared state from here; rate limiting and challenge
//! nonces go to Redis precisely because they are shared and mutable. This
//! file is strictly node-local survival data.

use std::path::{Path, PathBuf};

use tracing::warn;

use super::{Config, ConfigError};

pub fn path_from_env() -> Option<PathBuf> {
    std::env::var("VEIL_CONFIG_CACHE").ok().map(PathBuf::from)
}

/// Best-effort write (temp file + atomic rename). A failed cache write is
/// never fatal — the in-memory config is already active.
pub fn store(path: &Path, raw_json: &str) {
    let tmp = path.with_extension("tmp");
    let result = std::fs::write(&tmp, raw_json).and_then(|()| std::fs::rename(&tmp, path));
    if let Err(err) = result {
        warn!(path = %path.display(), error = %err, "failed to write config cache");
    }
}

pub fn load(path: &Path) -> Result<Config, ConfigError> {
    let raw = std::fs::read_to_string(path).map_err(ConfigError::Io)?;
    Config::from_json(&raw)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_path(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!("veil-cache-test-{name}-{}.json", std::process::id()))
    }

    #[test]
    fn store_then_load_roundtrips() {
        let path = temp_path("roundtrip");
        let raw = r#"{"zones": [{"name": "cached", "hosts": ["*"], "upstream": "http://127.0.0.1:1", "rules": []}]}"#;

        store(&path, raw);
        let config = load(&path).unwrap();
        assert_eq!(config.zones[0].name, "cached");

        std::fs::remove_file(&path).ok();
    }

    #[test]
    fn store_overwrites_previous_snapshot() {
        let path = temp_path("overwrite");
        store(&path, r#"{"zones": [{"name": "old", "hosts": ["*"], "upstream": "http://h:1", "rules": []}]}"#);
        store(&path, r#"{"zones": [{"name": "new", "hosts": ["*"], "upstream": "http://h:1", "rules": []}]}"#);

        assert_eq!(load(&path).unwrap().zones[0].name, "new");

        std::fs::remove_file(&path).ok();
    }

    #[test]
    fn load_missing_file_errors() {
        assert!(load(Path::new("definitely-missing-veil-cache.json")).is_err());
    }
}
