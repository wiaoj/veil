//! ACME HTTP-01 challenge support. The control plane publishes the active
//! challenge set (`POST /_veil/internal/acme-challenge`, same credentials as
//! config push); the node answers Let's Encrypt validation requests on
//! `GET /.well-known/acme-challenge/{token}` before any zone or rule logic —
//! a blocked or challenged client must never break certificate issuance.

use std::collections::HashMap;
use std::sync::RwLock;

use serde::Deserialize;

/// Path prefix served for HTTP-01 validation.
pub const HTTP01_PATH_PREFIX: &str = "/.well-known/acme-challenge/";

/// Reserved path where the control plane publishes the challenge set.
pub const ACME_PUSH_PATH: &str = "/_veil/internal/acme-challenge";

#[derive(Deserialize)]
pub struct ChallengeEntry {
    pub token: String,
    #[serde(rename = "keyAuthorization")]
    pub key_authorization: String,
}

#[derive(Deserialize)]
pub struct ChallengeSet {
    pub challenges: Vec<ChallengeEntry>,
}

/// In-memory token → key-authorization map. The push replaces the whole set,
/// so completed orders disappear on the next (possibly empty) publish.
#[derive(Default)]
pub struct AcmeStore {
    entries: RwLock<HashMap<String, String>>,
}

impl AcmeStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Replaces the active challenge set. Returns the new entry count.
    pub fn replace(&self, set: ChallengeSet) -> usize {
        let map: HashMap<String, String> = set
            .challenges
            .into_iter()
            .map(|c| (c.token, c.key_authorization))
            .collect();
        let count = map.len();
        *self.entries.write().expect("acme store lock poisoned") = map;
        count
    }

    /// Key authorization for a token, if the challenge is active.
    pub fn key_authorization(&self, token: &str) -> Option<String> {
        self.entries
            .read()
            .expect("acme store lock poisoned")
            .get(token)
            .cloned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn set(pairs: &[(&str, &str)]) -> ChallengeSet {
        ChallengeSet {
            challenges: pairs
                .iter()
                .map(|(t, k)| ChallengeEntry {
                    token: (*t).to_owned(),
                    key_authorization: (*k).to_owned(),
                })
                .collect(),
        }
    }

    #[test]
    fn replace_swaps_the_whole_set() {
        let store = AcmeStore::new();
        assert_eq!(store.replace(set(&[("a", "a.key"), ("b", "b.key")])), 2);
        assert_eq!(store.key_authorization("a").as_deref(), Some("a.key"));

        assert_eq!(store.replace(set(&[("c", "c.key")])), 1);
        assert_eq!(store.key_authorization("a"), None);
        assert_eq!(store.key_authorization("c").as_deref(), Some("c.key"));
    }

    #[test]
    fn empty_set_clears() {
        let store = AcmeStore::new();
        store.replace(set(&[("a", "a.key")]));
        store.replace(set(&[]));
        assert_eq!(store.key_authorization("a"), None);
    }

    #[test]
    fn parses_control_plane_payload() {
        let parsed: ChallengeSet = serde_json::from_str(
            r#"{"challenges":[{"token":"tok","keyAuthorization":"tok.thumb"}]}"#,
        )
        .unwrap();
        assert_eq!(parsed.challenges.len(), 1);
        assert_eq!(parsed.challenges[0].key_authorization, "tok.thumb");
    }
}
