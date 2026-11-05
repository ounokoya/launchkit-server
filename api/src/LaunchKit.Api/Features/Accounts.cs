using LaunchKit.Api.Auth;
using LaunchKit.Api.Common;
using LaunchKit.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Features;

public sealed record UserView(string Id, string Name, string Email, bool EmailVerified, bool IsSuperAdmin, string Locale);

public sealed record SessionView(string Token, string ExpiresAt, UserView User);

public sealed record MyOrganization(string Id, string Name, string Plan, string Role, int Members);

public sealed record MeView(UserView User, List<MyOrganization> Organizations);

/// <summary>Comptes (server-api.md): sign-up, e-mail verification, sign-in, sign-out, profile.</summary>
public static class Accounts
{
    public sealed record RegisterRequest(string? Name, string? Email, string? Password, string? Locale);
    public sealed record TokenRequest(string? Token);
    public sealed record EmailRequest(string? Email);
    public sealed record LoginRequest(string? Email, string? Password);
    public sealed record UpdateMeRequest(string? Name, string? Locale);

    private const string UserFields =
        "{ id: u._key, name: u.name, email: u.email, emailVerified: u.emailVerified, isSuperAdmin: u.isSuperAdmin, locale: u.locale }";

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Comptes");
        auth.MapPost("/register", Register);
        auth.MapPost("/verify-email", VerifyEmail);
        auth.MapPost("/verify-email/resend", Resend);
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", Logout);
        api.MapGet("/me", GetMe).WithTags("Comptes");
        api.MapPatch("/me", UpdateMe).WithTags("Comptes");
    }

    private static async Task<IResult> Register(RegisterRequest body, HttpContext http, ArangoClient db, Texts texts,
        IPasswordHasher<object> hasher, IOptions<AppOptions> app, TimeProvider clock)
    {
        var locale = Texts.Normalize(body.Locale ?? Texts.Resolve(http));
        new Check(texts, locale)
            .Required("name", body.Name).Length("name", body.Name, 80)
            .Email("email", body.Email)
            .Password("password", body.Password)
            .OneOf("locale", body.Locale, Texts.Locales, optional: true)
            .ThrowIfInvalid();

        var now = Iso.Now(clock);
        var email = Emails.Normalize(body.Email!);
        var name = body.Name!.Trim();
        var token = Tokens.New();
        var user = new
        {
            email,
            name,
            passwordHash = hasher.HashPassword(new object(), body.Password!),
            emailVerified = false,
            emailVerifiedAt = (string?)null,
            isSuperAdmin = false,
            locale,
            createdAt = now,
        };
        try
        {
            var created = await db.FirstAsync<UserView>($$"""
                LET u = FIRST(INSERT @user INTO users RETURN NEW)
                INSERT { userId: u._key, purpose: "verify_email", tokenHash: @hash, createdAt: @now, expiresAt: @expires }
                  INTO email_tokens
                INSERT @job INTO jobs
                RETURN {{UserFields}}
                """, new
            {
                user,
                hash = Tokens.Hash(token),
                now,
                expires = Iso.Of(clock.GetUtcNow().AddHours(app.Value.VerifyEmailHours)),
                job = VerifyEmailJob(email, name, locale, token, app.Value, now),
            });
            return Results.Created("/api/v1/me", new { user = created });
        }
        catch (ArangoException e) when (e.IsUniqueViolation)
        {
            throw ApiException.EmailTaken();
        }
    }

    private static object VerifyEmailJob(string email, string name, string locale, string token, AppOptions app, string now)
        => Jobs.SendEmail("verify_email", email, locale, new
        {
            firstName = Emails.FirstName(name),
            link = app.Link($"/verify-email?token={Uri.EscapeDataString(token)}"),
            hours = app.VerifyEmailHours,
        }, now);

    /// <summary>The link of the e-mail: marks the address verified and opens a session (W04 → W05).</summary>
    private static async Task<IResult> VerifyEmail(TokenRequest body, ArangoClient db, IOptions<AppOptions> app,
        TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(body.Token))
            throw ApiException.InvalidLink();
        var now = Iso.Now(clock);
        var session = Tokens.New();
        var expires = Iso.Of(clock.GetUtcNow().AddDays(app.Value.SessionLifetimeDays));
        var user = await db.FirstAsync<UserView>($$"""
            FOR t IN email_tokens
              FILTER t.tokenHash == @hash AND t.purpose == "verify_email" AND t.expiresAt > @now
              LIMIT 1
              UPDATE t.userId WITH { emailVerified: true, emailVerifiedAt: @now } IN users
              LET u = NEW
              REMOVE t IN email_tokens
              INSERT { userId: u._key, tokenHash: @session, createdAt: @now, expiresAt: @expires } INTO sessions
              RETURN {{UserFields}}
            """, new { hash = Tokens.Hash(body.Token), now, session = Tokens.Hash(session), expires });
        return user is null
            ? throw ApiException.InvalidLink()
            : Results.Ok(new SessionView(session, expires, user));
    }

    /// <summary>Always 202, so the answer never tells whether an account exists.</summary>
    private static async Task<IResult> Resend(EmailRequest body, HttpContext http, ArangoClient db, Texts texts,
        IOptions<AppOptions> app, TimeProvider clock)
    {
        new Check(texts, Texts.Resolve(http)).Email("email", body.Email).ThrowIfInvalid();
        var email = Emails.Normalize(body.Email!);
        await using var trx = await db.BeginAsync(write: ["email_tokens", "jobs"], read: ["users"]);
        var user = await trx.FirstAsync<UserView>($$"""
            FOR u IN users FILTER u.email == @email AND u.emailVerified == false LIMIT 1 RETURN {{UserFields}}
            """, new { email });
        if (user is not null)
        {
            var now = Iso.Now(clock);
            var token = Tokens.New();
            await trx.ExecuteAsync("FOR t IN email_tokens FILTER t.userId == @user REMOVE t IN email_tokens",
                new { user = user.Id });
            await trx.ExecuteAsync("""
                INSERT { userId: @user, purpose: "verify_email", tokenHash: @hash, createdAt: @now, expiresAt: @expires }
                  INTO email_tokens
                INSERT @job INTO jobs
                """, new
            {
                user = user.Id,
                hash = Tokens.Hash(token),
                now,
                expires = Iso.Of(clock.GetUtcNow().AddHours(app.Value.VerifyEmailHours)),
                job = VerifyEmailJob(user.Email, user.Name, user.Locale, token, app.Value, now),
            });
            await trx.CommitAsync();
        }
        return Results.Accepted();
    }

    private sealed record Credentials(string Id, string PasswordHash, bool EmailVerified);

    // Hash compared when the e-mail is unknown, so both cases take the same time.
    private static readonly string DummyHash = new PasswordHasher<object>().HashPassword(new object(), Tokens.New());

    private static async Task<IResult> Login(LoginRequest body, HttpContext http, ArangoClient db, Texts texts,
        IPasswordHasher<object> hasher, IOptions<AppOptions> app, TimeProvider clock)
    {
        new Check(texts, Texts.Resolve(http)).Email("email", body.Email).Required("password", body.Password)
            .ThrowIfInvalid();
        var email = Emails.Normalize(body.Email!);
        var found = await db.FirstAsync<Credentials>("""
            FOR u IN users FILTER u.email == @email LIMIT 1
              RETURN { id: u._key, passwordHash: u.passwordHash, emailVerified: u.emailVerified }
            """, new { email });
        var check = hasher.VerifyHashedPassword(new object(), found?.PasswordHash ?? DummyHash, body.Password!);
        if (found is null || check == PasswordVerificationResult.Failed)
            throw ApiException.LoginFailed();
        if (!found.EmailVerified)
            throw ApiException.EmailNotVerified();

        var now = Iso.Now(clock);
        var session = Tokens.New();
        var expires = Iso.Of(clock.GetUtcNow().AddDays(app.Value.SessionLifetimeDays));
        var user = await db.FirstAsync<UserView>($$"""
            LET u = DOCUMENT("users", @user)
            LET rehash = (FILTER @rehash != null UPDATE u WITH { passwordHash: @rehash } IN users)
            INSERT { userId: u._key, tokenHash: @session, createdAt: @now, expiresAt: @expires } INTO sessions
            RETURN {{UserFields}}
            """, new
        {
            user = found.Id,
            rehash = check == PasswordVerificationResult.SuccessRehashNeeded
                ? hasher.HashPassword(new object(), body.Password!)
                : null,
            session = Tokens.Hash(session),
            now,
            expires,
        });
        return Results.Ok(new SessionView(session, expires, user!));
    }

    private static async Task<IResult> Logout(HttpContext http, ArangoClient db)
    {
        http.Account();
        var token = SessionAuthHandler.BearerToken(http.Request)!;
        await db.ExecuteAsync("FOR s IN sessions FILTER s.tokenHash == @hash REMOVE s IN sessions",
            new { hash = Tokens.Hash(token) });
        return Results.NoContent();
    }

    private static async Task<MeView> GetMe(HttpContext http, ArangoClient db)
    {
        var me = http.Account();
        return (await db.FirstAsync<MeView>($$"""
            LET u = DOCUMENT("users", @user)
            LET orgs = (
              FOR o, m IN 1..1 OUTBOUND u memberships
                SORT o.name
                RETURN { id: o._key, name: o.name, plan: o.plan, role: m.role, members: o.usage.members })
            RETURN { user: {{UserFields}}, organizations: orgs }
            """, new { user = me.Id }))!;
    }

    private static async Task<object> UpdateMe(UpdateMeRequest body, HttpContext http, ArangoClient db, Texts texts)
    {
        var me = http.Account();
        var check = new Check(texts, me.Locale).OneOf("locale", body.Locale, Texts.Locales, optional: true);
        if (body.Name is not null)
            check.Required("name", body.Name).Length("name", body.Name, 80);
        check.ThrowIfInvalid();
        var user = await db.FirstAsync<UserView>($$"""
            UPDATE @user WITH @changes IN users
            LET u = NEW
            RETURN {{UserFields}}
            """, new
        {
            user = me.Id,
            changes = new Dictionary<string, object?>
            {
                ["name"] = body.Name?.Trim() ?? me.Name,
                ["locale"] = body.Locale ?? me.Locale,
            },
        });
        return new { user };
    }
}
