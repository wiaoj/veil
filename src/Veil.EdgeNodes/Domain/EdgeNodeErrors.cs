namespace Veil.EdgeNodes.Domain;

public static class EdgeNodeErrors {
    public static readonly Error NotFound =
        Error.NotFound("EdgeNode.NotFound", "Edge node bulunamadı.");

    public static readonly Error NameEmpty =
        Error.Validation("EdgeNode.NameEmpty", "Edge node adı boş olamaz.");

    public static Error AddressInvalid(string address) {
        return Error.Validation("EdgeNode.AddressInvalid", $"Adres '{address}' geçerli bir mutlak http/https URL değil.");
    }

    public static readonly Error TokenHashEmpty =
        Error.Validation("EdgeNode.TokenHashEmpty", "Node token hash boş olamaz.");
}
