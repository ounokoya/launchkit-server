using System.Net;
using System.Text.Json.Nodes;

namespace LaunchKit.Api.IntegrationTests;

/// <summary>server-api.md, « Comptes ».</summary>
[Collection("api")]
public sealed class AccountTests(ApiFixture api)
{
    [Fact]
    public async Task Register_queues_the_verification_email_and_stores_only_the_token_hash()
    {
        var email = ApiFixture.NewEmail("Marie");
        var client = api.Anonymous();

        var res = await client.PostJsonAsync("/api/v1/auth/register",
            new { name = "  Marie Koné ", email = $"  {email.ToUpperInvariant()} ", password = "motdepasse-1", locale = "fr" },
            HttpStatusCode.Created);

        Assert.Equal(email.ToLowerInvariant(), res["user"]!["email"]!.GetValue<string>());
        Assert.Equal("Marie Koné", res["user"]!["name"]!.GetValue<string>());
        Assert.False(res["user"]!["emailVerified"]!.GetValue<bool>());
        var job = await api.Db.FirstAsync<JsonNode>(
            "FOR j IN jobs FILTER j.payload.to == @email RETURN j", new { email = email.ToLowerInvariant() });
        Assert.Equal("verify_email", job!["payload"]!["template"]!.GetValue<string>());
        Assert.Equal("Marie", job["payload"]!["params"]!["firstName"]!.GetValue<string>());
        Assert.Equal(24, job["payload"]!["params"]!["hours"]!.GetValue<int>());
        Assert.StartsWith($"{ApiFixture.WebBaseUrl}/verify-email?token=", job["payload"]!["params"]!["link"]!.GetValue<string>());
        var token = await api.LastLinkTokenAsync(email.ToLowerInvariant(), "verify-email?token=");
        var stored = await api.Db.FirstAsync<int>(
            "RETURN LENGTH(FOR t IN email_tokens FILTER t.tokenHash == @token RETURN 1)", new { token });
        Assert.Equal(0, stored); // the clear token never lands in email_tokens
    }

    [Fact]
    public async Task Register_rejects_invalid_input_with_translated_field_errors()
    {
        var res = await api.Anonymous("en").PostAsync("/api/v1/auth/register",
            new { name = "", email = "not-an-email", password = "short" });

        var problem = await Client.ProblemAsync(res, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal("This field is required.", problem["errors"]!["name"]![0]!.GetValue<string>());
        Assert.StartsWith("Enter a valid email address", problem["errors"]!["email"]![0]!.GetValue<string>());
        Assert.Equal("Password must be at least 8 characters.", problem["errors"]!["password"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_twice_with_the_same_email_is_a_conflict()
    {
        var email = ApiFixture.NewEmail();
        var body = new { name = "Jean Diallo", email, password = "motdepasse-1" };
        Assert.Equal(HttpStatusCode.Created, (await api.Anonymous().PostAsync("/api/v1/auth/register", body)).StatusCode);

        var res = await api.Anonymous().PostAsync("/api/v1/auth/register", body);

        var problem = await Client.ProblemAsync(res, HttpStatusCode.Conflict, "email_taken");
        Assert.StartsWith("Un compte existe déjà", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Verify_email_opens_a_session_once_and_the_link_cannot_be_reused()
    {
        var email = ApiFixture.NewEmail();
        var client = api.Anonymous();
        await client.PostJsonAsync("/api/v1/auth/register", new { name = "Awa Traoré", email, password = "motdepasse-1" },
            HttpStatusCode.Created);
        var token = await api.LastLinkTokenAsync(email, "verify-email?token=");

        var session = await client.PostJsonAsync("/api/v1/auth/verify-email", new { token });
        Assert.True(session["user"]!["emailVerified"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(session["token"]!.GetValue<string>()));

        var again = await client.PostAsync("/api/v1/auth/verify-email", new { token });
        await Client.ProblemAsync(again, HttpStatusCode.Gone, "invalid_link");
    }

    [Fact]
    public async Task Login_is_refused_until_the_email_is_verified()
    {
        var email = ApiFixture.NewEmail();
        var client = api.Anonymous();
        await client.PostJsonAsync("/api/v1/auth/register", new { name = "Yao", email, password = "motdepasse-1" },
            HttpStatusCode.Created);

        var res = await client.PostAsync("/api/v1/auth/login", new { email, password = "motdepasse-1" });

        await Client.ProblemAsync(res, HttpStatusCode.Forbidden, "email_not_verified");
    }

    [Fact]
    public async Task Login_with_a_wrong_password_or_an_unknown_email_gives_the_same_answer()
    {
        var user = await api.VerifiedUserAsync();

        var wrongPassword = await api.Anonymous().PostAsync("/api/v1/auth/login", new { email = user.Email, password = "nope-nope" });
        var unknown = await api.Anonymous().PostAsync("/api/v1/auth/login", new { email = ApiFixture.NewEmail(), password = "nope-nope" });

        var a = await Client.ProblemAsync(wrongPassword, HttpStatusCode.Unauthorized, "login_failed");
        var b = await Client.ProblemAsync(unknown, HttpStatusCode.Unauthorized, "login_failed");
        Assert.Equal(a["detail"]!.GetValue<string>(), b["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_then_logout_revokes_the_session_immediately()
    {
        var user = await api.VerifiedUserAsync();
        var client = api.Anonymous();
        var session = await client.PostJsonAsync("/api/v1/auth/login", new { email = user.Email, password = "motdepasse-1" });
        client.SignIn(session["token"]!.GetValue<string>(), session["user"]!["id"]!.GetValue<string>(), user.Email!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/logout")).StatusCode);

        await Client.ProblemAsync(await client.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Resend_always_answers_202_and_replaces_the_previous_link()
    {
        var email = ApiFixture.NewEmail();
        var client = api.Anonymous();
        await client.PostJsonAsync("/api/v1/auth/register", new { name = "Koffi", email, password = "motdepasse-1" },
            HttpStatusCode.Created);
        var first = await api.LastLinkTokenAsync(email, "verify-email?token=");
        await Task.Delay(5); // a later createdAt for the new job

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/v1/auth/verify-email/resend", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsync("/api/v1/auth/verify-email/resend", new { email = ApiFixture.NewEmail() })).StatusCode);

        var second = await api.LastLinkTokenAsync(email, "verify-email?token=");
        Assert.NotEqual(first, second);
        await Client.ProblemAsync(await client.PostAsync("/api/v1/auth/verify-email", new { token = first }),
            HttpStatusCode.Gone, "invalid_link");
        await client.PostJsonAsync("/api/v1/auth/verify-email", new { token = second });
    }

    [Fact]
    public async Task Me_lists_organizations_and_patch_changes_name_and_language()
    {
        var user = await api.VerifiedUserAsync("Marie Koné");
        var orgId = await user.CreateOrgAsync("Acme");

        var me = await user.GetJsonAsync("/api/v1/me");
        Assert.Equal(orgId, me["organizations"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("owner", me["organizations"]![0]!["role"]!.GetValue<string>());

        var res = await user.PatchAsync("/api/v1/me", new { name = "Marie K.", locale = "en" });
        var updated = await Client.ReadAsync(res, HttpStatusCode.OK);
        Assert.Equal("en", updated["user"]!["locale"]!.GetValue<string>());

        // Messages now follow the user's language, whatever Accept-Language says.
        user.Headers.AcceptLanguage.ParseAdd("fr");
        var problem = await Client.ProblemAsync(await user.PatchAsync("/api/v1/me", new { locale = "de" }),
            HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal("Some information is missing or incorrect.", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unverified_session_cannot_use_the_rest_of_the_api()
    {
        var email = ApiFixture.NewEmail();
        var client = api.Anonymous();
        await client.PostJsonAsync("/api/v1/auth/register", new { name = "Yao", email, password = "motdepasse-1" },
            HttpStatusCode.Created);
        // A session that exists while the address is still unverified (e.g. the address was reset by support).
        var token = await api.LastLinkTokenAsync(email, "verify-email?token=");
        var session = await client.PostJsonAsync("/api/v1/auth/verify-email", new { token });
        client.SignIn(session["token"]!.GetValue<string>(), session["user"]!["id"]!.GetValue<string>(), email);
        await api.Db.ExecuteAsync("FOR u IN users FILTER u.email == @email UPDATE u WITH { emailVerified: false } IN users",
            new { email });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
        await Client.ProblemAsync(await client.PostAsync("/api/v1/orgs", new { name = "Acme" }),
            HttpStatusCode.Forbidden, "email_not_verified");
    }
}
