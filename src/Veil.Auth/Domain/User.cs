using Veil.Auth.Domain.Enums;
using Veil.Auth.Domain.Events;
using Veil.Auth.Domain.ValueObjects;

namespace Veil.Auth.Domain;

/// <summary>
/// A control-plane user. Passwords never reach the aggregate as plaintext —
/// hashing happens at the edge of the module (see Pbkdf2PasswordHasher) and
/// only the encoded hash is stored.
/// </summary>
public sealed class User : Aggregate<UserId> {
    public string Email { get; private set; }
    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public UserRole Role { get; private set; }
    public bool IsDisabled { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private User() { }

    public static Result<User> Create(
        string email,
        string displayName,
        string passwordHash,
        UserRole role,
        DateTimeOffset createdAtUtc) {
        string normalizedEmail = email?.Trim().ToLowerInvariant() ?? "";
        if(normalizedEmail.Length is 0 || !normalizedEmail.Contains('@'))
            return AuthErrors.EmailInvalid(email ?? "");

        if(string.IsNullOrWhiteSpace(displayName))
            return AuthErrors.DisplayNameEmpty;

        if(string.IsNullOrWhiteSpace(passwordHash))
            return AuthErrors.PasswordHashEmpty;

        User user = new() {
            Id = UserId.New(),
            Email = normalizedEmail,
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
            Role = role,
            IsDisabled = false,
            CreatedAtUtc = createdAtUtc
        };

        user.RaiseDomainEvent(new UserCreatedDomainEvent(user.Id));

        return Result<User>.Success(user);
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
}
