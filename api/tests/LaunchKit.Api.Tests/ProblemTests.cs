using System.Text.Json;
using LaunchKit.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LaunchKit.Api.Tests;

/// <summary>Error contract of server-api.md (« Codes d'erreur »), ADR-0007.</summary>
public sealed class ProblemTests
{
    public static TheoryData<ApiException, int, string, string> Cases => new()
    {
        { ApiException.Unauthorized(), 401, "unauthorized", "error.session_expired" },
        { ApiException.LoginFailed(), 401, "login_failed", "validation.login_failed" },
        { ApiException.EmailNotVerified(), 403, "email_not_verified", "validation.email_not_verified" },
        { ApiException.Forbidden(), 403, "forbidden", "error.forbidden.title" },
        { ApiException.Forbidden("Acme"), 403, "forbidden", "error.forbidden.text" },
        { ApiException.OwnerProtected(), 403, "owner_protected", "error.owner_protected" },
        { ApiException.WrongAccount("x@demo.test"), 403, "wrong_account", "invitation.wrong_account" },
        { ApiException.NotFound(), 404, "not_found", "error.not_found.text" },
        { ApiException.EmailTaken(), 409, "email_taken", "validation.email_taken" },
        { ApiException.ProjectLimit(3), 409, "plan_limit_reached", "projects.limit_alert" },
        { ApiException.MemberLimit(3), 409, "plan_limit_reached", "members.error.limit" },
        { ApiException.AlreadyMember("x@demo.test", "Acme"), 409, "already_member", "members.error.already_member" },
        { ApiException.AlreadyInvited("x@demo.test"), 409, "already_invited", "members.error.already_invited" },
        { ApiException.AlreadyPro(), 409, "already_pro", "error.already_pro" },
        { ApiException.InvalidLink(), 410, "invalid_link", "error.invalid_link" },
        { ApiException.InvitationExpired("Marie"), 410, "invitation_unavailable", "invitation.expired" },
        { ApiException.InvitationUnavailable(), 410, "invitation_unavailable", "error.invitation_unavailable" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_error_has_its_status_code_and_text(ApiException e, int status, string code, string key)
    {
        Assert.Equal(status, e.Status);
        Assert.Equal(code, e.Code);
        Assert.Equal(key, e.TextKey);
    }

    private static async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception, string? acceptLanguage = null)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        if (acceptLanguage is not null)
            http.Request.Headers.AcceptLanguage = acceptLanguage;
        var texts = Texts.Load(Path.Combine(AppContext.BaseDirectory, "i18n"));
        var handler = new ProblemHandler(texts, services.GetRequiredService<Microsoft.AspNetCore.Http.IProblemDetailsService>(),
            NullLogger<ProblemHandler>.Instance);

        Assert.True(await handler.TryHandleAsync(http, exception, CancellationToken.None));

        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, JsonDocument.Parse(http.Response.Body).RootElement);
    }

    [Fact]
    public async Task An_api_error_becomes_a_translated_problem()
    {
        var (status, body) = await HandleAsync(ApiException.ProjectLimit(3), "en");

        Assert.Equal(409, status);
        Assert.Equal("plan_limit_reached", body.GetProperty("code").GetString());
        Assert.Equal("You’ve reached the Free plan limit: 3 of 3 projects. Upgrade to Pro to create more.",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Field_errors_are_kept()
    {
        var errors = new Dictionary<string, string[]> { ["email"] = ["Adresse invalide."] };

        var (status, body) = await HandleAsync(ApiException.Invalid(errors));

        Assert.Equal(400, status);
        Assert.Equal("Adresse invalide.", body.GetProperty("errors").GetProperty("email")[0].GetString());
    }

    [Fact]
    public async Task Bad_requests_and_unexpected_errors_never_leak_details()
    {
        var (badStatus, bad) = await HandleAsync(new BadHttpRequestException("broken json"));
        var (status, body) = await HandleAsync(new InvalidOperationException("secret connection string"), "fr");

        Assert.Equal(400, badStatus);
        Assert.Equal("invalid_request", bad.GetProperty("code").GetString());
        Assert.Equal(500, status);
        Assert.Equal("server_error", body.GetProperty("code").GetString());
        Assert.DoesNotContain("secret", body.ToString(), StringComparison.Ordinal);
    }
}
