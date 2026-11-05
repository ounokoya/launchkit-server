using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LaunchKit.Scenario.Tests;

/// <summary>Runs only against a running stack (compose.yaml): set SCENARIO_API_URL, SCENARIO_MAILPIT_URL.</summary>
public sealed class ScenarioFactAttribute : FactAttribute
{
    public ScenarioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCENARIO_API_URL") is null)
            Skip = "Start the stack (docker compose up) and set SCENARIO_API_URL / SCENARIO_MAILPIT_URL.";
    }
}

/// <summary>
/// Scénario A / B (server-api.md, « Exemples »): the API, the Rust worker, ArangoDB and Mailpit
/// together. Every link is read from the e-mails actually delivered to Mailpit.
/// </summary>
public sealed partial class ScenarioAbTests
{
    private static readonly Uri Api = new(Environment.GetEnvironmentVariable("SCENARIO_API_URL") ?? "http://localhost:5080");
    private static readonly Uri Mailpit = new(Environment.GetEnvironmentVariable("SCENARIO_MAILPIT_URL") ?? "http://localhost:8025");
    private static readonly string AdminPassword = Environment.GetEnvironmentVariable("SCENARIO_ADMIN_PASSWORD") ?? "admin-demo-local";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(45);

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    [ScenarioFact]
    public async Task A_and_B_go_through_the_whole_journey()
    {
        // 0. The API answers once its schema is ready (the container may still be starting).
        using (var probe = new HttpClient())
        {
            await WaitUntilAsync(async () =>
            {
                try
                {
                    return (await probe.GetAsync(new Uri(Api, "/health"))).IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            });
        }

        // 1. A signs up; the e-mail arrives in Mailpit; the link verifies the account and opens a session.
        var a = await SignUpAndVerifyAsync("Marie Koné", $"marie-{_run}@demo.test");

        // 2. A creates Acme and 3 projects; the 4th is refused (409).
        var org = await PostAsync(a, "/api/v1/orgs", new { name = $"Acme {_run}" }, HttpStatusCode.Created);
        var orgId = org["org"]!["id"]!.GetValue<string>();
        foreach (var name in new[] { "Site vitrine", "App mobile", "Tableau de bord" })
            await PostAsync(a, $"/api/v1/orgs/{orgId}/projects", new { name }, HttpStatusCode.Created);
        var fourth = await PostAsync(a, $"/api/v1/orgs/{orgId}/projects", new { name = "Quatrième" }, HttpStatusCode.Conflict);
        Assert.Equal("plan_limit_reached", fourth["code"]!.GetValue<string>());

        // 3. A invites B; B signs up, accepts from the e-mail link; B cannot open billing (403).
        var bEmail = $"jean-{_run}@demo.test";
        await PostAsync(a, $"/api/v1/orgs/{orgId}/invitations", new { email = bEmail, role = "member" }, HttpStatusCode.Created);
        var invitationMail = await WaitForMailAsync(bEmail, "invite");
        Assert.Contains("Marie Koné", invitationMail.Subject, StringComparison.Ordinal);
        var invitationToken = Token(invitationMail.Text, InvitationLink());
        var b = await SignUpAndVerifyAsync("Jean Diallo", bEmail);
        var accepted = await PostAsync(b, $"/api/v1/invitations/{invitationToken}/accept", null, HttpStatusCode.OK);
        Assert.Equal("member", accepted["role"]!.GetValue<string>());
        var billingForB = await b.GetAsync(new Uri(Api, $"/api/v1/orgs/{orgId}/billing"));
        Assert.Equal(HttpStatusCode.Forbidden, billingForB.StatusCode);

        // 4. A upgrades: the Rust worker processes the simulated webhook, Acme becomes Pro, A gets the e-mail.
        await PostAsync(a, $"/api/v1/orgs/{orgId}/billing/checkout", new { plan = "pro" }, HttpStatusCode.Accepted);
        await WaitUntilAsync(async () =>
        {
            var billing = await a.GetFromJsonAsync<JsonNode>(new Uri(Api, $"/api/v1/orgs/{orgId}/billing"));
            return billing!["plan"]!.GetValue<string>() == "pro";
        });
        var proMail = await WaitForMailAsync($"marie-{_run}@demo.test", "Pro");
        Assert.Contains("Pro", proMail.Subject, StringComparison.Ordinal);

        // 5. The 4th project is accepted; the back office shows Acme on Pro.
        await PostAsync(a, $"/api/v1/orgs/{orgId}/projects", new { name = "Quatrième" }, HttpStatusCode.Created);
        var admin = await SignInAsync("admin@demo.test", AdminPassword);
        var orgs = await admin.GetFromJsonAsync<JsonArray>(new Uri(Api, "/api/v1/admin/organizations"));
        var acme = orgs!.Single(o => o!["id"]!.GetValue<string>() == orgId)!;
        Assert.Equal("pro", acme["plan"]!.GetValue<string>());
        Assert.Equal(4, acme["projects"]!.GetValue<int>());
        Assert.Equal(2, acme["members"]!.GetValue<int>());
    }

    private static async Task<HttpClient> SignUpAndVerifyAsync(string name, string email)
    {
        using var anonymous = new HttpClient();
        await PostAsync(anonymous, "/api/v1/auth/register", new { name, email, password = "motdepasse-1", locale = "fr" },
            HttpStatusCode.Created);
        var mail = await WaitForMailAsync(email, "adresse");
        var token = Token(mail.Text, VerifyLink());
        var session = await PostAsync(anonymous, "/api/v1/auth/verify-email", new { token }, HttpStatusCode.OK);
        return Authenticated(session["token"]!.GetValue<string>());
    }

    private static async Task<HttpClient> SignInAsync(string email, string password)
    {
        using var anonymous = new HttpClient();
        var session = await PostAsync(anonymous, "/api/v1/auth/login", new { email, password }, HttpStatusCode.OK);
        return Authenticated(session["token"]!.GetValue<string>());
    }

    private static HttpClient Authenticated(string token)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonNode> PostAsync(HttpClient client, string path, object? body, HttpStatusCode expected)
    {
        var res = await client.PostAsJsonAsync(new Uri(Api, path), body ?? new { });
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, $"POST {path}: {(int)res.StatusCode} instead of {(int)expected}: {text}");
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }

    private sealed record Mail(string Subject, string Text);

    /// <summary>Polls Mailpit until a message to <paramref name="to"/> whose subject contains <paramref name="subject"/>.</summary>
    private static async Task<Mail> WaitForMailAsync(string to, string subject)
    {
        using var mailpit = new HttpClient { BaseAddress = Mailpit };
        Mail? found = null;
        await WaitUntilAsync(async () =>
        {
            var search = await mailpit.GetFromJsonAsync<JsonNode>($"/api/v1/search?query={Uri.EscapeDataString($"to:\"{to}\"")}");
            var hit = search!["messages"]!.AsArray()
                .FirstOrDefault(m => m!["Subject"]!.GetValue<string>().Contains(subject, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                return false;
            var message = await mailpit.GetFromJsonAsync<JsonNode>($"/api/v1/message/{hit["ID"]!.GetValue<string>()}");
            found = new Mail(message!["Subject"]!.GetValue<string>(), message["Text"]!.GetValue<string>());
            return true;
        });
        return found!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the worker / Mailpit.");
            await Task.Delay(500);
        }
    }

    private static string Token(string text, Regex link)
    {
        var match = link.Match(text);
        Assert.True(match.Success, $"No link in the e-mail:\n{text}");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex(@"/verify-email\?token=([A-Za-z0-9_\-%]+)")]
    private static partial Regex VerifyLink();

    [GeneratedRegex(@"/invitations/([A-Za-z0-9_\-%]+)")]
    private static partial Regex InvitationLink();
}
