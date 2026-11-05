using System.Net;
using System.Text.Json.Nodes;

namespace LaunchKit.Api.IntegrationTests;

/// <summary>server-api.md, « Organisations » and « Projets »; ADR-0002 (isolation) and ADR-0003 (limits).</summary>
[Collection("api")]
public sealed class OrganizationAndProjectTests(ApiFixture api)
{
    [Fact]
    public async Task Creating_an_organization_makes_the_creator_owner_on_the_free_plan()
    {
        var marie = await api.VerifiedUserAsync();

        var res = await marie.PostJsonAsync("/api/v1/orgs", new { name = " Acme " }, HttpStatusCode.Created);

        var org = res["org"]!;
        Assert.Equal("Acme", org["name"]!.GetValue<string>());
        Assert.Equal("free", org["plan"]!.GetValue<string>());
        Assert.Equal(1, org["usage"]!["members"]!.GetValue<int>());
        Assert.Equal(3, org["limits"]!["projects"]!.GetValue<int>());
        Assert.Equal("owner", res["role"]!.GetValue<string>());
        var list = await marie.GetJsonAsync("/api/v1/orgs");
        Assert.Single(list.AsArray());
        var dashboard = await marie.GetJsonAsync($"/api/v1/orgs/{org["id"]}/dashboard");
        Assert.Equal("org_created", dashboard["activity"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Marie Koné", dashboard["activity"]![0]!["actor"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Someone_outside_the_organization_gets_404_everywhere()
    {
        var marie = await api.VerifiedUserAsync();
        var orgId = await marie.CreateOrgAsync();
        var project = await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/projects", new { name = "Site vitrine" },
            HttpStatusCode.Created);
        var projectId = project["project"]!["id"]!.GetValue<string>();
        var stranger = await api.VerifiedUserAsync("Yao");

        foreach (var url in new[]
                 {
                     $"/api/v1/orgs/{orgId}", $"/api/v1/orgs/{orgId}/dashboard", $"/api/v1/orgs/{orgId}/members",
                     $"/api/v1/orgs/{orgId}/projects", $"/api/v1/orgs/{orgId}/projects/{projectId}",
                     $"/api/v1/orgs/{orgId}/billing",
                 })
        {
            await Client.ProblemAsync(await stranger.GetAsync(url), HttpStatusCode.NotFound, "not_found");
        }
        await Client.ProblemAsync(await stranger.DeleteAsync($"/api/v1/orgs/{orgId}/projects/{projectId}"),
            HttpStatusCode.NotFound, "not_found");
        await Client.ProblemAsync(await stranger.PostAsync($"/api/v1/orgs/{orgId}/projects", new { name = "x" }),
            HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task The_fourth_project_on_free_is_refused_with_409_then_accepted_on_pro()
    {
        var marie = await api.VerifiedUserAsync();
        var orgId = await marie.CreateOrgAsync();
        foreach (var name in new[] { "Site vitrine", "App mobile", "Tableau de bord" })
            await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/projects", new { name }, HttpStatusCode.Created);

        var refused = await marie.PostAsync($"/api/v1/orgs/{orgId}/projects", new { name = "Quatrième" });
        var problem = await Client.ProblemAsync(refused, HttpStatusCode.Conflict, "plan_limit_reached");
        Assert.Equal("Vous avez atteint la limite de la formule Free : 3 projets sur 3. Passez en Pro pour en créer d’autres.",
            problem["detail"]!.GetValue<string>());
        Assert.Equal(3, (await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/projects")).AsArray().Count);

        await api.Db.ExecuteAsync("UPDATE @org WITH { plan: \"pro\" } IN organizations", new { org = orgId });
        await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/projects", new { name = "Quatrième" }, HttpStatusCode.Created);
        var org = await marie.GetJsonAsync($"/api/v1/orgs/{orgId}");
        Assert.Equal(4, org["org"]!["usage"]!["projects"]!.GetValue<int>());
        Assert.Null(org["org"]!["limits"]!["projects"]);
    }

    [Fact]
    public async Task The_project_limit_holds_under_concurrent_creation()
    {
        var marie = await api.VerifiedUserAsync();
        var orgId = await marie.CreateOrgAsync();

        var results = await Task.WhenAll(Enumerable.Range(1, 12)
            .Select(i => marie.PostAsync($"/api/v1/orgs/{orgId}/projects", new { name = $"Projet {i}" })));

        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var stored = await api.Db.FirstAsync<int>(
            "RETURN LENGTH(FOR p IN projects FILTER p.orgId == @org RETURN 1)", new { org = orgId });
        Assert.Equal(3, stored);
        var usage = await api.Db.FirstAsync<int>("RETURN DOCUMENT(\"organizations\", @org).usage.projects", new { org = orgId });
        Assert.Equal(3, usage);
    }

    [Fact]
    public async Task A_project_can_be_read_updated_with_history_and_deleted()
    {
        var marie = await api.VerifiedUserAsync("Marie Koné");
        var jean = await api.VerifiedUserAsync("Jean Diallo");
        var orgId = await marie.CreateOrgAsync();
        await marie.AddMemberAsync(orgId, jean);

        var created = await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/projects",
            new { name = "Site vitrine", description = "Refonte", leadId = jean.UserId, dueDate = "2026-11-28" },
            HttpStatusCode.Created);
        var id = created["project"]!["id"]!.GetValue<string>();
        Assert.Equal("draft", created["project"]!["status"]!.GetValue<string>());
        Assert.Equal("Jean Diallo", created["project"]!["leadName"]!.GetValue<string>());

        var res = await jean.PatchAsync($"/api/v1/orgs/{orgId}/projects/{id}",
            new { status = "active", description = "Refonte complète" });
        var updated = await Client.ReadAsync(res, HttpStatusCode.OK);
        Assert.Equal("active", updated["project"]!["status"]!.GetValue<string>());

        var detail = await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/projects/{id}");
        var types = detail["history"]!.AsArray().Select(a => a!["type"]!.GetValue<string>()).ToList();
        Assert.Contains("project_status_changed", types);
        Assert.Contains("project_description_changed", types);
        Assert.Contains("project_created", types);
        Assert.Equal(2, detail["members"]!.AsArray().Count);

        // A member who did not create the project cannot delete it; the owner can.
        await Client.ProblemAsync(await jean.DeleteAsync($"/api/v1/orgs/{orgId}/projects/{id}"),
            HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(HttpStatusCode.NoContent, (await marie.DeleteAsync($"/api/v1/orgs/{orgId}/projects/{id}")).StatusCode);
        await Client.ProblemAsync(await marie.GetAsync($"/api/v1/orgs/{orgId}/projects/{id}"),
            HttpStatusCode.NotFound, "not_found");
        var org = await marie.GetJsonAsync($"/api/v1/orgs/{orgId}");
        Assert.Equal(0, org["org"]!["usage"]!["projects"]!.GetValue<int>());
    }

    [Fact]
    public async Task Project_input_is_validated()
    {
        var marie = await api.VerifiedUserAsync();
        var stranger = await api.VerifiedUserAsync();
        var orgId = await marie.CreateOrgAsync();

        var res = await marie.PostAsync($"/api/v1/orgs/{orgId}/projects",
            new { name = "", status = "archived", dueDate = "28/11/2026" });
        var problem = await Client.ProblemAsync(res, HttpStatusCode.BadRequest, "invalid_request");
        Assert.NotNull(problem["errors"]!["name"]);
        Assert.NotNull(problem["errors"]!["status"]);
        Assert.NotNull(problem["errors"]!["dueDate"]);

        var notMember = await marie.PostAsync($"/api/v1/orgs/{orgId}/projects", new { name = "x", leadId = stranger.UserId });
        Assert.NotNull((await Client.ProblemAsync(notMember, HttpStatusCode.BadRequest, "invalid_request"))["errors"]!["leadId"]);
    }

    [Fact]
    public async Task Billing_is_for_admins_and_checkout_queues_the_simulated_webhook_once()
    {
        var marie = await api.VerifiedUserAsync();
        var jean = await api.VerifiedUserAsync("Jean Diallo");
        var orgId = await marie.CreateOrgAsync("Acme");
        await marie.AddMemberAsync(orgId, jean);

        var forbidden = await Client.ProblemAsync(await jean.GetAsync($"/api/v1/orgs/{orgId}/billing"),
            HttpStatusCode.Forbidden, "forbidden");
        Assert.EndsWith("de l’organisation Acme.", forbidden["detail"]!.GetValue<string>());

        var billing = await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/billing");
        Assert.Equal("free", billing["plan"]!.GetValue<string>());
        Assert.Equal(2900, billing["prices"]!["pro"]!["amount"]!.GetValue<int>());
        Assert.False(billing["pendingPayment"]!.GetValue<bool>());

        var first = await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/billing/checkout", new { plan = "pro" }, HttpStatusCode.Accepted);
        var second = await marie.PostJsonAsync($"/api/v1/orgs/{orgId}/billing/checkout", new { plan = "pro" }, HttpStatusCode.Accepted);
        Assert.Equal(first["paymentId"]!.GetValue<string>(), second["paymentId"]!.GetValue<string>());
        var jobs = await api.Db.QueryAsync<JsonNode>(
            "FOR j IN jobs FILTER j.type == \"payment_webhook\" AND j.payload.paymentId == @p RETURN j",
            new { p = first["paymentId"]!.GetValue<string>() });
        Assert.Single(jobs);
        Assert.True((await marie.GetJsonAsync($"/api/v1/orgs/{orgId}/billing"))["pendingPayment"]!.GetValue<bool>());

        // What the worker does (server-taches.md), then a new checkout is refused.
        await api.Db.ExecuteAsync("UPDATE @org WITH { plan: \"pro\" } IN organizations", new { org = orgId });
        await Client.ProblemAsync(await marie.PostAsync($"/api/v1/orgs/{orgId}/billing/checkout", new { plan = "pro" }),
            HttpStatusCode.Conflict, "already_pro");
        await Client.ProblemAsync(await marie.PostAsync($"/api/v1/orgs/{orgId}/billing/checkout", new { plan = "gold" }),
            HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Concurrent_checkouts_create_a_single_payment()
    {
        var marie = await api.VerifiedUserAsync();
        var orgId = await marie.CreateOrgAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => marie.PostAsync($"/api/v1/orgs/{orgId}/billing/checkout", new { plan = "pro" })));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var payments = await api.Db.FirstAsync<int>(
            "RETURN LENGTH(FOR p IN payments FILTER p.orgId == @org RETURN 1)", new { org = orgId });
        Assert.Equal(1, payments);
    }
}
