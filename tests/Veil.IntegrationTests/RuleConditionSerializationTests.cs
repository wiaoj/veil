using System.Text.Json;
using Veil.Zones.Domain.Enums;
using Veil.Zones.Domain.ValueObjects;
using Xunit;

namespace Veil.IntegrationTests;

/// <summary>
/// The Zones module stores rule conditions as a polymorphic jsonb list, so every
/// <see cref="RuleCondition"/> subtype must be registered as a
/// <c>[JsonDerivedType]</c> or persistence throws. This guards the full
/// vocabulary — including the header/user_agent/path_regex/ja3/ja4 types that
/// were previously unregistered — with a round-trip through System.Text.Json.
/// </summary>
public sealed class RuleConditionSerializationTests {
    [Fact]
    public void Every_condition_type_round_trips_through_polymorphic_json() {
        List<RuleCondition> conditions = [
            new IpMatchCondition("203.0.113.7"),
            new IpRangeMatchCondition("203.0.113.0/24"),
            new CountryMatchCondition("TR"),
            new AsnMatchCondition(64500),
            new PathMatchCondition("/admin", PathMatchMode.Exact),
            new PathRegexMatchCondition("^/api/.*$"),
            new HeaderMatchCondition("X-Api-Key", "secret"),
            new UserAgentMatchCondition("curl"),
            new MethodMatchCondition("POST"),
            new QueryRegexMatchCondition("(?i)union.*select"),
            new HeaderRegexMatchCondition("Referer", "evil\\.com"),
            new BodyRegexMatchCondition("<script"),
            new BodyJsonMatchCondition("$.comment", "(?i)<script"),
            new BodySchemaMatchCondition("create-user", "1.0.0"),
            new Ja3MatchCondition("e7d705a3286e19ea42f587b344ee6865"),
            new Ja4MatchCondition("t13d1516h2_8daaf6152771_b186095e22b6"),
        ];

        string json = JsonSerializer.Serialize(conditions);
        List<RuleCondition>? restored = JsonSerializer.Deserialize<List<RuleCondition>>(json);

        Assert.NotNull(restored);
        Assert.Equal(conditions.Count, restored.Count);
        // Types survive the round-trip (discriminators resolve back to subtypes).
        Assert.IsType<MethodMatchCondition>(restored[8]);
        Assert.IsType<HeaderRegexMatchCondition>(restored[10]);
        Assert.IsType<BodyJsonMatchCondition>(restored[12]);
        Assert.Equal("$.comment", ((BodyJsonMatchCondition)restored[12]).Path);
        Assert.IsType<BodySchemaMatchCondition>(restored[13]);
        Assert.Equal("create-user", ((BodySchemaMatchCondition)restored[13]).Subject);
        Assert.IsType<Ja3MatchCondition>(restored[14]);
        Assert.IsType<Ja4MatchCondition>(restored[15]);
        Assert.Equal("t13d1516h2_8daaf6152771_b186095e22b6", ((Ja4MatchCondition)restored[15]).Fingerprint);
        Assert.Equal("secret", ((HeaderMatchCondition)restored[6]).Value);
    }
}
