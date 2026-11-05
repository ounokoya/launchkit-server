using System.Net;
using System.Text.Json.Nodes;

namespace LaunchKit.Api.IntegrationTests;

/// <summary>server-api.md, « Membres et invitations »; member limit of ADR-0003.</summary>
[Collection("api")]
public sealed class MemberTests(ApiFixture api)
{
    private async Task<(Client Owner, string OrgId)> OrgAsync()
    {
        var owner = await api.VerifiedUserAsync("Marie Koné");
        return (owner, await owner.CreateOrgAsync("Acme"));
    }

    [Fact]
    public async Task An_invitation_queues_an_email_in_the_invitees_language_and_counts_as_pending()
    {
        var (marie, orgId) = await OrgAsync();
        var jean = await api.VerifiedUserAsync("Jean Diallo", locale: "en");

        var res = await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = jean.Email, role = "admin" },
            HttpStatusCode.Created);

        Assert.Equal("admin", res["invitation"]!["role"]!.GetValue<string>());
        var job = await api.Db.FirstAsync<JsonNode>("""
            FOR j IN jobs FILTER j.payload.to == @email AND j.payload.template == "invitation" RETURN j.payload
            """, new { email = jean.Email });
        Assert.Equal("en", job!["locale"]!.GetValue<string>());
        Assert.Equal("Marie Koné", job["params"]!["inviter"]!.GetValue<string>());
        Assert.Equal("Acme", job["params"]!["org"]!.GetValue<string>());
        Assert.Equal(7, job["params"]!["days"]!.GetValue<int>());
        var members = await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/members");
        Assert.Single(members["invitations"]!.AsArray());
        Assert.Equal(1, (await marie.GetJsonAsync($"/api/v1/orgs/{orgId}"))["org"]!["usage"]!["pendingInvitations"]!.GetValue<int>());
    }

    [Fact]
    public async Task Accepting_joins_the_organization_with_the_proposed_role()
    {
        var (marie, orgId) = await OrgAsync();
        var jean = await api.VerifiedUserAsync("Jean Diallo");
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = jean.Email, role = "member" },
            HttpStatusCode.Created);
        var token = await api.LastLinkTokenAsync(jean.Email!, "/invitations/");

        var shown = await jean.GetJsonAsync($"/api/v1/invitations/{token}");
        Assert.Equal("Acme", shown["org"]!["name"]!.GetValue<string>());
        Assert.Equal("Marie Koné", shown["inviter"]!.GetValue<string>());

        var accepted = await jean.PostJsonAsync($"/api/v1/invitations/{token}/accept");
        Assert.Equal("member", accepted["role"]!.GetValue<string>());

        var org = await jean.GetJsonAsync($"/api/v1/orgs/{orgId}");
        Assert.Equal("member", org["role"]!.GetValue<string>());
        Assert.Equal(2, org["org"]!["usage"]!["members"]!.GetValue<int>());
        Assert.Equal(0, org["org"]!["usage"]!["pendingInvitations"]!.GetValue<int>());
        // Members do not see pending invitations (they show e-mail addresses).
        Assert.Empty((await jean.GetJsonAsync($"/api/v1/orgs/{orgId}/members"))["invitations"]!.AsArray());
        await Client.ProblemAsync(await jean.PostAsync($"/api/v1/invitations/{token}/accept"),
            HttpStatusCode.Gone, "invitation_unavailable");
    }

    [Fact]
    public async Task Only_the_invited_address_can_accept_or_decline()
    {
        var (marie, orgId) = await OrgAsync();
        var invitee = ApiFixture.NewEmail("awa");
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = invitee, role = "member" },
            HttpStatusCode.Created);
        var token = await api.LastLinkTokenAsync(invitee, "/invitations/");
        var someoneElse = await api.VerifiedUserAsync("Yao");

        var problem = await Client.ProblemAsync(await someoneElse.PostAsync($"/api/v1/invitations/{token}/accept"),
            HttpStatusCode.Forbidden, "wrong_account");
        Assert.Contains(invitee, problem["detail"]!.GetValue<string>());
        await Client.ProblemAsync(await someoneElse.PostAsync($"/api/v1/invitations/{token}/decline"),
            HttpStatusCode.Forbidden, "wrong_account");

        var awa = await api.VerifiedUserAsync("Awa Traoré", invitee);
        Assert.Equal(HttpStatusCode.NoContent, (await awa.PostAsync($"/api/v1/invitations/{token}/decline")).StatusCode);
        Assert.Equal(0, (await marie.GetJsonAsync($"/api/v1/orgs/{orgId}"))["org"]!["usage"]!["pendingInvitations"]!.GetValue<int>());
    }

    [Fact]
    public async Task Free_counts_members_and_pending_invitations_against_the_limit_of_three()
    {
        var (marie, orgId) = await OrgAsync();
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = ApiFixture.NewEmail(), role = "member" },
            HttpStatusCode.Created);
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = ApiFixture.NewEmail(), role = "member" },
            HttpStatusCode.Created);

        var refused = await marie.PostAsync($"/api/v1/orgs/{orgId}/invitations", new { email = ApiFixture.NewEmail(), role = "member" });

        var problem = await Client.ProblemAsync(refused, HttpStatusCode.Conflict, "plan_limit_reached");
        Assert.Contains("3", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Duplicate_invitations_and_existing_members_are_refused()
    {
        var (marie, orgId) = await OrgAsync();
        var jean = await api.VerifiedUserAsync("Jean Diallo");
        var awa = ApiFixture.NewEmail();
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = awa, role = "member" }, HttpStatusCode.Created);
        await marie.AddMemberAsync(orgId, jean);

        await Client.ProblemAsync(await marie.PostAsync($"/api/v1/orgs/{orgId}/invitations", new { email = awa, role = "member" }),
            HttpStatusCode.Conflict, "already_invited");
        await Client.ProblemAsync(await marie.PostAsync($"/api/v1/orgs/{orgId}/invitations", new { email = jean.Email, role = "member" }),
            HttpStatusCode.Conflict, "already_member");
        await Client.ProblemAsync(await marie.PostAsync($"/api/v1/orgs/{orgId}/invitations", new { email = "x@y.z", role = "owner" }),
            HttpStatusCode.BadRequest, "invalid_request");
        // A plain member cannot invite.
        await Client.ProblemAsync(await jean.PostAsync($"/api/v1/orgs/{orgId}/invitations", new { email = ApiFixture.NewEmail(), role = "member" }),
            HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task An_expired_invitation_answers_410_with_the_inviters_name()
    {
        var (marie, orgId) = await OrgAsync();
        var jean = await api.VerifiedUserAsync("Jean Diallo");
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = jean.Email, role = "member" }, HttpStatusCode.Created);
        var token = await api.LastLinkTokenAsync(jean.Email!, "/invitations/");
        await api.Db.ExecuteAsync("""
            FOR i IN invitations FILTER i.orgId == @org UPDATE i WITH { expiresAt: "2020-01-01T00:00:00.000Z" } IN invitations
            """, new { org = orgId });

        var problem = await Client.ProblemAsync(await jean.PostAsync($"/api/v1/invitations/{token}/accept"),
            HttpStatusCode.Gone, "invitation_unavailable");
        Assert.Contains("Marie Koné", problem["detail"]!.GetValue<string>());

        // Resending renews the link and the expiry date.
        var invitationId = (await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/members"))["invitations"]![0]!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Accepted,
            (await marie.PostAsync($"/api/v1/orgs/{orgId}/invitations/{invitationId}/resend")).StatusCode);
        var renewed = await api.LastLinkTokenAsync(jean.Email!, "/invitations/");
        Assert.NotEqual(token, renewed);
        await jean.PostJsonAsync($"/api/v1/invitations/{renewed}/accept");
    }

    [Fact]
    public async Task Canceling_an_invitation_frees_the_slot()
    {
        var (marie, orgId) = await OrgAsync();
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = ApiFixture.NewEmail(), role = "member" }, HttpStatusCode.Created);
        var invitationId = (await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/members"))["invitations"]![0]!["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.NoContent, (await marie.DeleteAsync($"/api/v1/orgs/{orgId}/invitations/{invitationId}")).StatusCode);

        Assert.Equal(0, (await marie.GetJsonAsync($"/api/v1/orgs/{orgId}"))["org"]!["usage"]!["pendingInvitations"]!.GetValue<int>());
        await Client.ProblemAsync(await marie.DeleteAsync($"/api/v1/orgs/{orgId}/invitations/{invitationId}"),
            HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Roles_can_change_but_the_owner_is_protected()
    {
        var (marie, orgId) = await OrgAsync();
        var jean = await api.VerifiedUserAsync("Jean Diallo");
        await marie.AddMemberAsync(orgId, jean);

        var res = await marie.PatchAsync($"/api/v1/orgs/{orgId}/members/{jean.UserId}", new { role = "admin" });
        Assert.Equal("admin", (await Client.ReadAsync(res, HttpStatusCode.OK))["member"]!["role"]!.GetValue<string>());

        await Client.ProblemAsync(await jean.PatchAsync($"/api/v1/orgs/{orgId}/members/{marie.UserId}", new { role = "member" }),
            HttpStatusCode.Forbidden, "owner_protected");
        await Client.ProblemAsync(await jean.DeleteAsync($"/api/v1/orgs/{orgId}/members/{marie.UserId}"),
            HttpStatusCode.Forbidden, "owner_protected");
        await Client.ProblemAsync(await marie.DeleteAsync($"/api/v1/orgs/{orgId}/members/{marie.UserId}"),
            HttpStatusCode.Forbidden, "owner_protected");
        await Client.ProblemAsync(await marie.PatchAsync($"/api/v1/orgs/{orgId}/members/{jean.UserId}", new { role = "owner" }),
            HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Members_can_be_removed_by_an_admin_or_leave_on_their_own()
    {
        var (marie, orgId) = await OrgAsync();
        var jean = await api.VerifiedUserAsync("Jean Diallo");
        var awa = await api.VerifiedUserAsync("Awa Traoré");
        await marie.AddMemberAsync(orgId, jean);
        await marie.AddMemberAsync(orgId, awa);

        await Client.ProblemAsync(await jean.DeleteAsync($"/api/v1/orgs/{orgId}/members/{awa.UserId}"),
            HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(HttpStatusCode.NoContent, (await jean.DeleteAsync($"/api/v1/orgs/{orgId}/members/{jean.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await marie.DeleteAsync($"/api/v1/orgs/{orgId}/members/{awa.UserId}")).StatusCode);

        await Client.ProblemAsync(await jean.GetAsync($"/api/v1/orgs/{orgId}"), HttpStatusCode.NotFound, "not_found");
        var org = await marie.GetJsonAsync($"/api/v1/orgs/{orgId}");
        Assert.Equal(1, org["org"]!["usage"]!["members"]!.GetValue<int>());
        Assert.Single((await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/members"))["members"]!.AsArray());
    }
}
