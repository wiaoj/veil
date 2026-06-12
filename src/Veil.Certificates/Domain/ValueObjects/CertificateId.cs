using Veil.Shared;

namespace Veil.Certificates.Domain.ValueObjects;

public readonly struct CertificateId : IPrefixedId<CertificateId> {
    public static string Prefix => "crt";

    public SnowflakeId Value { get; }

    private CertificateId(SnowflakeId value) {
        this.Value = value;
    }

    public static CertificateId From(SnowflakeId value) {
        return new(value);
    }

    public static CertificateId New() {
        return new(SnowflakeId.NewId());
    }
}
