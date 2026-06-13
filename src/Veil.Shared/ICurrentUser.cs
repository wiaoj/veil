using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Veil.Shared;

/// <summary>
/// The authenticated principal for the current request, projected from the
/// JWT claims (or an API key principal). <see cref="IsAuthenticated"/> is
/// false for anonymous endpoints (login, internal node-token routes).
/// </summary>
public interface ICurrentUser {
    bool IsAuthenticated { get; }
    /// <summary>Obfuscated user id (JWT <c>sub</c>), or null when unauthenticated.</summary>
    string? UserId { get; }
    string? Email { get; }
    string? Role { get; }
    bool IsInRole(string role);
}

/// <summary>
/// <see cref="ICurrentUser"/> backed by the ambient <see cref="HttpContext"/>.
/// Registered in HTTP hosts (the worker has no request principal).
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser {
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => this.Principal?.Identity?.IsAuthenticated ?? false;

    // JWT registered claim names ("sub"/"email") without taking a dependency
    // on the JWT package.
    public string? UserId =>
        this.Principal?.FindFirstValue("sub")
        ?? this.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? Email =>
        this.Principal?.FindFirstValue("email")
        ?? this.Principal?.FindFirstValue(ClaimTypes.Email);

    public string? Role => this.Principal?.FindFirstValue(ClaimTypes.Role);

    public bool IsInRole(string role) => this.Principal?.IsInRole(role) ?? false;
}
