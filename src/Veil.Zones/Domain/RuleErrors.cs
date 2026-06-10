namespace Veil.Zones.Domain;

public static class RuleErrors {
    public static readonly Error NotFound =
        Error.NotFound("Rule.NotFound", "Kural bulunamadı.");

    public static Error ConditionTypeUnknown(string type) {
        return Error.Validation("Rule.ConditionTypeUnknown", $"Bilinmeyen koşul tipi: '{type}'.");
    }

    public static Error ConditionValueMissing(string type, string field) {
        return Error.Validation("Rule.ConditionValueMissing", $"'{type}' koşulu için '{field}' alanı gereklidir.");
    }

    public static readonly Error NameEmpty =
        Error.Validation("Rule.NameEmpty", "Kural adı boş olamaz.");

    public static readonly Error PriorityNegative =
        Error.Validation("Rule.PriorityNegative", "Öncelik değeri negatif olamaz.");

    public static readonly Error ConditionsEmpty =
        Error.Validation("Rule.ConditionsEmpty", "En az bir koşul gereklidir.");

    public static readonly Error RateLimitConfigMissing =
        Error.Validation("Rule.RateLimitConfigMissing", "RateLimit action'ı için RateLimitConfig gereklidir.");

    public static readonly Error RateLimitConfigNotAllowed =
        Error.Validation("Rule.RateLimitConfigNotAllowed", "RateLimitConfig yalnızca RateLimit action'ında kullanılabilir.");

    public static readonly Error RateLimitRequestsInvalid =
        Error.Validation("Rule.RateLimitRequestsInvalid", "Rate limit request sayısı en az 1 olmalıdır.");

    public static readonly Error RateLimitWindowInvalid =
        Error.Validation("Rule.RateLimitWindowInvalid", "Rate limit pencere süresi en az 1 saniye olmalıdır.");

    public static readonly Error RateLimitWindowTooLarge =
        Error.Validation("Rule.RateLimitWindowTooLarge", "Rate limit pencere süresi 24 saati aşamaz.");
}
