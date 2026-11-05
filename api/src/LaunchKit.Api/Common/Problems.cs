using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace LaunchKit.Api.Common;

/// <summary>
/// A refused request (server-api.md, « Codes d'erreur »): HTTP status, stable <see cref="Code"/> and the text key of
/// the translated message.
/// </summary>
public sealed class ApiException(
    int status,
    string code,
    string textKey,
    IReadOnlyDictionary<string, object?>? args = null,
    IReadOnlyDictionary<string, string[]>? errors = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string TextKey { get; } = textKey;
    public IReadOnlyDictionary<string, object?>? Args { get; } = args;

    /// <summary>Field → text keys (already filled with their values by <see cref="Check"/>).</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public static ApiException Invalid(IReadOnlyDictionary<string, string[]> errors)
        => new(400, "invalid_request", "error.invalid_request", errors: errors);

    public static ApiException Unauthorized() => new(401, "unauthorized", "error.session_expired");
    public static ApiException LoginFailed() => new(401, "login_failed", "validation.login_failed");
    public static ApiException EmailNotVerified() => new(403, "email_not_verified", "validation.email_not_verified");

    /// <summary>Without an organization (back office), the message is the short « Accès refusé ».</summary>
    public static ApiException Forbidden(string? org = null) => org is null
        ? new(403, "forbidden", "error.forbidden.title")
        : new(403, "forbidden", "error.forbidden.text", Values(("org", org)));

    public static ApiException OwnerProtected() => new(403, "owner_protected", "error.owner_protected");

    public static ApiException WrongAccount(string email)
        => new(403, "wrong_account", "invitation.wrong_account", Values(("email", email)));

    public static ApiException NotFound() => new(404, "not_found", "error.not_found.text");
    public static ApiException EmailTaken() => new(409, "email_taken", "validation.email_taken");

    public static ApiException ProjectLimit(int max)
        => new(409, "plan_limit_reached", "projects.limit_alert", Values(("used", max), ("max", max)));

    public static ApiException MemberLimit(int max)
        => new(409, "plan_limit_reached", "members.error.limit", Values(("max", max)));

    public static ApiException AlreadyMember(string email, string org)
        => new(409, "already_member", "members.error.already_member", Values(("email", email), ("org", org)));

    public static ApiException AlreadyInvited(string email)
        => new(409, "already_invited", "members.error.already_invited", Values(("email", email)));

    public static ApiException AlreadyPro() => new(409, "already_pro", "error.already_pro");
    public static ApiException InvalidLink() => new(410, "invalid_link", "error.invalid_link");

    public static ApiException InvitationExpired(string inviter)
        => new(410, "invitation_unavailable", "invitation.expired", Values(("inviter", inviter)));

    public static ApiException InvitationUnavailable() => new(410, "invitation_unavailable", "error.invitation_unavailable");

    private static Dictionary<string, object?> Values(params (string Key, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);
}

/// <summary>Writes every error as application/problem+json with a translated <c>detail</c> (ADR-0007).</summary>
public sealed partial class ProblemHandler(Texts texts, IProblemDetailsService problems, ILogger<ProblemHandler> log)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        var http = httpContext;
        var locale = Texts.Resolve(http);
        ProblemDetails problem;
        if (exception is ApiException e)
        {
            problem = new ProblemDetails
            {
                Status = e.Status,
                Title = e.Code,
                Detail = texts.Get(locale, e.TextKey, e.Args),
            };
            problem.Extensions["code"] = e.Code;
            if (e.Errors is not null)
                problem.Extensions["errors"] = e.Errors;
        }
        else if (exception is BadHttpRequestException)
        {
            problem = new ProblemDetails
            {
                Status = 400,
                Title = "invalid_request",
                Detail = texts.Get(locale, "error.invalid_request"),
            };
            problem.Extensions["code"] = "invalid_request";
        }
        else
        {
            LogUnhandled(log, exception, http.Request.Path);
            problem = new ProblemDetails
            {
                Status = 500,
                Title = "server_error",
                Detail = texts.Get(locale, "error.server.text"),
            };
            problem.Extensions["code"] = "server_error";
        }

        http.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error on {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, PathString path);
}

/// <summary>Collects input errors field by field, then throws a single 400 <c>invalid_request</c>.</summary>
public sealed class Check(Texts texts, string locale)
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public Check Required(string field, string? value)
        => string.IsNullOrWhiteSpace(value) ? Add(field, "validation.required") : this;

    public Check Length(string field, string? value, int max)
        => value is not null && value.Trim().Length > max ? Add(field, "validation.too_long", ("max", max)) : this;

    public Check Email(string field, string? value)
        => string.IsNullOrWhiteSpace(value) ? Add(field, "validation.required")
            : !Emails.LooksValid(value.Trim()) ? Add(field, "validation.email_invalid") : this;

    public Check Password(string field, string? value, int min = 8)
        => string.IsNullOrEmpty(value) ? Add(field, "validation.required")
            : value.Length < min ? Add(field, "validation.password_short", ("min", min))
            : value.Length > 128 ? Add(field, "validation.too_long", ("max", 128)) : this;

    public Check OneOf(string field, string? value, IEnumerable<string> allowed, bool optional = false)
        => value is null && optional ? this
            : value is null || !allowed.Contains(value) ? Add(field, "error.invalid_request") : this;

    public Check Date(string field, string? value)
        => value is null || DateOnly.TryParseExact(value, "yyyy-MM-dd", out _) ? this : Add(field, "error.invalid_request");

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
            throw ApiException.Invalid(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }

    private Check Add(string field, string key, params (string Key, object? Value)[] args)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = [];
        list.Add(texts.Get(locale, key, args.ToDictionary(a => a.Key, a => a.Value)));
        return this;
    }
}
