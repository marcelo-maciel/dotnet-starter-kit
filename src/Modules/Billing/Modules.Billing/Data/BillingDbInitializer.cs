using FSH.Framework.Persistence;
using FSH.Modules.Billing.Contracts;
using FSH.Modules.Billing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Billing.Data;

public sealed class BillingDbInitializer(
    BillingDbContext dbContext,
    ILogger<BillingDbInitializer> logger) : IDbInitializer
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if ((await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[Billing] applied migrations");
        }
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // Plans are a global catalogue (IGlobalEntity); seed defaults once. "free" backs the trial
        // fallback; keys align with QuotaOptions plan keys so quota limits resolve.
        if (await dbContext.Plans.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // PAF (ADR 0008): names in pt-BR and prices in BRL; the keys stay, they are what quota and trial resolve.
        dbContext.Plans.Add(BillingPlan.Create("free", "Gratuito", "BRL", 0m, interval: PlanInterval.Monthly));
        dbContext.Plans.Add(BillingPlan.Create("pro", "Pro", "BRL", 29m, interval: PlanInterval.Monthly));
        dbContext.Plans.Add(BillingPlan.Create("pro-annual", "Pro anual", "BRL", 29m,
            interval: PlanInterval.Yearly, annualPrice: 290m));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("[Billing] seeded default plans (free, pro, pro-annual)");
    }
}
