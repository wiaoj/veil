namespace Veil.Auth.Domain.Enums;

public enum UserRole {
    /// <summary>Full control-plane access, including user and key management.</summary>
    Admin,
    /// <summary>Day-to-day zone and rule management.</summary>
    Member,
}
