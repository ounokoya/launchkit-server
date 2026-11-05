using System.Net;
using System.Net.Http.Json;

namespace LaunchKit.Api.IntegrationTests;

/// <summary>Back-office, technical endpoints and the error contract of server-api.md.</summary>
[Collection("api")]
public sealed class PlatformTests(ApiFixture api)
{
    private async Task<Client> AdminAsync()
    {
        var client = api.Anonymous();
        var session = await client.PostJsonAsync("/api/v1/auth/login",
            new { email = ApiFixture.AdminEmail, password = ApiFixture.AdminPassword });
        Assert.True(session["user"]!["isSuperAdmin"]!.GetValue<bool>());
        client.SignIn(session["token"]!.GetValue<string>(), session["user"]!["id"]!.GetValue<string>(), ApiFixture.AdminEmail);
        return client;
    }

    [Fact]
    public async Task The_super_admin_sees_every_organization_and_user()
    {
        var marie = await api.VerifiedUserAsync();
        var orgId = await marie.CreateOrgAsync("Globex");
        var admin = await AdminAsync();

        var stats = await admin.GetJsonAsync("/api/v1/admin/stats");
        Assert.True(stats["organizations"]!.GetValue<int>() >= 1);
        Assert.True(stats["users"]!.GetValue<int>() >= 2);
        var orgs = await admin.GetJsonAsync("/api/v1/admin/organizations");
        var globex = orgs.AsArray().Single(o => o!["id"]!.GetValue<string>() == orgId)!;
        Assert.Equal(marie.Email, globex["owner"]!["email"]!.GetValue<string>());
        var users = await admin.GetJsonAsync("/api/v1/admin/users");
        Assert.Contains(users.AsArray(), u => u!["email"]!.GetValue<string>() == marie.Email && u["organizations"]!.GetValue<int>() == 1);
    }

    [Fact]
    public async Task Regular_users_cannot_open_the_back_office()
    {
        var marie = await api.VerifiedUserAsync();

        foreach (var url in new[] { "/api/v1/admin/stats", "/api/v1/admin/organizations", "/api/v1/admin/users" })
        {
            var problem = await Client.ProblemAsync(await marie.GetAsync(url), HttpStatusCode.Forbidden, "forbidden");
            Assert.Equal("Accès refusé", problem["detail"]!.GetValue<string>());
        }
        await Client.ProblemAsync(await api.Anonymous().GetAsync("/api/v1/admin/stats"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Messages_follow_accept_language_for_anonymous_requests()
    {
        var english = await Client.ProblemAsync(await api.Anonymous("en-US,en;q=0.9").GetAsync("/api/v1/me"),
            HttpStatusCode.Unauthorized, "unauthorized");
        var french = await Client.ProblemAsync(await api.Anonymous("de-DE").GetAsync("/api/v1/me"),
            HttpStatusCode.Unauthorized, "unauthorized");

        Assert.Equal("Your session has expired. Sign in again to continue.", english["detail"]!.GetValue<string>());
        Assert.Equal("Votre session a expiré. Reconnectez-vous pour continuer.", french["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_forged_or_malformed_token_is_unauthorized()
    {
        var client = api.Anonymous();
        client.SignIn("forged-token", "nobody", "nobody@demo.test");

        await Client.ProblemAsync(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem()
    {
        var http = api.Anonymous();
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        var res = await PostRawAsync(http, "/api/v1/auth/login", content);

        await Client.ProblemAsync(res, HttpStatusCode.BadRequest, "invalid_request");
    }

    private static Task<HttpResponseMessage> PostRawAsync(Client client, string url, HttpContent content)
        => client.SendRawAsync(HttpMethod.Post, url, content);

    [Fact]
    public async Task Health_and_openapi_are_public()
    {
        var client = api.Anonymous();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var doc = await client.GetJsonAsync("/openapi/v1.json");
        Assert.NotNull(doc["paths"]!["/api/v1/orgs/{orgId}/projects"]);
        var res = await client.GetAsync("/health");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        _ = await res.Content.ReadFromJsonAsync<object>();
    }
}
