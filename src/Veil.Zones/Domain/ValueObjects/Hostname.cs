using System.Text.RegularExpressions;

namespace Veil.Zones.Domain.ValueObjects;

/// <summary>
/// Validated DNS hostname. Supports bare domains ("api.example.com")
/// and wildcard prefixes ("*.example.com").
/// </summary>
public readonly partial struct Hostname : IEquatable<Hostname> {
    public const int MaxLength = 253;
    public const int MaxLabelLength = 63;

    public string Value { get; }

    private Hostname(string value) {
        this.Value = value;
    }

    public bool IsWildcard => this.Value.StartsWith("*.");

    public static Result<Hostname> Create(string? value) {
        if(string.IsNullOrWhiteSpace(value))
            return ZoneErrors.HostnameEmpty;

        string hostname = value.Trim().ToLowerInvariant();

        if(hostname.Length > MaxLength)
            return ZoneErrors.HostnameTooLong(hostname.Length);

        // Strip wildcard prefix for label validation
        string labelsToValidate = hostname.StartsWith("*.")
            ? hostname[2..]
            : hostname;

        if(string.IsNullOrEmpty(labelsToValidate))
            return ZoneErrors.HostnameWildcardMissingLabel;

        string[] labels = labelsToValidate.Split('.');
        foreach(string label in labels) {
            if(label.Length is 0 or > MaxLabelLength)
                return ZoneErrors.HostnameLabelLength(label);

            if(label.StartsWith('-') || label.EndsWith('-'))
                return ZoneErrors.HostnameLabelHyphen(label);

            if(!LabelRegex().IsMatch(label))
                return ZoneErrors.HostnameLabelInvalidChars(label);
        }

        return new Hostname(hostname);
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    public bool Matches(string requestHost) {
        string host = requestHost.ToLowerInvariant();

        if(!this.IsWildcard)
            return this.Value == host;

        // *.example.com matches sub.example.com but not example.com
        string suffix = this.Value[1..]; // ".example.com"
        return host.Length > suffix.Length && host.EndsWith(suffix);
    }

    public bool Equals(Hostname other) {
        return this.Value == other.Value;
    }

    public override bool Equals(object? obj) {
        return obj is Hostname other && Equals(other);
    }

    public override int GetHashCode() {
        return this.Value.GetHashCode();
    }

    public override string ToString() {
        return this.Value;
    }

    public static bool operator ==(Hostname left, Hostname right) {
        return left.Equals(right);
    }

    public static bool operator !=(Hostname left, Hostname right) {
        return !left.Equals(right);
    }
}