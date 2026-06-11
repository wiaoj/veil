namespace Veil.Auth.Domain;

public static class AuthErrors {
    public static Error EmailInvalid(string email) {
        return Error.Validation("Auth.EmailInvalid", $"'{email}' geçerli bir e-posta adresi değil.");
    }

    public static readonly Error DisplayNameEmpty =
        Error.Validation("Auth.DisplayNameEmpty", "Görünen ad boş olamaz.");

    public static readonly Error PasswordHashEmpty =
        Error.Validation("Auth.PasswordHashEmpty", "Parola hash'i boş olamaz.");

    public static readonly Error ApiKeyNameEmpty =
        Error.Validation("Auth.ApiKeyNameEmpty", "API anahtarı adı boş olamaz.");

    public static readonly Error KeyHashEmpty =
        Error.Validation("Auth.KeyHashEmpty", "API anahtarı hash'i boş olamaz.");

    public static readonly Error ApiKeyAlreadyRevoked =
        Error.Validation("Auth.ApiKeyAlreadyRevoked", "API anahtarı zaten iptal edilmiş.");

    public static readonly Error ApiKeyNotFound =
        Error.NotFound("Auth.ApiKeyNotFound", "API anahtarı bulunamadı.");
}
