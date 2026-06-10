namespace Veil.Zones.Domain.ValueObjects;

public readonly record struct TokenTtl {
    public static readonly Range<TimeSpan> AllowedRange = new(TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));

    public TimeSpan Value { get; }

    private TokenTtl(TimeSpan value) {
        this.Value = value;
    }

    public static Result<TokenTtl> Create(TimeSpan value) {
        if(!AllowedRange.Contains(value)) {
            return ZoneErrors.TokenTtlOutOfRange(AllowedRange.Min.TotalMinutes, AllowedRange.Max.TotalMinutes);
        }

        return new TokenTtl(value);
    }
}