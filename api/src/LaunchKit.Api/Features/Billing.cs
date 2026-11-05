using LaunchKit.Api.Auth;
using LaunchKit.Api.Common;
using LaunchKit.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace LaunchKit.Api.Features;

/// <summary>Abonnement (server-api.md; W09, M07): simulated payment of ADR-0006, handled by the Rust worker.</summary>
public static class Billing
{
    public sealed record CheckoutRequest(string? Plan);

    public static void Map(RouteGroupBuilder api)
    {
        var billing = api.MapGroup("/orgs/{orgId}/billing").WithTags("Abonnement");
        billing.MapGet("/", Get);
        billing.MapPost("/checkout", Checkout);
    }

    private sealed record BillingRow(Organizations.OrgRow Org, bool PendingPayment);

    private static async Task<object> Get(string orgId, HttpContext http, ArangoClient db, OrgAccess access,
        IOptions<PlansOptions> plans)
    {
        await access.RequireAsync(http.Verified(), orgId, OrgRole.Admin);
        var row = await db.FirstAsync<BillingRow>($$"""
            LET o = DOCUMENT("organizations", @org)
            RETURN {
              org: {{Organizations.OrgFields}},
              pendingPayment: LENGTH(FOR p IN payments FILTER p.orgId == @org AND p.status == "pending" RETURN 1) > 0
            }
            """, new { org = orgId });
        var org = Organizations.View(row!.Org, plans.Value);
        return new
        {
            plan = org.Plan,
            usage = org.Usage,
            limits = org.Limits,
            prices = new { pro = new { amount = plans.Value.ProPriceCents, currency = plans.Value.Currency } },
            pendingPayment = row.PendingPayment,
        };
    }

    /// <summary>
    /// Creates the simulated payment and its webhook job, or returns the payment already pending. Touching the
    /// organization serialises concurrent checkouts (write-write conflict, then retry).
    /// </summary>
    private static async Task<IResult> Checkout(string orgId, CheckoutRequest body, HttpContext http, ArangoClient db,
        OrgAccess access, Texts texts, IOptions<PlansOptions> plans, TimeProvider clock)
    {
        var user = http.Verified();
        await access.RequireAsync(user, orgId, OrgRole.Admin);
        new Check(texts, user.Locale).OneOf("plan", body.Plan, ["pro"]).ThrowIfInvalid();
        var now = Iso.Now(clock);
        var paymentId = await ArangoClient.RetryOnConflictAsync(() => db.FirstAsync<string>("""
            LET o = DOCUMENT("organizations", @org)
            FILTER o.plan != "pro"
            LET existing = FIRST(FOR p IN payments FILTER p.orgId == @org AND p.status == "pending" RETURN p._key)
            LET created = (
              FILTER existing == null
              INSERT { orgId: @org, provider: "simulated", plan: "pro", amount: @amount, currency: @currency,
                       status: "pending", createdBy: @user, createdAt: @now, completedAt: null }
              INTO payments RETURN NEW._key)
            LET job = (
              FILTER existing == null
              INSERT MERGE(@job, { payload: { paymentId: created[0] } }) INTO jobs)
            UPDATE o WITH { lastCheckoutAt: @now } IN organizations
            RETURN existing != null ? existing : created[0]
            """, new
        {
            org = orgId,
            amount = plans.Value.ProPriceCents,
            currency = plans.Value.Currency,
            user = user.Id,
            now,
            job = Jobs.PaymentWebhook("", now),
        }));
        return paymentId is null ? throw ApiException.AlreadyPro() : Results.Accepted(value: new { paymentId });
    }
}
