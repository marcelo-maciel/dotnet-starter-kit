using System.Globalization;
using FSH.Framework.Mailing;
using FSH.Modules.Notifications.IntegrationEventHandlers;

namespace Notifications.Tests.IntegrationEventHandlers;

/// <summary>
/// Every tenant billing email is built here, and the pair it returns is the whole contract: a
/// <c>Body</c> the provider sends as text/html and a <c>TextBody</c> the provider sends as the
/// text/plain alternative. Passing the markup as both is what this PR fixes downstream, and the only
/// thing that keeps the plain part honest is that it is written separately - so a template that
/// forgot it, or that reused the HTML, has to be caught here.
/// </summary>
public sealed class BillingEmailBodiesTests
{
    private static readonly DateTime ValidUpto = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static TheoryData<string, (string Subject, string Body, string TextBody)> AllTemplates() => new()
    {
        { "NearingExpiry", BillingEmailBodies.NearingExpiry("Acme", "pro", ValidUpto, daysRemaining: 3, En) },
        { "EnteredGrace", BillingEmailBodies.EnteredGrace("Acme", "pro", ValidUpto, ValidUpto.AddDays(7), En) },
        { "Expired", BillingEmailBodies.Expired("Acme", "pro", ValidUpto, En) },
        { "InvoiceIssued", BillingEmailBodies.InvoiceIssued("INV-2026-0001", 129.50m, "USD", ValidUpto, En) },
    };

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Template_Should_ReturnAnHtmlBodyAndAPlainTextTwin(
        string name,
        (string Subject, string Body, string TextBody) built)
    {
        // Assert
        built.Subject.ShouldNotBeNullOrWhiteSpace(name);

        built.Body.ShouldContain("<p", Case.Sensitive, name);
        built.Body.ShouldContain(built.Subject, Case.Sensitive, name);

        // The plain part is not allowed to be the markup, nor to be empty because someone passed the
        // HTML twice and trimmed it. No angle bracket survives the text twin.
        built.TextBody.ShouldNotBeNullOrWhiteSpace(name);
        built.TextBody.ShouldNotContain("<", Case.Sensitive, name);
        built.TextBody.ShouldContain(built.Subject, Case.Sensitive, name);
        built.TextBody.ShouldContain("This is an automated message.", Case.Sensitive, name);
    }

    [Fact]
    public void NearingExpiry_Should_EscapeTheTenantNameInHtml_ButNotInPlainText()
    {
        // Arrange — a tenant name is operator-supplied and lands inside the markup.
        const string Hostile = "<script>alert(1)</script> & Co";

        // Act
        var (_, body, text) = BillingEmailBodies.NearingExpiry(Hostile, "pro", ValidUpto, daysRemaining: 3, En);

        // Assert — escaped where it is parsed as markup…
        body.ShouldNotContain("<script>");
        body.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt; &amp; Co");

        // …and left alone where it is not: entities in a text/plain part are read literally, so
        // escaping there would show the reader "&amp;" instead of "&".
        text.ShouldContain(Hostile);
    }

    [Fact]
    public void InvoiceIssued_Should_DropTheDueLine_When_ThereIsNoDueDate()
    {
        // Act
        var (_, body, text) = BillingEmailBodies.InvoiceIssued("INV-1", 10m, "USD", dueAtUtc: null, En);

        // Assert — no orphan label, and no blank paragraph where the optional line would have been.
        body.ShouldNotContain("Due by");
        text.ShouldNotContain("Due by");
        text.ShouldNotContain($"{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}");
    }

    [Fact]
    public void NearingExpiry_Should_SayTomorrow_When_OnlyOneDayIsLeft()
    {
        // The singular arm reads as copy, not as a count, so it is the one a template change breaks
        // silently: "expires in 1 days" still renders.
        var (subject, body, text) = BillingEmailBodies.NearingExpiry("Acme", "pro", ValidUpto, daysRemaining: 1, En);

        subject.ShouldBe("Your subscription expires tomorrow");
        body.ShouldContain(subject);
        text.ShouldContain(subject);
    }

    [Fact]
    public void InvoiceIssued_Should_RenderTheKitsEnglishVerbatim_In_EnUS()
    {
        // The copy moved into the catalog; en-US must come out byte for byte as the literals it replaced,
        // date pattern and amount format included.
        var (subject, body, text) = BillingEmailBodies.InvoiceIssued("INV-2026-0001", 129.50m, "USD", ValidUpto, En);

        subject.ShouldBe("Invoice INV-2026-0001 issued");
        body.ShouldBe(HtmlEmail.Shell(
            "Invoice INV-2026-0001 issued",
            "<div style=\"font-size: 15px; line-height: 1.6; color: #334155;\">" +
            "<p>A new invoice <strong>INV-2026-0001</strong> for <strong>129.50 USD</strong> has been issued.</p>" +
            "<p>Due by <strong>May 1, 2026</strong>.</p>" +
            "<p>You can view and download this invoice from your dashboard.</p>" +
            "<p style=\"margin-top:24px;color:#6b7280;font-size:12px\">This is an automated message.</p>" +
            "</div>"));
        text.ShouldBe(string.Join(
            Environment.NewLine + Environment.NewLine,
            "Invoice INV-2026-0001 issued",
            "A new invoice INV-2026-0001 for 129.50 USD has been issued.",
            "Due by May 1, 2026.",
            "You can view and download this invoice from your dashboard.",
            "This is an automated message."));
    }

    [Fact]
    public void EveryTemplate_Should_KeepItsEnglishPlainText_In_EnUS()
    {
        var nl2 = Environment.NewLine + Environment.NewLine;
        BillingEmailBodies.NearingExpiry("Acme", null, ValidUpto, daysRemaining: 3, En).TextBody.ShouldBe(string.Join(nl2,
            "Your subscription expires in 3 days",
            "Hi Acme,",
            "Your current subscription is valid until May 1, 2026 (3 day(s) remaining).",
            "Please contact your account operator to renew and avoid any interruption to your service.",
            "This is an automated message."));
        BillingEmailBodies.EnteredGrace("Acme", "pro", ValidUpto, ValidUpto.AddDays(7), En).TextBody.ShouldBe(string.Join(nl2,
            "Your subscription has lapsed — grace period active",
            "Hi Acme,",
            "Your pro subscription expired on May 1, 2026. Your service continues during a grace period that ends on May 8, 2026.",
            "Please renew before the grace period ends to keep your access uninterrupted.",
            "This is an automated message."));
        BillingEmailBodies.Expired("Acme", "pro", ValidUpto, En).TextBody.ShouldBe(string.Join(nl2,
            "Your subscription has expired",
            "Hi Acme,",
            "Your pro subscription expired on May 1, 2026 and the grace period has ended, so access is now suspended.",
            "Contact your account operator to renew and restore access.",
            "This is an automated message."));
    }

    public static TheoryData<string, (string Subject, string Body, string TextBody)> AllTemplatesInPortuguese() => new()
    {
        { "NearingExpiry", BillingEmailBodies.NearingExpiry("Acme", "pro", ValidUpto, daysRemaining: 3, Pt) },
        { "EnteredGrace", BillingEmailBodies.EnteredGrace("Acme", "pro", ValidUpto, ValidUpto.AddDays(7), Pt) },
        { "Expired", BillingEmailBodies.Expired("Acme", "pro", ValidUpto, Pt) },
        { "InvoiceIssued", BillingEmailBodies.InvoiceIssued("INV-2026-0001", 129.50m, "USD", ValidUpto, Pt) },
    };

    [Theory]
    [MemberData(nameof(AllTemplatesInPortuguese))]
    public void Template_Should_BeWrittenInPortuguese_In_PtBR(
        string name,
        (string Subject, string Body, string TextBody) built)
    {
        // Same contract as the English twin, and no English sentence left behind in either part.
        built.Body.ShouldContain(HtmlEmail.Encode(built.Subject), Case.Sensitive, name);
        built.TextBody.ShouldContain(built.Subject, Case.Sensitive, name);
        built.TextBody.ShouldNotContain("<", Case.Sensitive, name);
        built.TextBody.ShouldContain("Esta é uma mensagem automática.", Case.Sensitive, name);
        foreach (var english in new[] { "Your ", "Hi ", "This is an automated message", "invoice", "May" })
        {
            built.Body.ShouldNotContain(english, Case.Sensitive, name);
            built.TextBody.ShouldNotContain(english, Case.Sensitive, name);
        }
    }

    [Fact]
    public void NearingExpiry_Should_UseBrazilianDatesAndWords_In_PtBR()
    {
        var (subject, _, text) = BillingEmailBodies.NearingExpiry("Acme", null, ValidUpto, daysRemaining: 3, Pt);

        subject.ShouldBe("Sua assinatura vence em 3 dias");
        text.ShouldContain("Sua assinatura atual é válida até 1 de maio de 2026 (3 dia(s) restante(s)).");
        BillingEmailBodies.NearingExpiry("Acme", "pro", ValidUpto, daysRemaining: 1, Pt).Subject.ShouldBe("Sua assinatura vence amanhã");
    }

    [Fact]
    public void InvoiceIssued_Should_UseTheBrazilianDecimalComma_In_PtBR()
    {
        var (subject, _, text) = BillingEmailBodies.InvoiceIssued("INV-1", 1234.5m, "BRL", ValidUpto, Pt);

        subject.ShouldBe("Fatura INV-1 emitida");
        text.ShouldContain("Uma nova fatura INV-1, no valor de 1.234,50 BRL, foi emitida.");
        text.ShouldContain("Vencimento em 1 de maio de 2026.");
    }
}
