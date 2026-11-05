using LaunchKit.Api.Auth;
using LaunchKit.Api.Common;
using LaunchKit.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Features;

public sealed record Usage(int Projects, int Members, int PendingInvitations);

public sealed record Limits(int? Projects, int? Members);

public sealed record OrgView(string Id, string Name, string Plan, Usage Usage, Limits Limits, string CreatedAt);

public sealed record ActivityView(string Id, string Type, Dictionary<string, object?> Data, string? ProjectId,
    ActorView? Actor, string CreatedAt);

public sealed record ActorView(string Id, string Name);

public sealed record ProjectView(string Id, string Name, string Description, string Status, string? LeadId,
    string? LeadName, string? DueDate, string CreatedBy, string CreatedAt, string UpdatedAt);

/// <summary>Organisations (server-api.md): creation, list, detail, dashboard (W05, W06, M03).</summary>
public static class Organizations
{
    internal sealed record OrgRow(string Id, string Name, string Plan, Usage Usage, string CreatedAt);

    /// <summary>AQL shape of an organization document <c>o</c>.</summary>
    internal const string OrgFields = "{ id: o._key, name: o.name, plan: o.plan, usage: o.usage, createdAt: o.createdAt }";

    internal const string ActivityFields = """
        { id: a._key, type: a.type, data: a.data, projectId: a.projectId, createdAt: a.createdAt,
          actor: a.actorId == null ? null : FIRST(FOR u IN users FILTER u._key == a.actorId RETURN { id: u._key, name: u.name }) }
        """;

    internal const string ProjectFields = """
        { id: p._key, name: p.name, description: p.description, status: p.status, leadId: p.leadId,
          leadName: p.leadId == null ? null : DOCUMENT("users", p.leadId).name,
          dueDate: p.dueDate, createdBy: p.createdBy, createdAt: p.createdAt, updatedAt: p.updatedAt }
        """;

    /// <summary>Same shape, for queries that just wrote: ArangoDB forbids DOCUMENT() after a write, so the lead's
    /// name is read beforehand into <c>leadName</c>.</summary>
    internal const string ProjectFieldsAfterWrite = """
        { id: p._key, name: p.name, description: p.description, status: p.status, leadId: p.leadId,
          leadName: leadName, dueDate: p.dueDate, createdBy: p.createdBy, createdAt: p.createdAt, updatedAt: p.updatedAt }
        """;

    internal static OrgView View(OrgRow o, PlansOptions plans)
    {
        var limits = plans.For(o.Plan);
        return new OrgView(o.Id, o.Name, o.Plan, o.Usage, new Limits(limits.MaxProjects, limits.MaxMembers), o.CreatedAt);
    }

    public sealed record CreateRequest(string? Name);

    public static void Map(RouteGroupBuilder api)
    {
        var orgs = api.MapGroup("/orgs").WithTags("Organisations");
        orgs.MapPost("/", Create);
        orgs.MapGet("/", List);
        orgs.MapGet("/{orgId}", Get);
        orgs.MapGet("/{orgId}/dashboard", Dashboard);
    }

    private static async Task<IResult> Create(CreateRequest body, HttpContext http, ArangoClient db, Texts texts,
        IOptions<PlansOptions> plans, TimeProvider clock)
    {
        var me = http.Verified();
        new Check(texts, me.Locale).Required("name", body.Name).Length("name", body.Name, 60).ThrowIfInvalid();
        var now = Iso.Now(clock);
        var org = await db.FirstAsync<OrgRow>($$"""
            LET o = FIRST(INSERT { name: @name, plan: "free", ownerId: @user, createdAt: @now, upgradedAt: null,
                                   usage: { projects: 0, members: 1, pendingInvitations: 0 } }
                          INTO organizations RETURN NEW)
            INSERT { _from: CONCAT("users/", @user), _to: o._id, role: "owner", joinedAt: @now } INTO memberships
            INSERT { orgId: o._key, projectId: null, actorId: @user, type: "org_created", data: { org: o.name }, createdAt: @now }
              INTO activities
            RETURN {{OrgFields}}
            """, new { name = body.Name!.Trim(), user = me.Id, now });
        var view = View(org!, plans.Value);
        return Results.Created($"/api/v1/orgs/{view.Id}", new { org = view, role = "owner" });
    }

    private sealed record OrgWithRole(OrgRow Org, string Role);

    private static async Task<IEnumerable<object>> List(HttpContext http, ArangoClient db, IOptions<PlansOptions> plans)
    {
        var me = http.Verified();
        var rows = await db.QueryAsync<OrgWithRole>($$"""
            FOR o, m IN 1..1 OUTBOUND CONCAT("users/", @user) memberships
              SORT o.name
              RETURN { org: {{OrgFields}}, role: m.role }
            """, new { user = me.Id });
        return rows.Select(r => new { org = View(r.Org, plans.Value), role = r.Role });
    }

    private static async Task<object> Get(string orgId, HttpContext http, ArangoClient db, OrgAccess access,
        IOptions<PlansOptions> plans)
    {
        var membership = await access.RequireAsync(http.Verified(), orgId, OrgRole.Member);
        var org = await Load(db, orgId);
        return new { org = View(org, plans.Value), role = Roles.Code(membership.Role) };
    }

    internal static async Task<OrgRow> Load(ArangoClient db, string orgId)
        => await db.FirstAsync<OrgRow>($"LET o = DOCUMENT(\"organizations\", @org) FILTER o != null RETURN {OrgFields}",
            new { org = orgId }) ?? throw ApiException.NotFound();

    private sealed record DashboardRow(OrgRow Org, List<ProjectView> RecentProjects, List<ActivityView> Activity);

    private static async Task<object> Dashboard(string orgId, HttpContext http, ArangoClient db, OrgAccess access,
        IOptions<PlansOptions> plans)
    {
        var membership = await access.RequireAsync(http.Verified(), orgId, OrgRole.Member);
        var row = await db.FirstAsync<DashboardRow>($$"""
            LET o = DOCUMENT("organizations", @org)
            RETURN {
              org: {{OrgFields}},
              recentProjects: (FOR p IN projects FILTER p.orgId == @org SORT p.updatedAt DESC LIMIT 3 RETURN {{ProjectFields}}),
              activity: (FOR a IN activities FILTER a.orgId == @org SORT a.createdAt DESC LIMIT 10 RETURN {{ActivityFields}})
            }
            """, new { org = orgId });
        return new
        {
            org = View(row!.Org, plans.Value),
            role = Roles.Code(membership.Role),
            recentProjects = row.RecentProjects,
            activity = row.Activity,
        };
    }
}
