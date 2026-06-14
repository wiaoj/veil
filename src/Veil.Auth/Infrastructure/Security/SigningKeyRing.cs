using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Veil.Auth.Infrastructure.Security;

/// <summary>
/// The set of HMAC signing keys for access tokens. New tokens are signed with
/// the active key (its id stamped into the JWT <c>kid</c> header); every key
/// in the ring is accepted for validation, which is what makes key rotation
/// zero-downtime — old tokens keep verifying against the old key until they
/// expire.
///
/// Built from <see cref="AuthOptions.SigningKeys"/>; if that is empty it falls
/// back to the legacy single <see cref="AuthOptions.SigningKey"/> under the id
/// <c>default</c>. With no key material at all the ring is empty and the auth
/// module stays unregistered (open mode).
/// </summary>
public sealed class SigningKeyRing {
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }
    public SigningCredentials? ActiveCredentials { get; }
    public bool HasKeys => this.ActiveCredentials is not null;

    public SigningKeyRing(AuthOptions options) {
        List<(string Kid, byte[] Bytes)> entries = [];
        foreach(SigningKeyEntry entry in options.SigningKeys) {
            if(!string.IsNullOrEmpty(entry.Key))
                entries.Add((entry.Kid, Encoding.UTF8.GetBytes(entry.Key)));
        }
        if(entries.Count == 0 && !string.IsNullOrEmpty(options.SigningKey))
            entries.Add(("default", Encoding.UTF8.GetBytes(options.SigningKey)));

        this.ValidationKeys = entries
            .Select(e => (SecurityKey)new SymmetricSecurityKey(e.Bytes) { KeyId = e.Kid })
            .ToList();

        if(entries.Count == 0)
            return;

        (string Kid, byte[] Bytes) active = entries.Find(e => e.Kid == options.ActiveSigningKeyId);
        if(active.Bytes is null)
            active = entries[0];

        SymmetricSecurityKey signingKey = new(active.Bytes) { KeyId = active.Kid };
        this.ActiveCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
    }
}
