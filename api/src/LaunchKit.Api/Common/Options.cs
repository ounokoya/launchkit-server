namespace LaunchKit.Api.Common;

/// <summary>Limits and price of a plan (ADR-0003). <c>null</c> means unlimited.</summary>
public sealed class PlanLimits
{
    public int? MaxProjects { get; set; }
    public int? MaxMembers { get; set; }
}

public sealed class PlansOptions
{
    public PlanLimits Free { get; set; } = new();
    public PlanLimits Pro { get; set; } = new();
    public int ProPriceCents { get; set; } = 2900;
    public string Currency { get; set; } = "EUR";

    public PlanLimits For(string plan) => plan == "pro" ? Pro : Free;
}

public sealed class AppOptions
{
    /// <summary>Public URL of the web app: links in e-mails point there.</summary>
    public string WebBaseUrl { get; set; } = "http://localhost:5173";
    public int SessionLifetimeDays { get; set; } = 14;
    public int VerifyEmailHours { get; set; } = 24;
    public int InvitationDays { get; set; } = 7;

    public string Link(string path) => WebBaseUrl.TrimEnd('/') + path;
}
