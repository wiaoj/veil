namespace Veil.Certificates.Domain;

public static class CertificateErrors {
    public static readonly Error NotFound =
        Error.NotFound("Certificate.NotFound", "Sertifika bulunamadı.");

    public static Error HostnameInvalid(string hostname) {
        return Error.Validation("Certificate.HostnameInvalid", $"'{hostname}' geçerli bir hostname değil.");
    }

    public static Error AlreadyRequested(string hostname) {
        return Error.Conflict("Certificate.AlreadyRequested",
            $"'{hostname}' için bekleyen veya aktif bir sertifika zaten var.");
    }

    public static readonly Error NotPending =
        Error.Conflict("Certificate.NotPending", "Sertifika bekleyen durumda değil.");

    public static readonly Error NotActive =
        Error.Conflict("Certificate.NotActive", "Sertifika aktif durumda değil.");

    public static readonly Error MaterialEmpty =
        Error.Validation("Certificate.MaterialEmpty", "Sertifika zinciri ve şifrelenmiş özel anahtar boş olamaz.");
}
