//! Visitor-facing localisation for edge-served pages (challenge, error).
//!
//! Language is resolved per request: a `?locale=` / `?l=` query override wins,
//! otherwise the `Accept-Language` header is consulted, otherwise English.
//! Region and case subtags are ignored, so `tr-TR`, `tr_tr` and `tr` all map
//! to Turkish.

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Lang {
    En,
    Tr,
}

impl Lang {
    /// The `lang` attribute / short code for this language.
    pub fn code(self) -> &'static str {
        match self {
            Lang::En => "en",
            Lang::Tr => "tr",
        }
    }
}

/// Maps a single BCP-47 tag to a supported language by its primary subtag.
fn from_tag(tag: &str) -> Option<Lang> {
    let lowered = tag.trim().to_ascii_lowercase();
    match lowered.split(['-', '_']).next().unwrap_or("") {
        "tr" => Some(Lang::Tr),
        "en" => Some(Lang::En),
        _ => None,
    }
}

/// Resolves the request language. Precedence: `locale`/`l` query parameter →
/// first matching `Accept-Language` tag → English default.
pub fn resolve(query: Option<&str>, accept_language: Option<&str>) -> Lang {
    if let Some(q) = query {
        for pair in q.split('&') {
            let (key, value) = pair.split_once('=').unwrap_or((pair, ""));
            if (key == "locale" || key == "l") && !value.is_empty() {
                if let Some(lang) = from_tag(value) {
                    return lang;
                }
            }
        }
    }

    if let Some(header) = accept_language {
        for part in header.split(',') {
            let tag = part.split(';').next().unwrap_or("");
            if let Some(lang) = from_tag(tag) {
                return lang;
            }
        }
    }

    Lang::En
}

/// Localised strings rendered into `templates/challenge.html`.
pub struct ChallengeStrings {
    pub doc_title: &'static str,
    pub heading: &'static str,
    pub intro: &'static str,
    pub noscript: &'static str,
    pub status_verifying: &'static str,
    pub hint: &'static str,
    pub footer: &'static str,
    pub status_redirecting: &'static str,
    pub status_almost: &'static str,
    pub status_error: &'static str,
    pub status_failed_retry: &'static str,
    pub status_conn_retry: &'static str,
}

pub fn challenge_strings(lang: Lang) -> ChallengeStrings {
    match lang {
        Lang::Tr => ChallengeStrings {
            doc_title: "Bağlantı Doğrulanıyor",
            heading: "Bağlantınız doğrulanıyor",
            intro: "Veil, istenen sayfaya erişiminizden önce tarayıcınızı doğruluyor. Bu işlem otomatik olarak gerçekleşir ve yalnızca birkaç saniye sürer.",
            noscript: "Devam etmek için JavaScript gereklidir. Lütfen tarayıcı ayarlarınızdan JavaScript'i etkinleştirin.",
            status_verifying: "Doğrulanıyor…",
            hint: "Doğrulamayı tamamlamak için imlecinizi hareket ettirin veya ekrana dokunun.",
            footer: "Veil Edge tarafından korunmaktadır",
            status_redirecting: "Doğrulandı, yönlendiriliyorsunuz…",
            status_almost: "Neredeyse tamam…",
            status_error: "Bir hata oluştu. Sayfayı yenileyin.",
            status_failed_retry: "Doğrulama başarısız. Tekrar deneniyor…",
            status_conn_retry: "Bağlantı hatası. Tekrar deneniyor…",
        },
        Lang::En => ChallengeStrings {
            doc_title: "Verifying Your Connection",
            heading: "Verifying your connection",
            intro: "Veil is checking your browser before you reach the requested page. This is automatic and only takes a few seconds.",
            noscript: "JavaScript is required to continue. Please enable JavaScript in your browser settings.",
            status_verifying: "Verifying…",
            hint: "Move your cursor or tap the screen to complete verification.",
            footer: "Protected by Veil Edge",
            status_redirecting: "Verified, redirecting you…",
            status_almost: "Almost there…",
            status_error: "Something went wrong. Please refresh the page.",
            status_failed_retry: "Verification failed. Retrying…",
            status_conn_retry: "Connection error. Retrying…",
        },
    }
}

/// Localised strings for `templates/error.html` (block / rate-limit pages).
pub struct ErrorStrings {
    pub forbidden_title: &'static str,
    pub forbidden_detail: &'static str,
    pub rate_limited_title: &'static str,
    /// `{}` is replaced with the retry-after seconds.
    pub rate_limited_detail_fmt: &'static str,
}

pub fn error_strings(lang: Lang) -> ErrorStrings {
    match lang {
        Lang::Tr => ErrorStrings {
            forbidden_title: "Erişim Reddedildi",
            forbidden_detail: "Bu kaynağa erişim izniniz yok.",
            rate_limited_title: "Çok Fazla İstek",
            rate_limited_detail_fmt: "İstek limitini aştınız. Lütfen {} saniye sonra tekrar deneyin.",
        },
        Lang::En => ErrorStrings {
            forbidden_title: "Access Denied",
            forbidden_detail: "You do not have permission to access this resource.",
            rate_limited_title: "Too Many Requests",
            rate_limited_detail_fmt: "You have exceeded your rate limit. Please try again in {} seconds.",
        },
    }
}
