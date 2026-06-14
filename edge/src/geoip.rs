//! GeoIP / ASN enrichment via MaxMind MMDB databases.
//!
//! Optional: enabled by `VEIL_GEOIP_PATH` (a GeoLite2/GeoIP2 *Country* or
//! *City* database) and/or `VEIL_GEOIP_ASN_PATH` (an ASN database). When
//! neither is set — or a file fails to open — geo enrichment is simply absent
//! and the country/ASN fields on the request context stay `None`. Lookups are
//! in-memory and allocation-light; the reader holds the mmap'd database.

use std::net::IpAddr;

use maxminddb::{geoip2, Reader};
use tracing::{info, warn};

/// Resolved geo attributes for a client IP.
#[derive(Debug, Default, Clone)]
pub struct GeoInfo {
    /// ISO 3166-1 alpha-2 country code (uppercase), e.g. `"TR"`.
    pub country: Option<String>,
    /// Autonomous system number.
    pub asn: Option<u32>,
}

pub struct GeoDb {
    country: Option<Reader<Vec<u8>>>,
    asn: Option<Reader<Vec<u8>>>,
}

impl GeoDb {
    /// Opens the databases named by `VEIL_GEOIP_PATH` / `VEIL_GEOIP_ASN_PATH`.
    /// Returns `None` when neither is configured or both fail to open.
    pub fn from_env() -> Option<Self> {
        let country = std::env::var("VEIL_GEOIP_PATH").ok().and_then(|p| open(&p, "country/city"));
        let asn = std::env::var("VEIL_GEOIP_ASN_PATH").ok().and_then(|p| open(&p, "ASN"));
        if country.is_none() && asn.is_none() {
            return None;
        }
        Some(Self { country, asn })
    }

    /// Looks up the country code and ASN for `ip`. Missing entries (and an
    /// absent database) yield `None` fields rather than an error.
    pub fn lookup(&self, ip: IpAddr) -> GeoInfo {
        let country = self.country.as_ref().and_then(|r| {
            r.lookup::<geoip2::Country>(ip)
                .ok()
                .and_then(|c| c.country)
                .and_then(|c| c.iso_code)
                .map(|code| code.to_ascii_uppercase())
        });
        let asn = self
            .asn
            .as_ref()
            .and_then(|r| r.lookup::<geoip2::Asn>(ip).ok())
            .and_then(|a| a.autonomous_system_number);
        GeoInfo { country, asn }
    }
}

fn open(path: &str, kind: &str) -> Option<Reader<Vec<u8>>> {
    match Reader::open_readfile(path) {
        Ok(reader) => {
            info!(path, kind, "loaded GeoIP database");
            Some(reader)
        }
        Err(err) => {
            warn!(path, kind, error = %err, "failed to open GeoIP database; enrichment disabled for it");
            None
        }
    }
}
