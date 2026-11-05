using System.Security.Claims;
using System.Text.Encodings.Web;
using LaunchKit.Api.Common;
using LaunchKit.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Auth;

public static class Claims
{
    public const string UserId = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Locale = "locale";
    public const string Verified = "email_verified";
    public const string SuperAdmin = "super_admin";
}

/// <summary>
/// Opaque session tokens (ADR-0004): <c>Authorization: Bearer &lt;token&gt;</c>, looked up by SHA-256 in
/// <c>sessions</c>. A missing or expired token simply leaves the request anonymous; the endpoint filters answer 401.
/// </summary>
public sealed class SessionAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ArangoClient db,
    TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";

    private sealed record SessionUser(string Id, string Email, string Name, string Locale, bool EmailVerified, bool IsSuperAdmin);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = BearerToken(Request);
        if (token is null)
            return AuthenticateResult.NoResult();

        var user = await db.FirstAsync<SessionUser>("""
            FOR s IN sessions
              FILTER s.tokenHash == @hash AND s.expiresAt > @now
              LIMIT 1
              LET u = DOCUMENT("users", s.userId)
              FILTER u != null
              RETURN { id: u._key, email: u.email, name: u.name, locale: u.locale,
                       emailVerified: u.emailVerified, isSuperAdmin: u.isSuperAdmin }
            """, new { hash = Tokens.Hash(token), now = Iso.Now(clock) }, ct: Context.RequestAborted);
        if (user is null)
            return AuthenticateResult.NoResult();

        var identity = new ClaimsIdentity(
        [
            new Claim(Claims.UserId, user.Id),
            new Claim(Claims.Email, user.Email),
            new Claim(Claims.Name, user.Name),
            new Claim(Claims.Locale, user.Locale),
            new Claim(Claims.Verified, user.EmailVerified ? "true" : "false"),
            new Claim(Claims.SuperAdmin, user.IsSuperAdmin ? "true" : "false"),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length
            ? header[prefix.Length..].Trim()
            : null;
    }
}

/// <summary>The signed-in user, read from the claims set by <see cref="SessionAuthHandler"/>.</summary>
public sealed record CurrentUser(string Id, string Email, string Name, string Locale, bool EmailVerified, bool IsSuperAdmin)
{
    public static CurrentUser? From(ClaimsPrincipal p)
    {
        var id = p.FindFirst(Claims.UserId)?.Value;
        if (id is null)
            return null;
        return new CurrentUser(id, p.FindFirst(Claims.Email)!.Value, p.FindFirst(Claims.Name)!.Value,
            p.FindFirst(Claims.Locale)!.Value, p.FindFirst(Claims.Verified)!.Value == "true",
            p.FindFirst(Claims.SuperAdmin)!.Value == "true");
    }
}

/// <summary>Access levels of server-api.md: compte, connecté (verified e-mail), super.</summary>
public static class Access
{
    public static CurrentUser Account(this HttpContext http)
        => CurrentUser.From(http.User) ?? throw ApiException.Unauthorized();

    public static CurrentUser Verified(this HttpContext http)
    {
        var user = http.Account();
        return user.EmailVerified ? user : throw ApiException.EmailNotVerified();
    }

    public static CurrentUser SuperAdmin(this HttpContext http)
    {
        var user = http.Verified();
        return user.IsSuperAdmin ? user : throw ApiException.Forbidden();
    }
}

/// <summary>Membership check (ADR-0002): not a member → 404, member without the role → 403.</summary>
public sealed class OrgAccess(ArangoClient db)
{
    public sealed record Membership(string OrgId, string OrgName, OrgRole Role);

    private sealed record Row(string Name, string Role);

    public async Task<Membership> RequireAsync(CurrentUser user, string orgId, OrgRole minimum, CancellationToken ct = default)
    {
        var row = await db.FirstAsync<Row>("""
            FOR m IN memberships
              FILTER m._from == CONCAT("users/", @user) AND m._to == CONCAT("organizations/", @org)
              LET o = DOCUMENT(m._to)
              RETURN { name: o.name, role: m.role }
            """, new { user = user.Id, org = orgId }, ct: ct);
        if (row is null)
            throw ApiException.NotFound();
        var role = Roles.Parse(row.Role);
        if (role < minimum)
            throw ApiException.Forbidden(row.Name);
        return new Membership(orgId, row.Name, role);
    }
}
