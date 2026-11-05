using System.Text.Json;
using LaunchKit.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace LaunchKit.Api.Tests;

public sealed class IsoTests
{
    [Fact]
    public void Dates_are_utc_with_milliseconds_and_sort_as_text()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 9, 30, 0, 5, TimeSpan.FromHours(2)));

        Assert.Equal("2026-11-04T07:30:00.005Z", Iso.Now(clock));
        Assert.True(string.CompareOrdinal(Iso.Of(clock.GetUtcNow()), Iso.Of(clock.GetUtcNow().AddMilliseconds(1))) < 0);
    }
}

public sealed class EmailsTests
{
    [Theory]
    [InlineData("marie@demo.test", true)]
    [InlineData("jean.diallo@acme.co.ci", true)]
    [InlineData("pas-une-adresse", false)]
    [InlineData("@demo.test", false)]
    [InlineData("marie@demo", false)]
    [InlineData("marie@demo.", false)]
    [InlineData("ma rie@demo.test", false)]
    [InlineData("a@b@demo.test", false)]
    public void Looks_valid(string email, bool expected) => Assert.Equal(expected, Emails.LooksValid(email));

    [Fact]
    public void Normalize_trims_and_lowercases() => Assert.Equal("marie@demo.test", Emails.Normalize("  Marie@Demo.TEST "));

    [Theory]
    [InlineData("Marie Koné", "Marie")]
    [InlineData("  Awa  ", "Awa")]
    [InlineData("Jean-Paul Diallo", "Jean-Paul")]
    public void First_name_is_the_first_word(string name, string expected) => Assert.Equal(expected, Emails.FirstName(name));
}

public sealed class TokensTests
{
    [Fact]
    public void Tokens_are_random_url_safe_and_hashed_to_sha256_hex()
    {
        var a = Tokens.New();
        var b = Tokens.New();

        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length); // 32 bytes in base64url, no padding
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.Equal(64, Tokens.Hash(a).Length);
        Assert.Equal(Tokens.Hash(a), Tokens.Hash(a));
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", Tokens.Hash("hello"));
    }
}

public sealed class RolesTests
{
    [Fact]
    public void Roles_are_ordered_member_admin_owner()
    {
        Assert.True(OrgRole.Owner > OrgRole.Admin);
        Assert.True(OrgRole.Admin > OrgRole.Member);
    }

    [Theory]
    [InlineData("owner", OrgRole.Owner)]
    [InlineData("admin", OrgRole.Admin)]
    [InlineData("member", OrgRole.Member)]
    public void Codes_round_trip(string code, OrgRole role)
    {
        Assert.Equal(role, Roles.Parse(code));
        Assert.Equal(code, Roles.Code(role));
    }

    [Fact]
    public void Unknown_code_throws_and_owner_is_never_assignable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Roles.Parse("king"));
        Assert.DoesNotContain("owner", Roles.Assignable);
    }
}

public sealed class OptionsTests
{
    [Fact]
    public void Plans_resolve_limits_and_links_are_built_on_the_web_url()
    {
        var plans = new PlansOptions { Free = new PlanLimits { MaxProjects = 3, MaxMembers = 3 }, Pro = new PlanLimits() };

        Assert.Equal(3, plans.For("free").MaxProjects);
        Assert.Null(plans.For("pro").MaxMembers);
        Assert.Equal("https://app.demo.test/verify-email?token=x",
            new AppOptions { WebBaseUrl = "https://app.demo.test/" }.Link("/verify-email?token=x"));
    }
}

public sealed class JobsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Jobs_follow_the_worker_contract()
    {
        var json = JsonSerializer.SerializeToElement(
            Jobs.SendEmail("verify_email", "marie@demo.test", "fr", new { firstName = "Marie" }, "2026-11-04T00:00:00.000Z"), Web);

        Assert.Equal("send_email", json.GetProperty("type").GetString());
        Assert.Equal("pending", json.GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("attempts").GetInt32());
        Assert.Equal(5, json.GetProperty("maxAttempts").GetInt32());
        Assert.Equal("Marie", json.GetProperty("payload").GetProperty("params").GetProperty("firstName").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lockedAt").ValueKind);
        var webhook = JsonSerializer.SerializeToElement(Jobs.PaymentWebhook("42", "t"), Web);
        Assert.Equal("42", webhook.GetProperty("payload").GetProperty("paymentId").GetString());
    }
}

public sealed class TextsTests
{
    internal static readonly Texts Sample = new(new Dictionary<string, Dictionary<string, string>>
    {
        ["fr"] = new()
        {
            ["validation.required"] = "Ce champ est obligatoire.",
            ["validation.password_short"] = "Le mot de passe doit contenir au moins {min} caractères.",
            ["validation.too_long"] = "{max} caractères maximum.",
            ["validation.email_invalid"] = "Adresse invalide.",
            ["error.invalid_request"] = "Informations incorrectes.",
            ["members.error.already_member"] = "{email} fait déjà partie de l’organisation {org}.",
        },
        ["en"] = new()
        {
            ["validation.required"] = "This field is required.",
            ["error.invalid_request"] = "Some information is missing or incorrect.",
        },
    });

    [Fact]
    public void Placeholders_are_filled_and_unknown_ones_are_kept()
    {
        var text = Sample.Get("fr", "members.error.already_member",
            new Dictionary<string, object?> { ["email"] = "jean@demo.test", ["org"] = "Acme" });

        Assert.Equal("jean@demo.test fait déjà partie de l’organisation Acme.", text);
        Assert.Equal("{max} caractères maximum.", Sample.Get("fr", "validation.too_long", new Dictionary<string, object?>()));
    }

    [Fact]
    public void Unknown_keys_and_locales_fall_back()
    {
        Assert.Equal("missing.key", Sample.Get("fr", "missing.key"));
        Assert.Equal("Ce champ est obligatoire.", Sample.Get("de", "validation.required"));
        Assert.True(Sample.Has("en", "validation.required"));
        Assert.False(Sample.Has("en", "validation.too_long"));
        Assert.Equal("fr", Texts.Normalize("de"));
        Assert.Equal("en", Texts.Normalize("en"));
    }

    [Theory]
    [InlineData("en-US,en;q=0.9,fr;q=0.8", "en")]
    [InlineData("de-DE,fr;q=0.5", "fr")]
    [InlineData("es, en;q=0.4", "en")]
    [InlineData("", "fr")]
    public void Accept_language_picks_the_best_supported_locale(string header, string expected)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.AcceptLanguage = header;

        Assert.Equal(expected, Texts.Resolve(http));
    }

    [Fact]
    public void Shipped_texts_cover_every_api_message()
    {
        var texts = Texts.Load(Path.Combine(AppContext.BaseDirectory, "i18n"));
        string[] keys =
        [
            "error.invalid_request", "error.session_expired", "validation.login_failed", "validation.email_not_verified",
            "error.forbidden.text", "error.forbidden.title", "error.owner_protected", "invitation.wrong_account",
            "error.not_found.text", "validation.email_taken", "projects.limit_alert", "members.error.limit",
            "members.error.already_member", "members.error.already_invited", "error.already_pro", "error.invalid_link",
            "invitation.expired", "error.invitation_unavailable", "error.server.text", "validation.required",
            "validation.too_long", "validation.email_invalid", "validation.password_short",
        ];
        foreach (var locale in Texts.Locales)
            Assert.All(keys, k => Assert.True(texts.Has(locale, k), $"{locale}: {k}"));
    }
}

public sealed class CheckTests
{
    [Fact]
    public void Valid_input_passes()
    {
        new Check(TextsTests.Sample, "fr")
            .Required("name", "Acme").Length("name", "Acme", 60)
            .Email("email", "marie@demo.test")
            .Password("password", "motdepasse-1")
            .OneOf("role", "member", ["admin", "member"])
            .OneOf("locale", null, ["fr", "en"], optional: true)
            .Date("dueDate", "2026-11-28").Date("other", null)
            .ThrowIfInvalid();
    }

    [Fact]
    public void Every_error_is_collected_and_translated()
    {
        var check = new Check(TextsTests.Sample, "fr")
            .Required("name", "  ")
            .Length("description", new string('x', 11), 10)
            .Email("email", "nope")
            .Email("other", null)
            .Password("password", "court")
            .Password("long", new string('x', 129))
            .Password("missing", null)
            .OneOf("role", "owner", ["admin", "member"])
            .Date("dueDate", "28/11/2026");

        var e = Assert.Throws<ApiException>(check.ThrowIfInvalid);

        Assert.Equal(400, e.Status);
        Assert.Equal("invalid_request", e.Code);
        Assert.Equal(["Ce champ est obligatoire."], e.Errors!["name"]);
        Assert.Equal(["10 caractères maximum."], e.Errors["description"]);
        Assert.Equal(["Adresse invalide."], e.Errors["email"]);
        Assert.Equal(["Ce champ est obligatoire."], e.Errors["other"]);
        Assert.Equal(["Le mot de passe doit contenir au moins 8 caractères."], e.Errors["password"]);
        Assert.Equal(["128 caractères maximum."], e.Errors["long"]);
        Assert.Equal(["Ce champ est obligatoire."], e.Errors["missing"]);
        Assert.Equal(["Informations incorrectes."], e.Errors["role"]);
        Assert.Equal(["Informations incorrectes."], e.Errors["dueDate"]);
    }
}
