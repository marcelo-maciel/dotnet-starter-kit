using FSH.Modules.Billing.Contracts;
using FSH.Modules.Billing.Domain;

namespace Billing.Tests.Domain;

/// <summary>
/// The API container runs with <c>TZ=America/Sao_Paulo</c>. System.Text.Json reads a date sent with a
/// non-Z offset as <see cref="DateTimeKind.Local"/>, already moved to that zone; relabelling it as UTC
/// would store the Brasília wall clock as UTC, three hours early. The Local value here is built from an
/// explicit offset, so the expected instant holds in any machine zone; the relabelling it guards against
/// is only wrong, and only caught, where the machine zone is not UTC.
/// </summary>
public sealed class UtcInstantTests
{
    private static readonly DateTime ExpectedUtc = new(2026, 5, 1, 3, 0, 0, DateTimeKind.Utc);

    // What System.Text.Json yields for "2026-05-01T00:00:00-03:00": Kind=Local, in the machine's zone.
    private static DateTime ParsedWithOffset() =>
        new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.FromHours(-3)).LocalDateTime;

    [OffUtcFact]
    public void From_Should_ConvertALocalValue_ToTheSameUtcInstant()
    {
        var local = ParsedWithOffset();
        local.Kind.ShouldBe(DateTimeKind.Local);

        var utc = UtcInstant.From(local);

        utc.ShouldBe(ExpectedUtc);
        utc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void From_Should_ReadAnUnspecifiedValue_AsUtc()
    {
        var utc = UtcInstant.From(new DateTime(2026, 5, 1, 3, 0, 0, DateTimeKind.Unspecified));

        utc.ShouldBe(ExpectedUtc);
        utc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [OffUtcFact]
    public void Issue_Should_StoreTheDueInstant_When_ItArrivesAsLocal()
    {
        var invoice = Invoice.CreateDraft("acme", "INV-1", 2026, 5, "BRL");

        invoice.Issue(ParsedWithOffset());

        invoice.DueAtUtc.ShouldBe(ExpectedUtc);
    }

    [OffUtcFact]
    public void Subscription_Should_StoreTheTerm_When_ItArrivesAsLocal()
    {
        var subscription = Subscription.Create("acme", Guid.NewGuid(), ParsedWithOffset(), ParsedWithOffset().AddMonths(1));

        subscription.StartUtc.ShouldBe(ExpectedUtc);
        subscription.EndUtc.ShouldBe(ExpectedUtc.AddMonths(1));
    }

    [OffUtcFact]
    public void SubscriptionInvoice_Should_StoreThePeriod_When_ItArrivesAsLocal()
    {
        var invoice = Invoice.CreateDraft("acme", "SUB-1", 2026, 5, "BRL",
            InvoicePurpose.Subscription, ParsedWithOffset(), ParsedWithOffset().AddMonths(1));

        invoice.PeriodStartUtc.ShouldBe(ExpectedUtc);
        invoice.PeriodEndUtc.ShouldBe(ExpectedUtc.AddMonths(1));
    }
}
