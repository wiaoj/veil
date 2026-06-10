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
    /// Challenge devre dışı preset.
    /// </summary>
    public static ChallengeConfig Disabled => new(
        false, 
        PowDifficulty.Create(20).Value, 
        TokenTtl.Create(TimeSpan.FromMinutes(10)).Value, 
        false);

    private ChallengeConfig(
        bool enabled,
        PowDifficulty powDifficulty,
        TokenTtl tokenTtl,
        bool requireCaptchaOnHighRisk) {
        this.Enabled = enabled;
        this.PowDifficulty = powDifficulty;
        this.TokenTtl = tokenTtl;
        this.RequireCaptchaOnHighRisk = requireCaptchaOnHighRisk;
    }

    public static ChallengeConfig CreateDefault() {
        return new ChallengeConfig(
            true,
            PowDifficulty.Create(20).Value,
            TokenTtl.Create(TimeSpan.FromMinutes(10)).Value,
            false);
    }

    public static ChallengeConfig Create(
        PowDifficulty powDifficulty,
        TokenTtl tokenTtl,
        bool requireCaptchaOnHighRisk) {
        
        return new ChallengeConfig(
            true,
            powDifficulty,
            tokenTtl,
            requireCaptchaOnHighRisk);
    }
}