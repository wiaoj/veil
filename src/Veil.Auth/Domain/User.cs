using Veil.Auth.Domain.Enums;
using Veil.Auth.Domain.Events;
using Veil.Auth.Domain.ValueObjects;
using Wiaoj.Primitives.Cryptography.Hashing;
using Wiaoj.Security;

namespace Veil.Auth.Domain;

/// <summary>
/// A control-plane user. Passwords never reach the aggregate as plaintext —
/// hashing happens at the edge of the module (see Pbkdf2PasswordHasher) and
/// only the encoded hash is stored.
/// </summary>
public sealed class User : Aggregate<UserId> {
    public EncryptedSecret<EmailSecretContext> EncryptedEmail { get; private set; } 
    public int EmailKeyVersion { get; private set; }
    public HexString EmailHash { get; private set; }

    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public UserRole Role { get; private set; }
    public bool IsDisabled { get; private set; }

    /// <summary>Consecutive failed login attempts since the last success.</summary>
    public int FailedLoginAttempts { get; private set; }

    /// <summary>When set and in the future, login is rejected (lockout).</summary>
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    private User() { }

    public static Result<User> Create(string email,
                                      EncryptedSecret<EmailSecretContext> encryptedEmail,
                                      HexString emailHash,
                                      string displayName,
                                      string passwordHash,
                                      UserRole role) {

        string normalizedEmail = email?.Trim().ToLowerInvariant() ?? "";
        if(normalizedEmail.Length is 0 || !normalizedEmail.Contains('@'))
            return AuthErrors.EmailInvalid(email ?? "");

        if(string.IsNullOrWhiteSpace(displayName))
            return AuthErrors.DisplayNameEmpty;

        if(string.IsNullOrWhiteSpace(passwordHash))
            return AuthErrors.PasswordHashEmpty;

        User user = new() {
            Id = UserId.New(),
            EncryptedEmail = encryptedEmail,
            EmailKeyVersion = encryptedEmail.KeyVersion.Value,
            EmailHash = emailHash,
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
            Role = role,
            IsDisabled = false
        };

        user.RaiseDomainEvent(new UserCreatedDomainEvent(user.Id));

        return user;
    }

    public Result<Success> ChangePassword(string newPasswordHash) {
        if(string.IsNullOrWhiteSpace(newPasswordHash))
            return AuthErrors.PasswordHashEmpty;

        this.PasswordHash = newPasswordHash;
        return Result.Success();
    }

    public Result<Success> Disable() {
        this.IsDisabled = true;
        return Result.Success();
    }

    public Result<Success> Enable() {
        this.IsDisabled = false;
        return Result.Success();
    }

    /// <summary>Whether the account is currently locked out at <paramref name="now"/>.</summary>
    public bool IsLockedOut(DateTimeOffset now) {
        return this.LockedUntilUtc is { } until && until > now;
    }

    /// <summary>
    /// Records a failed login. After <paramref name="maxAttempts"/> consecutive
    /// failures the account is locked for <paramref name="lockoutDuration"/>
    /// and the counter resets, so the lockout re-arms cleanly on the next miss.
    /// </summary>
    public void RegisterFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration) {
        this.FailedLoginAttempts++;
        if(this.FailedLoginAttempts >= maxAttempts) {
            this.LockedUntilUtc = now + lockoutDuration;
            this.FailedLoginAttempts = 0;
        }
    }

    /// <summary>Clears the failed-attempt counter and any lockout after a success.</summary>
    public void RegisterSuccessfulLogin() {
        this.FailedLoginAttempts = 0;
        this.LockedUntilUtc = null;
    }

    public static Result<HexString> GenerateEmailHash(string email) {
        if(string.IsNullOrWhiteSpace(email)) {
            return AuthErrors.EmailInvalid(email ?? "");
        }

        string normalized = email.Trim().ToLowerInvariant();
        return Sha256Hash.Compute(normalized).ToHexStringLower();
    }

    public Result<Success> ChangeEmail(string newEmail, EncryptedSecret<EmailSecretContext> newEncryptedEmail) {
        string normalizedEmail = newEmail?.Trim().ToLowerInvariant() ?? "";
        if(normalizedEmail.Length is 0 || !normalizedEmail.Contains('@'))
            return AuthErrors.EmailInvalid(newEmail ?? "");

        var hashResult = GenerateEmailHash(normalizedEmail);
        if(hashResult.IsFailure) {
            return hashResult.FirstError;
        }

        this.EncryptedEmail = newEncryptedEmail;
        this.EmailKeyVersion = newEncryptedEmail.KeyVersion.Value;
        this.EmailHash = hashResult.Value;

        return Result.Success();
    }

    public void RotateEmailKey(EncryptedSecret<EmailSecretContext> rotatedEmail) {
        this.EncryptedEmail = rotatedEmail;
        this.EmailKeyVersion = rotatedEmail.KeyVersion.Value;
    }
}