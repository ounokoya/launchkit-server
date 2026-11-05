using LaunchKit.Api.Auth;
using LaunchKit.Api.Common;
using LaunchKit.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Features;

/// <summary>Projets (server-api.md; W10, W11, M04, M05): the sample business module, limited by plan (ADR-0003).</summary>
public static class Projects
{
    public static readonly string[] Statuses = ["active", "in_progress", "draft"];

    public sealed record ProjectRequest(string? Name, string? Description, string? Status, string? LeadId, string? DueDate);

    public static void Map(RouteGroupBuilder api)
    {
        var projects = api.MapGroup("/orgs/{orgId}/projects").WithTags("Projets");
        projects.MapGet("/", List);
        projects.MapPost("/", Create);
        projects.MapGet("/{projectId}", Get);
        projects.MapPatch("/{projectId}", Update);
        projects.MapDelete("/{projectId}", Delete);
    }

    private static async Task<List<ProjectView>> List(string orgId, HttpContext http, ArangoClient db, OrgAccess access)
    {
        await access.RequireAsync(http.Verified(), orgId, OrgRole.Member);
        return await db.QueryAsync<ProjectView>($$"""
            FOR p IN projects FILTER p.orgId == @org SORT p.updatedAt DESC RETURN {{Organizations.ProjectFields}}
            """, new { org = orgId });
    }

    private static void Validate(ProjectRequest body, Check check, bool creating)
    {
        if (creating || body.Name is not null)
            check.Required("name", body.Name).Length("name", body.Name, 80);
        check.Length("description", body.Description, 2000)
            .OneOf("status", body.Status, Statuses, optional: true)
            .Date("dueDate", body.DueDate)
            .ThrowIfInvalid();
    }

    private static async Task RequireLeadIsMemberAsync(ArangoClient db, Texts texts, string locale, string orgId,
        string? leadId)
    {
        if (string.IsNullOrEmpty(leadId))
            return;
        var isMember = await db.FirstAsync<bool>("""
            RETURN LENGTH(FOR m IN memberships
              FILTER m._from == CONCAT("users/", @lead) AND m._to == CONCAT("organizations/", @org) RETURN 1) > 0
            """, new { lead = leadId, org = orgId });
        if (!isMember)
            new Check(texts, locale).OneOf("leadId", leadId, []).ThrowIfInvalid();
    }

    private static async Task<IResult> Create(string orgId, ProjectRequest body, HttpContext http, ArangoClient db,
        OrgAccess access, Texts texts, IOptions<PlansOptions> plans, TimeProvider clock)
    {
        var user = http.Verified();
        await access.RequireAsync(user, orgId, OrgRole.Member);
        Validate(body, new Check(texts, user.Locale), creating: true);
        await RequireLeadIsMemberAsync(db, texts, user.Locale, orgId, body.LeadId);

        var org = await Organizations.Load(db, orgId);
        var max = plans.Value.For(org.Plan).MaxProjects;
        var now = Iso.Now(clock);
        var project = await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<ProjectView>($$"""
            LET o = DOCUMENT("organizations", @org)
            LET leadName = @project.leadId == null ? null : DOCUMENT("users", @project.leadId).name
            FILTER @max == null OR o.usage.projects < @max
            UPDATE o WITH { usage: { projects: o.usage.projects + 1 } } IN organizations
            LET p = FIRST(INSERT @project INTO projects RETURN NEW)
            INSERT { orgId: @org, projectId: p._key, actorId: @user, type: "project_created", data: { project: p.name },
                     createdAt: @now }
              INTO activities
            RETURN {{Organizations.ProjectFieldsAfterWrite}}
            """, new
        {
            org = orgId,
            max,
            user = user.Id,
            now,
            project = new
            {
                orgId,
                name = body.Name!.Trim(),
                description = body.Description?.Trim() ?? "",
                status = body.Status ?? "draft",
                leadId = string.IsNullOrEmpty(body.LeadId) ? null : body.LeadId,
                dueDate = body.DueDate,
                createdBy = user.Id,
                createdAt = now,
                updatedAt = now,
            },
        }));
        return project is null
            ? throw ApiException.ProjectLimit(max!.Value)
            : Results.Created($"/api/v1/orgs/{orgId}/projects/{project.Id}", new { project });
    }

    private sealed record ProjectDetail(ProjectView Project, List<MemberView> Members, List<ActivityView> History);

    private static async Task<ProjectDetail> Get(string orgId, string projectId, HttpContext http, ArangoClient db,
        OrgAccess access)
    {
        await access.RequireAsync(http.Verified(), orgId, OrgRole.Member);
        return await db.FirstAsync<ProjectDetail>($$"""
            FOR p IN projects FILTER p._key == @project AND p.orgId == @org
              RETURN {
                project: {{Organizations.ProjectFields}},
                members: (FOR u, m IN 1..1 INBOUND CONCAT("organizations/", @org) memberships SORT m.joinedAt
                            RETURN { id: u._key, name: u.name, email: u.email, role: m.role, joinedAt: m.joinedAt }),
                history: (FOR a IN activities FILTER a.projectId == p._key SORT a.createdAt DESC
                            RETURN {{Organizations.ActivityFields}})
              }
            """, new { org = orgId, project = projectId }) ?? throw ApiException.NotFound();
    }

    private sealed record Current(string Name, string Description, string Status);

    private static async Task<object> Update(string orgId, string projectId, ProjectRequest body, HttpContext http,
        ArangoClient db, OrgAccess access, Texts texts, TimeProvider clock)
    {
        var user = http.Verified();
        await access.RequireAsync(user, orgId, OrgRole.Member);
        Validate(body, new Check(texts, user.Locale), creating: false);
        await RequireLeadIsMemberAsync(db, texts, user.Locale, orgId, body.LeadId);

        var current = await db.FirstAsync<Current>("""
            FOR p IN projects FILTER p._key == @project AND p.orgId == @org
              RETURN { name: p.name, description: p.description, status: p.status }
            """, new { org = orgId, project = projectId }) ?? throw ApiException.NotFound();

        var now = Iso.Now(clock);
        var changes = new Dictionary<string, object?> { ["updatedAt"] = now };
        if (body.Name is not null) changes["name"] = body.Name.Trim();
        if (body.Description is not null) changes["description"] = body.Description.Trim();
        if (body.Status is not null) changes["status"] = body.Status;
        if (body.LeadId is not null) changes["leadId"] = body.LeadId.Length == 0 ? null : body.LeadId;
        if (body.DueDate is not null) changes["dueDate"] = body.DueDate;

        var name = (string)(changes.GetValueOrDefault("name") ?? current.Name)!;
        var activities = new List<object>();
        if (body.Status is not null && body.Status != current.Status)
            activities.Add(Activity("project_status_changed", new { project = name, status = body.Status }));
        if (body.Description is not null && body.Description.Trim() != current.Description)
            activities.Add(Activity("project_description_changed", new { project = name }));

        var project = await db.FirstAsync<ProjectView>($$"""
            LET before = DOCUMENT("projects", @project)
            LET lead = HAS(@changes, "leadId") ? @changes.leadId : before.leadId
            LET leadName = lead == null ? null : DOCUMENT("users", lead).name
            LET p = FIRST(UPDATE before WITH @changes IN projects RETURN NEW)
            LET logged = (FOR a IN @activities INSERT a INTO activities)
            RETURN {{Organizations.ProjectFieldsAfterWrite}}
            """, new { project = projectId, changes, activities });
        return new { project };

        object Activity(string type, object data) => new
        {
            orgId,
            projectId,
            actorId = user.Id,
            type,
            data,
            createdAt = now,
        };
    }

    /// <summary>Owner, admins and the project's creator can delete it; the counter goes down in the same query.</summary>
    private static async Task<IResult> Delete(string orgId, string projectId, HttpContext http, ArangoClient db,
        OrgAccess access)
    {
        var user = http.Verified();
        var membership = await access.RequireAsync(user, orgId, OrgRole.Member);
        var createdBy = await db.FirstAsync<string>(
            "FOR p IN projects FILTER p._key == @project AND p.orgId == @org RETURN p.createdBy",
            new { org = orgId, project = projectId }) ?? throw ApiException.NotFound();
        if (membership.Role < OrgRole.Admin && createdBy != user.Id)
            throw ApiException.Forbidden(membership.OrgName);
        await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<int?>("""
            LET o = DOCUMENT("organizations", @org)
            FOR p IN projects FILTER p._key == @project AND p.orgId == @org
              REMOVE p IN projects
              UPDATE o WITH { usage: { projects: o.usage.projects - 1 } } IN organizations
              RETURN 1
            """, new { org = orgId, project = projectId }));
        return Results.NoContent();
    }
}
