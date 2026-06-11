namespace Veil.EdgeNodes.Domain.Enums;

public enum EdgeNodeStatus {
    /// <summary>Registered but has not pulled config / reported in yet.</summary>
    Registered,
    /// <summary>Recently seen — receiving config pushes.</summary>
    Active,
    /// <summary>Administratively disabled — excluded from config pushes.</summary>
    Disabled
}
