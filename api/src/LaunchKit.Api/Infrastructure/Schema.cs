using System.Text.Json.Nodes;
using LaunchKit.Api.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Infrastructure;

/// <summary>Collections and indexes of the data specification (server-donnees.md).</summary>
public static class Schema
{
    public static readonly string[] Documents =
        ["users", "organizations", "invitations", "projects", "activities", "jobs", "payments", "sessions", "email_tokens"];

    public const string Memberships = "memberships";

    public static async Task EnsureAsync(ArangoClient db, CancellationToken ct = default)
    {
        await db.EnsureDatabaseAsync(ct);
        foreach (var name in Documents)
            await db.EnsureCollectionAsync(name, edge: false, ct);
        await db.EnsureCollectionAsync(Memberships, edge: true, ct);

        await db.EnsureIndexAsync("users", Persistent(unique: true, "email"), ct);
        await db.EnsureIndexAsync(Memberships, Persistent(unique: true, "_from", "_to"), ct);
        await db.EnsureIndexAsync("invitations", Persistent(unique: true, "tokenHash"), ct);
        await db.EnsureIndexAsync("invitations", Persistent(unique: false, "orgId", "status"), ct);
        await db.EnsureIndexAsync("invitations", Persistent(unique: false, "status", "expiresAt"), ct);
        await db.EnsureIndexAsync("projects", Persistent(unique: false, "orgId", "updatedAt"), ct);
        await db.EnsureIndexAsync("activities", Persistent(unique: false, "orgId", "createdAt"), ct);
        await db.EnsureIndexAsync("activities", Persistent(unique: false, "projectId", "createdAt"), ct);
        await db.EnsureIndexAsync("jobs", Persistent(unique: false, "status", "runAt"), ct);
        await db.EnsureIndexAsync("payments", Persistent(unique: false, "orgId", "status"), ct);
        await db.EnsureIndexAsync("sessions", Persistent(unique: true, "tokenHash"), ct);
        await db.EnsureIndexAsync("sessions", Ttl("expiresAt"), ct);
        await db.EnsureIndexAsync("email_tokens", Persistent(unique: true, "tokenHash"), ct);
        await db.EnsureIndexAsync("email_tokens", Ttl("expiresAt"), ct);
    }

    private static JsonObject Persistent(bool unique, params string[] fields) => new()
    {
        ["type"] = "persistent",
        ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)f)]),
        ["unique"] = unique,
    };

    // Documents are removed once their ISO 8601 date in `expiresAt` is in the past.
    private static JsonObject Ttl(string field) => new()
    {
        ["type"] = "ttl",
        ["fields"] = new JsonArray(field),
        ["expireAfter"] = 0,
    };
}

public sealed class SeedOptions
{
    /// <summary>Super admin of the demo back office (W12). No account is created when the password is empty.</summary>
    public string AdminEmail { get; set; } = "admin@demo.test";
    public string AdminPassword { get; set; } = "";
}

/// <summary>Creates the schema and the demo super admin before the API starts serving requests.</summary>
public sealed partial class StartupInitializer(
    ArangoClient db,
    IOptions<ArangoOptions> arango,
    IOptions<SeedOptions> seed,
    IPasswordHasher<object> hasher,
    TimeProvider clock,
    ILogger<StartupInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (!arango.Value.InitializeSchema)
            return;
        await Schema.EnsureAsync(db, ct);

        var s = seed.Value;
        if (string.IsNullOrEmpty(s.AdminPassword))
            return;
        var now = Iso.Now(clock);
        await db.ExecuteAsync("""
            UPSERT { email: @email }
            INSERT { email: @email, name: "Super Admin", passwordHash: @hash, emailVerified: true,
                     emailVerifiedAt: @now, isSuperAdmin: true, locale: "fr", createdAt: @now }
            UPDATE { isSuperAdmin: true }
            IN users
            """, new { email = Emails.Normalize(s.AdminEmail), hash = hasher.HashPassword(new object(), s.AdminPassword), now }, ct: ct);
        LogAdminReady(log, s.AdminEmail);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Super admin {Email} ready")]
    private static partial void LogAdminReady(ILogger logger, string email);
}
