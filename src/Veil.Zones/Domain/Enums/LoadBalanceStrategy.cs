namespace Veil.Zones.Domain.Enums;

public enum LoadBalanceStrategy {
    RoundRobin,
    LeastConnections,
    IpHash
}