using Veil.Shared;
using Wiaoj.Primitives;

namespace Veil.Zones.Domain.ValueObjects;

public readonly record struct PowDifficulty
{
    public static readonly Range<int> AllowedRange = new(8, 32);

    public int Value { get; }

    private PowDifficulty(int value)
    {
        this.Value = value;
    }

    public static Result<PowDifficulty> Create(int value)
    {
        if (!AllowedRange.Contains(value))
        {
            return ZoneErrors.PowDifficultyOutOfRange(AllowedRange.Min, AllowedRange.Max);
        }

        return new PowDifficulty(value);
    }
}
