using LaunchKit.Api.Auth;
using LaunchKit.Api.Common;
using LaunchKit.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Features;

public sealed record MemberView(string Id, string Name, string Email, string Role, string JoinedAt);

public sealed record InvitationView(string Id, string Email, string Role, string CreatedAt, string ExpiresAt, bool Expired);

/// <summary>Membres et invitations (server-api.md; W07, W08, M06). Limits follow ADR-0003.</summary>
public static class Members
{
    public sealed record RoleRequest(string? Role);
    public sealed record InviteRequest(string? Email, string? Role);

    public static void Map(RouteGroupBuilder api)
    {
        var org = api.MapGroup("/orgs/{orgId}").WithTags("Membres et invitations");
        org.MapGet("/members", List);
        org.MapPatch("/members/{userId}", ChangeRole);
        org.MapDelete("/members/{userId}", Remove);
        org.MapPost("/invitations", Invite);
        org.MapPost("/invitations/{invitationId}/resend", Resend);
        org.MapDelete("/invitations/{invitationId}", Cancel);

        var invitations = api.MapGroup("/invitations/{token}").WithTags("Membres et invitations");
        invitations.MapGet("/", Show);
        invitations.MapPost("/accept", Accept);
        invitations.MapPost("/decline", Decline);
    }

    private static async Task<object> List(string orgId, HttpContext http, ArangoClient db, OrgAccess access,
        TimeProvider clock)
    {
        var me = await access.RequireAsync(http.Verified(), orgId, OrgRole.Member);
        var members = await db.QueryAsync<MemberView>("""
            FOR u, m IN 1..1 INBOUND CONCAT("organizations/", @org) memberships
              SORT m.joinedAt
              RETURN { id: u._key, name: u.name, email: u.email, role: m.role, joinedAt: m.joinedAt }
            """, new { org = orgId });
        // Pending invitations show e-mail addresses: owner and admins only.
        var invitations = me.Role >= OrgRole.Admin
            ? await db.QueryAsync<InvitationView>("""
                FOR i IN invitations
                  FILTER i.orgId == @org AND i.status == "pending"
                  SORT i.createdAt
                  RETURN { id: i._key, email: i.email, role: i.role, createdAt: i.createdAt, expiresAt: i.expiresAt,
                           expired: i.expiresAt <= @now }
                """, new { org = orgId, now = Iso.Now(clock) })
            : [];
        return new { members, invitations };
    }

    private static async Task<object> ChangeRole(string orgId, string userId, RoleRequest body, HttpContext http,
        ArangoClient db, OrgAccess access, Texts texts)
    {
        var user = http.Verified();
        await access.RequireAsync(user, orgId, OrgRole.Admin);
        new Check(texts, user.Locale).OneOf("role", body.Role, Roles.Assignable).ThrowIfInvalid();
        await RequireNotOwnerAsync(db, orgId, userId);
        var member = await db.FirstAsync<MemberView>("""
            FOR m IN memberships
              FILTER m._from == CONCAT("users/", @user) AND m._to == CONCAT("organizations/", @org) AND m.role != "owner"
              LET u = DOCUMENT(m._from)
              UPDATE m WITH { role: @role } IN memberships
              RETURN { id: u._key, name: u.name, email: u.email, role: NEW.role, joinedAt: NEW.joinedAt }
            """, new { user = userId, org = orgId, role = body.Role });
        return new { member = member ?? throw ApiException.NotFound() };
    }

    /// <summary>Admins remove anyone but the owner; a member may also leave on their own.</summary>
    private static async Task<IResult> Remove(string orgId, string userId, HttpContext http, ArangoClient db,
        OrgAccess access)
    {
        var user = http.Verified();
        await access.RequireAsync(user, orgId, userId == user.Id ? OrgRole.Member : OrgRole.Admin);
        await RequireNotOwnerAsync(db, orgId, userId);
        var removed = await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<int?>("""
            LET o = DOCUMENT("organizations", @org)
            FOR m IN memberships
              FILTER m._from == CONCAT("users/", @user) AND m._to == CONCAT("organizations/", @org) AND m.role != "owner"
              REMOVE m IN memberships
              UPDATE o WITH { usage: { members: o.usage.members - 1 } } IN organizations
              RETURN 1
            """, new { user = userId, org = orgId }));
        return removed is null ? throw ApiException.NotFound() : Results.NoContent();
    }

    private static async Task RequireNotOwnerAsync(ArangoClient db, string orgId, string userId)
    {
        var role = await db.FirstAsync<string>("""
            FOR m IN memberships
              FILTER m._from == CONCAT("users/", @user) AND m._to == CONCAT("organizations/", @org)
              RETURN m.role
            """, new { user = userId, org = orgId });
        if (role is null)
            throw ApiException.NotFound();
        if (role == "owner")
            throw ApiException.OwnerProtected();
    }

    private sealed record InviteContext(string Plan, bool IsMember, bool AlreadyInvited, string? InviteeLocale);

    private static async Task<IResult> Invite(string orgId, InviteRequest body, HttpContext http, ArangoClient db,
        OrgAccess access, Texts texts, IOptions<PlansOptions> plans, IOptions<AppOptions> app, TimeProvider clock)
    {
        var user = http.Verified();
        var membership = await access.RequireAsync(user, orgId, OrgRole.Admin);
        new Check(texts, user.Locale).Email("email", body.Email).OneOf("role", body.Role, Roles.Assignable)
            .ThrowIfInvalid();
        var email = Emails.Normalize(body.Email!);
        var now = Iso.Now(clock);

        var context = (await db.FirstAsync<InviteContext>("""
            LET invitee = FIRST(FOR u IN users FILTER u.email == @email RETURN u)
            RETURN {
              plan: DOCUMENT("organizations", @org).plan,
              isMember: invitee != null AND LENGTH(
                FOR m IN memberships FILTER m._from == invitee._id AND m._to == CONCAT("organizations/", @org) RETURN 1) > 0,
              alreadyInvited: LENGTH(
                FOR i IN invitations
                  FILTER i.orgId == @org AND i.email == @email AND i.status == "pending" AND i.expiresAt > @now
                  RETURN 1) > 0,
              inviteeLocale: invitee.locale
            }
            """, new { org = orgId, email, now }))!;
        if (context.IsMember)
            throw ApiException.AlreadyMember(email, membership.OrgName);
        if (context.AlreadyInvited)
            throw ApiException.AlreadyInvited(email);

        var max = plans.Value.For(context.Plan).MaxMembers;
        var token = Tokens.New();
        var invitation = await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<InvitationView>("""
            LET o = DOCUMENT("organizations", @org)
            FILTER @max == null OR o.usage.members + o.usage.pendingInvitations < @max
            UPDATE o WITH { usage: { pendingInvitations: o.usage.pendingInvitations + 1 } } IN organizations
            LET i = FIRST(
              INSERT { orgId: @org, email: @email, role: @role, tokenHash: @hash, invitedBy: @user, status: "pending",
                       createdAt: @now, expiresAt: @expires, respondedAt: null }
              INTO invitations RETURN NEW)
            INSERT { orgId: @org, projectId: null, actorId: @user, type: "invitation_sent", data: { email: @email },
                     createdAt: @now }
              INTO activities
            INSERT @job INTO jobs
            RETURN { id: i._key, email: i.email, role: i.role, createdAt: i.createdAt, expiresAt: i.expiresAt, expired: false }
            """, new
        {
            org = orgId,
            max,
            email,
            role = body.Role,
            hash = Tokens.Hash(token),
            user = user.Id,
            now,
            expires = Iso.Of(clock.GetUtcNow().AddDays(app.Value.InvitationDays)),
            job = InvitationJob(email, context.InviteeLocale ?? user.Locale, user.Name, membership.OrgName, body.Role!,
                token, app.Value, now),
        }));
        return invitation is null
            ? throw ApiException.MemberLimit(max!.Value)
            : Results.Created($"/api/v1/orgs/{orgId}/members", new { invitation });
    }

    private static object InvitationJob(string email, string locale, string inviter, string org, string role,
        string token, AppOptions app, string now)
        => Jobs.SendEmail("invitation", email, locale, new
        {
            inviter,
            org,
            role,
            link = app.Link($"/invitations/{Uri.EscapeDataString(token)}"),
            days = app.InvitationDays,
        }, now);

    private sealed record PendingInvitation(string Email, string Role, string? InviteeLocale);

    private static async Task<IResult> Resend(string orgId, string invitationId, HttpContext http, ArangoClient db,
        OrgAccess access, IOptions<AppOptions> app, TimeProvider clock)
    {
        var user = http.Verified();
        var membership = await access.RequireAsync(user, orgId, OrgRole.Admin);
        var pending = await db.FirstAsync<PendingInvitation>("""
            FOR i IN invitations FILTER i._key == @inv AND i.orgId == @org AND i.status == "pending"
              RETURN { email: i.email, role: i.role,
                       inviteeLocale: FIRST(FOR u IN users FILTER u.email == i.email RETURN u.locale) }
            """, new { inv = invitationId, org = orgId }) ?? throw ApiException.NotFound();
        var now = Iso.Now(clock);
        var token = Tokens.New();
        await db.ExecuteAsync("""
            FOR i IN invitations FILTER i._key == @inv AND i.status == "pending"
              UPDATE i WITH { tokenHash: @hash, expiresAt: @expires } IN invitations
              INSERT @job INTO jobs
            """, new
        {
            inv = invitationId,
            hash = Tokens.Hash(token),
            expires = Iso.Of(clock.GetUtcNow().AddDays(app.Value.InvitationDays)),
            job = InvitationJob(pending.Email, pending.InviteeLocale ?? user.Locale, user.Name, membership.OrgName,
                pending.Role, token, app.Value, now),
        });
        return Results.Accepted();
    }

    private static async Task<IResult> Cancel(string orgId, string invitationId, HttpContext http, ArangoClient db,
        OrgAccess access, TimeProvider clock)
    {
        await access.RequireAsync(http.Verified(), orgId, OrgRole.Admin);
        var done = await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<int?>("""
            FOR i IN invitations
              FILTER i._key == @inv AND i.orgId == @org AND i.status == "pending"
              LET o = DOCUMENT("organizations", @org)
              UPDATE i WITH { status: "canceled", respondedAt: @now } IN invitations
              UPDATE o WITH { usage: { pendingInvitations: o.usage.pendingInvitations - 1 } } IN organizations
              RETURN 1
            """, new { inv = invitationId, org = orgId, now = Iso.Now(clock) }));
        return done is null ? throw ApiException.NotFound() : Results.NoContent();
    }

    // ----- invitation link (W08) -----

    private sealed record InvitationRow(string Id, string Status, string Email, string Role, string ExpiresAt,
        string OrgId, string OrgName, string Plan, string Inviter);

    private static async Task<InvitationRow> LoadByTokenAsync(ArangoClient db, string token, TimeProvider clock)
    {
        var row = await db.FirstAsync<InvitationRow>("""
            FOR i IN invitations FILTER i.tokenHash == @hash LIMIT 1
              LET o = DOCUMENT("organizations", i.orgId)
              RETURN { id: i._key, status: i.status, email: i.email, role: i.role, expiresAt: i.expiresAt,
                       orgId: o._key, orgName: o.name, plan: o.plan, inviter: DOCUMENT("users", i.invitedBy).name }
            """, new { hash = Tokens.Hash(token) });
        if (row is null || row.Status != "pending")
            throw ApiException.InvitationUnavailable();
        if (string.CompareOrdinal(row.ExpiresAt, Iso.Now(clock)) <= 0)
            throw ApiException.InvitationExpired(row.Inviter);
        return row;
    }

    private static async Task<object> Show(string token, HttpContext http, ArangoClient db, TimeProvider clock)
    {
        http.Verified();
        var i = await LoadByTokenAsync(db, token, clock);
        return new { org = new { id = i.OrgId, name = i.OrgName }, inviter = i.Inviter, role = i.Role, email = i.Email, expiresAt = i.ExpiresAt };
    }

    private static async Task<object> Accept(string token, HttpContext http, ArangoClient db,
        IOptions<PlansOptions> plans, TimeProvider clock)
    {
        var user = http.Verified();
        var i = await LoadByTokenAsync(db, token, clock);
        if (i.Email != user.Email)
            throw ApiException.WrongAccount(i.Email);
        var max = plans.Value.For(i.Plan).MaxMembers;
        try
        {
            var joined = await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<string>("""
                FOR i IN invitations
                  FILTER i._key == @inv AND i.status == "pending"
                  LET o = DOCUMENT("organizations", i.orgId)
                  FILTER @max == null OR o.usage.members < @max
                  UPDATE i WITH { status: "accepted", respondedAt: @now } IN invitations
                  UPDATE o WITH { usage: { members: o.usage.members + 1,
                                           pendingInvitations: o.usage.pendingInvitations - 1 } } IN organizations
                  INSERT { _from: CONCAT("users/", @user), _to: o._id, role: i.role, joinedAt: @now } INTO memberships
                  INSERT { orgId: o._key, projectId: null, actorId: @user, type: "member_joined", data: { name: @name },
                           createdAt: @now } INTO activities
                  RETURN i.role
                """, new { inv = i.Id, max, user = user.Id, name = user.Name, now = Iso.Now(clock) }));
            if (joined is null)
                throw max is null ? ApiException.InvitationUnavailable() : ApiException.MemberLimit(max.Value);
            return new { org = new { id = i.OrgId, name = i.OrgName }, role = joined };
        }
        catch (ArangoException e) when (e.IsUniqueViolation)
        {
            throw ApiException.AlreadyMember(user.Email, i.OrgName);
        }
    }

    private static async Task<IResult> Decline(string token, HttpContext http, ArangoClient db, TimeProvider clock)
    {
        var user = http.Verified();
        var i = await LoadByTokenAsync(db, token, clock);
        if (i.Email != user.Email)
            throw ApiException.WrongAccount(i.Email);
        await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<int?>("""
            FOR i IN invitations
              FILTER i._key == @inv AND i.status == "pending"
              LET o = DOCUMENT("organizations", i.orgId)
              UPDATE i WITH { status: "declined", respondedAt: @now } IN invitations
              UPDATE o WITH { usage: { pendingInvitations: o.usage.pendingInvitations - 1 } } IN organizations
              RETURN 1
            """, new { inv = i.Id, now = Iso.Now(clock) }));
        return Results.NoContent();
    }
}
