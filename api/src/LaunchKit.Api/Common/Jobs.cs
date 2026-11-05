namespace LaunchKit.Api.Common;

/// <summary>Documents of the <c>jobs</c> queue read by the Rust worker (server-taches.md, ADR-0005).</summary>
public static class Jobs
{
    public const int MaxAttempts = 5;

    public static object SendEmail(string template, string to, string locale, object parameters, string now)
        => New("send_email", new { template, to, locale, @params = parameters }, now);

    public static object PaymentWebhook(string paymentId, string now) => New("payment_webhook", new { paymentId }, now);

    private static object New(string type, object payload, string now) => new
    {
        type,
        payload,
        status = "pending",
        attempts = 0,
        maxAttempts = MaxAttempts,
        runAt = now,
        lockedAt = (string?)null,
        lastError = (string?)null,
        createdAt = now,
        finishedAt = (string?)null,
    };
}
