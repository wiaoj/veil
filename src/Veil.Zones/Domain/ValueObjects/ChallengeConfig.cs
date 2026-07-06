namespace Veil.Zones.Domain.ValueObjects;

/// <summary>
/// Zone-specific challenge ayarları. Edge node, bu konfigürasyonu
/// kullanarak PoW difficulty ve token TTL'ini belirler.
/// </summary>
public sealed class ChallengeConfig {
    public bool Enabled { get; }
    public PowDifficulty PowDifficulty { get; }
    public TokenTtl TokenTtl { get; }
    public bool RequireCaptchaOnHighRisk { get; }
    /// <summary>
    /// Risk skoru (0..100) bu eşiğin üstündeyse Tier-1 PoW yerine Tier-2
    /// etkileşim challenge'ı sunulur. Edge'e push edilen tek per-zone knob.
    /// </summary>
    public int RiskThreshold { get; }

    private const int DefaultRiskThreshold = 70;

    /// <summary>
    /// Challenge devre dışı preset.
    /// </summary>
    public static ChallengeConfig Disabled => new(
        false,
        PowDifficulty.Create(20).Value,
        TokenTtl.Create(TimeSpan.FromMinutes(10)).Value,
        false,
        DefaultRiskThreshold);

    private ChallengeConfig(
        bool enabled,
        PowDifficulty powDifficulty,
        TokenTtl tokenTtl,
        bool requireCaptchaOnHighRisk,
        int riskThreshold) {
        this.Enabled = enabled;
        this.PowDifficulty = powDifficulty;
        this.TokenTtl = tokenTtl;
        this.RequireCaptchaOnHighRisk = requireCaptchaOnHighRisk;
        this.RiskThreshold = Math.Clamp(riskThreshold, 0, 100);
    }

    /// <summary>
    /// Persistence-only factory: trusts previously validated data coming back
    /// from the database.
    /// </summary>
    internal static ChallengeConfig Restore(
        bool enabled,
        int powDifficulty,
        TimeSpan tokenTtl,
        bool requireCaptchaOnHighRisk,
        int riskThreshold) {
        return new ChallengeConfig(
            enabled,
            PowDifficulty.Create(powDifficulty).Value,
            TokenTtl.Create(tokenTtl).Value,
            requireCaptchaOnHighRisk,
            riskThreshold);
    }

    public static ChallengeConfig CreateDefault() {
        return new ChallengeConfig(
            true,
            PowDifficulty.Create(20).Value,
            TokenTtl.Create(TimeSpan.FromMinutes(10)).Value,
            false,
            DefaultRiskThreshold);
    }

    public static ChallengeConfig Create(
        PowDifficulty powDifficulty,
        TokenTtl tokenTtl,
        bool requireCaptchaOnHighRisk,
        int riskThreshold = DefaultRiskThreshold) {

        return new ChallengeConfig(
            true,
            powDifficulty,
            tokenTtl,
            requireCaptchaOnHighRisk,
            riskThreshold);
    }
}
