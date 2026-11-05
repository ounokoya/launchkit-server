using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LaunchKit.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.ArangoDb;

namespace LaunchKit.Api.IntegrationTests;

/// <summary>
/// One real ArangoDB (Testcontainers) and one API for all tests (ADR-0009). Tests never share users or
/// organizations: every e-mail is unique.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@demo.test";
    public const string AdminPassword = "admin-password-for-tests";
    public const string WebBaseUrl = "http://web.test";

    private readonly ArangoDbContainer _arango = new ArangoDbBuilder("arangodb:3.12")
        .WithPassword("test-password")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    public ArangoClient Db => _factory.Services.GetRequiredService<ArangoClient>();

    public async Task InitializeAsync()
    {
        await _arango.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Arango:Url", _arango.GetTransportAddress());
            b.UseSetting("Arango:Password", "test-password");
            b.UseSetting("Arango:Database", "launchkit_tests");
            b.UseSetting("Seed:AdminPassword", AdminPassword);
            b.UseSetting("App:WebBaseUrl", WebBaseUrl);
        });
        _ = _factory.Server; // starts the host: schema and admin seed run before the first request
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _arango.DisposeAsync();
    }

    public Client Anonymous(string? acceptLanguage = null)
    {
        var http = _factory.CreateClient();
        if (acceptLanguage is not null)
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);
        return new Client(http, this);
    }

    public static string NewEmail(string who = "user") => $"{who}-{Guid.NewGuid():N}@demo.test";

    /// <summary>Signs up, reads the verification link from the queued job, opens the session.</summary>
    public async Task<Client> VerifiedUserAsync(string name = "Marie Koné", string? email = null, string locale = "fr")
    {
        email ??= NewEmail();
        var client = Anonymous();
        var res = await client.PostAsync("/api/v1/auth/register", new { name, email, password = "motdepasse-1", locale });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var token = await LastLinkTokenAsync(email, "verify-email?token=");
        var session = await client.PostJsonAsync("/api/v1/auth/verify-email", new { token });
        client.SignIn(session["token"]!.GetValue<string>(), session["user"]!["id"]!.GetValue<string>(), email);
        return client;
    }

    /// <summary>Token carried by the link of the last e-mail job sent to <paramref name="email"/>.</summary>
    public async Task<string> LastLinkTokenAsync(string email, string marker)
    {
        var link = await Db.FirstAsync<string>("""
            FOR j IN jobs FILTER j.type == "send_email" AND j.payload.to == @email
              SORT j.createdAt DESC LIMIT 1 RETURN j.payload.params.link
            """, new { email });
        Assert.NotNull(link);
        var start = link.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, link);
        return Uri.UnescapeDataString(link[(start + marker.Length)..]);
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;

/// <summary>HTTP client of one person, with small JSON helpers.</summary>
public sealed class Client(HttpClient http, ApiFixture fixture)
{
    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public string? Token { get; private set; }

    public void SignIn(string token, string userId, string email)
    {
        Token = token;
        UserId = userId;
        Email = email;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public HttpRequestHeaders Headers => http.DefaultRequestHeaders;

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => http.PostAsJsonAsync(url, body ?? new { });
    public Task<HttpResponseMessage> PatchAsync(string url, object body) => http.PatchAsJsonAsync(url, body);
    public Task<HttpResponseMessage> DeleteAsync(string url) => http.DeleteAsync(url);

    public async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, HttpContent content)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        return await http.SendAsync(req);
    }

    public async Task<JsonNode> GetJsonAsync(string url) => await ReadAsync(await http.GetAsync(url), HttpStatusCode.OK);

    public async Task<JsonNode> PostJsonAsync(string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
        => await ReadAsync(await PostAsync(url, body), expected);

    public static async Task<JsonNode> ReadAsync(HttpResponseMessage res, HttpStatusCode expected)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"{(int)res.StatusCode} instead of {(int)expected}: {text}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>Asserts an application/problem+json answer and returns it.</summary>
    public static async Task<JsonNode> ProblemAsync(HttpResponseMessage res, HttpStatusCode status, string code)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == status, $"{(int)res.StatusCode} instead of {(int)status}: {text}");
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(text)!;
        Assert.Equal(code, problem["code"]!.GetValue<string>());
        return problem;
    }

    public async Task<string> CreateOrgAsync(string name = "Acme")
    {
        var res = await PostJsonAsync("/api/v1/orgs", new { name }, HttpStatusCode.Created);
        return res["org"]!["id"]!.GetValue<string>();
    }

    /// <summary>Invites <paramref name="other"/>, who accepts through the link of the e-mail job.</summary>
    public async Task AddMemberAsync(string orgId, Client other, string role = "member")
    {
        await PostJsonAsync($"/api/v1/orgs/{orgId}/invitations", new { email = other.Email, role }, HttpStatusCode.Created);
        var token = await fixture.LastLinkTokenAsync(other.Email!, "/invitations/");
        await other.PostJsonAsync($"/api/v1/invitations/{token}/accept");
    }
}
