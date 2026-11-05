using LaunchKit.Api.Auth;
using LaunchKit.Api.Infrastructure;

namespace LaunchKit.Api.Features;

/// <summary>Back-office (server-api.md; W12): read-only views for the super admin.</summary>
public static class Admin
{
    public sealed record Stats(int Organizations, int Users, int Pro, int PendingJobs);

    public sealed record OrganizationRow(string Id, string Name, string Plan, OwnerRow Owner, int Members, int Projects,
        string CreatedAt);

    public sealed record OwnerRow(string? Email);

    public sealed record UserRow(string Id, string Name, string Email, bool EmailVerified, int Organizations,
        string CreatedAt);

    public static void Map(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Back-office");
        admin.MapGet("/stats", GetStats);
        admin.MapGet("/organizations", ListOrganizations);
        admin.MapGet("/users", ListUsers);
    }

    private static async Task<Stats> GetStats(HttpContext http, ArangoClient db)
    {
        http.SuperAdmin();
        return (await db.FirstAsync<Stats>("""
            RETURN {
              organizations: LENGTH(organizations),
              users: LENGTH(users),
              pro: LENGTH(FOR o IN organizations FILTER o.plan == "pro" RETURN 1),
              pendingJobs: LENGTH(FOR j IN jobs FILTER j.status IN ["pending", "running"] RETURN 1)
            }
            """))!;
    }

    private static async Task<List<OrganizationRow>> ListOrganizations(HttpContext http, ArangoClient db)
    {
        http.SuperAdmin();
        return await db.QueryAsync<OrganizationRow>("""
            FOR o IN organizations SORT o.createdAt DESC
              RETURN { id: o._key, name: o.name, plan: o.plan, owner: { email: DOCUMENT("users", o.ownerId).email },
                       members: o.usage.members, projects: o.usage.projects, createdAt: o.createdAt }
            """);
    }

    private static async Task<List<UserRow>> ListUsers(HttpContext http, ArangoClient db)
    {
        http.SuperAdmin();
        return await db.QueryAsync<UserRow>("""
            FOR u IN users SORT u.createdAt DESC
              RETURN { id: u._key, name: u.name, email: u.email, emailVerified: u.emailVerified,
                       organizations: LENGTH(FOR m IN memberships FILTER m._from == u._id RETURN 1),
                       createdAt: u.createdAt }
            """);
    }
}
