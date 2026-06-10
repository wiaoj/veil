namespace Veil.Zones.Domain;

public static class ZoneErrors {
    public static readonly Error NotFound =
        Error.NotFound("Zone.NotFound", "Zone bulunamadı.");

    public static readonly Error RuleReorderMismatch =
        Error.Validation("Zone.RuleReorderMismatch", "Sıralama listesi zone'daki kurallarla birebir eşleşmelidir.");

    // ── Hostname ─────────────────────────────────────────────────────

    public static readonly Error HostnameEmpty =
        Error.Validation("Zone.HostnameEmpty", "Hostname boş olamaz.");

    public static Error HostnameTooLong(int length) {
        return Error.Validation("Zone.HostnameTooLong", $"Hostname {length} karakter — maksimum 253.");
    }

    public static readonly Error HostnameWildcardMissingLabel =
        Error.Validation("Zone.HostnameWildcardMissingLabel", "Wildcard hostname en az bir domain label içermelidir.");

    public static Error HostnameLabelLength(string label) {
        return Error.Validation("Zone.HostnameLabelLength", $"Label '{label}' geçersiz uzunlukta (1-63 karakter).");
    }

    public static Error HostnameLabelHyphen(string label) {
        return Error.Validation("Zone.HostnameLabelHyphen", $"Label '{label}' tire ile başlayamaz veya bitemez.");
    }

    public static Error HostnameLabelInvalidChars(string label) {
        return Error.Validation("Zone.HostnameLabelInvalidChars", $"Label '{label}' geçersiz karakter içeriyor.");
    }

    // ── Upstream ─────────────────────────────────────────────────────

    public static readonly Error UpstreamEmpty =
        Error.Validation("Zone.UpstreamEmpty", "En az bir upstream target gereklidir.");

    public static Error UpstreamInvalidUrl(string url) {
        return Error.Validation("Zone.UpstreamInvalidUrl", $"Upstream target '{url}' geçerli bir mutlak URL değil.");
    }

    public static Error UpstreamInvalidScheme(string url) {
        return Error.Validation("Zone.UpstreamInvalidScheme", $"Upstream target '{url}' geçersiz scheme — yalnızca http/https desteklenir.");
    }

    public static readonly Error UpstreamInvalidWeight =
        Error.Validation("Zone.UpstreamInvalidWeight", "Upstream target weight en az 1 olmalıdır.");

    // ── Challenge ────────────────────────────────────────────────────

    public static Error PowDifficultyOutOfRange(int min, int max) {
        return Error.Validation("Zone.PowDifficultyOutOfRange", $"PoW difficulty {min}-{max} aralığında olmalıdır.");
    }

    public static Error TokenTtlOutOfRange(double minMinutes, double maxMinutes) {
        return Error.Validation("Zone.TokenTtlOutOfRange", $"Token TTL {minMinutes}-{maxMinutes} dakika aralığında olmalıdır.");
    }
}
