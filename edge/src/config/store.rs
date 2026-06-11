//! Shared, atomically swappable config snapshot.
//!
//! Readers grab an `Arc` clone and keep using that snapshot for the whole
//! request — a concurrent swap never blocks or changes a request mid-flight.
//! The write lock is held only for the pointer swap itself.

use std::sync::{Arc, RwLock};

use super::Config;

pub struct ConfigStore {
    inner: RwLock<Arc<Config>>,
}

impl ConfigStore {
    pub fn new(config: Config) -> Self {
        Self { inner: RwLock::new(Arc::new(config)) }
    }

    /// The current snapshot. Cheap (one atomic refcount bump).
    pub fn load(&self) -> Arc<Config> {
        self.inner.read().expect("config store poisoned").clone()
    }

    /// Atomically replaces the snapshot. In-flight requests keep the old one.
    pub fn swap(&self, config: Config) {
        *self.inner.write().expect("config store poisoned") = Arc::new(config);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::Config;

    fn config(name: &str) -> Config {
        Config::from_json(&format!(
            r#"{{"zones": [{{"name": "{name}", "hosts": ["*"], "upstream": "http://127.0.0.1:1", "rules": []}}]}}"#
        ))
        .unwrap()
    }

    #[test]
    fn load_returns_current_snapshot() {
        let store = ConfigStore::new(config("a"));
        assert_eq!(store.load().zones[0].name, "a");
    }

    #[test]
    fn swap_replaces_snapshot_but_old_arcs_stay_valid() {
        let store = ConfigStore::new(config("a"));
        let old = store.load();
        store.swap(config("b"));
        assert_eq!(old.zones[0].name, "a", "held snapshot must stay intact");
        assert_eq!(store.load().zones[0].name, "b");
    }
}
