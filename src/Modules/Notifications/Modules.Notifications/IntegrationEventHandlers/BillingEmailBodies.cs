using System.Globalization;
using System.Resources;
using FSH.Framework.Mailing;
using FSH.Modules.Notifications.Localization;

namespace FSH.Modules.Notifications.IntegrationEventHandlers;

/// <summary>
/// Builds the subject + HTML body + text/plain alternative for tenant billing emails. Plain interpolated
/// HTML (the framework has no template engine); kept here so the handlers stay thin and the copy is easy
/// to review. Every message carries both parts: HTML-only mail leaves text-only clients with nothing and
/// scores worse with spam filters. The document shell and the encoder come from
/// <see cref="HtmlEmail"/> so every module escapes the same way.
/// </summary>
/// <remarks>
/// The words come from the Notifications catalog (<c>Notifications.Billing.*</c>) in an explicit culture:
/// these handlers run from the outbox or a scan job, with no request culture. Date and amount patterns
/// live in the catalog too, so the neutral (en-US) catalog renders exactly the kit's original English.
/// </remarks>
internal static class BillingEmailBodies
{
    private static readonly ResourceManager Resources = new(typeof(NotificationsResources));

    public static (string Subject, string Body, string TextBody) NearingExpiry(string tenantName, string? planKey, DateTime validUpto, int daysRemaining, CultureInfo culture)
    {
        var subject = daysRemaining <= 1
            ? Text("Notifications.Billing.NearingExpiry.SubjectTomorrow", culture)
            : Text("Notifications.Billing.NearingExpiry.Subject", culture, Number(daysRemaining));
        var plan = planKey ?? Text("Notifications.Billing.CurrentPlan", culture);
        var date = Date(validUpto, culture);
        var action = Text("Notifications.Billing.NearingExpiry.Action", culture);
        var body = Wrap(subject, culture,
            Paragraph("Notifications.Billing.Greeting", culture, HtmlEmail.Encode(tenantName)) +
            Paragraph("Notifications.Billing.NearingExpiry.Status", culture, Strong(plan), Strong(date), Number(daysRemaining)) +
            $"<p>{HtmlEmail.Encode(action)}</p>");
        var text = PlainText(subject, culture,
            Text("Notifications.Billing.Greeting", culture, tenantName),
            Text("Notifications.Billing.NearingExpiry.Status", culture, plan, date, Number(daysRemaining)),
            action);
        return (subject, body, text);
    }

    public static (string Subject, string Body, string TextBody) EnteredGrace(string tenantName, string? planKey, DateTime validUpto, DateTime graceEnds, CultureInfo culture)
    {
        var subject = Text("Notifications.Billing.EnteredGrace.Subject", culture);
        var plan = planKey ?? Text("Notifications.Billing.CurrentPlan", culture);
        var expiredOn = Date(validUpto, culture);
        var graceEndsOn = Date(graceEnds, culture);
        var action = Text("Notifications.Billing.EnteredGrace.Action", culture);
        var body = Wrap(subject, culture,
            Paragraph("Notifications.Billing.Greeting", culture, HtmlEmail.Encode(tenantName)) +
            Paragraph("Notifications.Billing.EnteredGrace.Status", culture, Strong(plan), Strong(expiredOn), Strong(graceEndsOn)) +
            $"<p>{HtmlEmail.Encode(action)}</p>");
        var text = PlainText(subject, culture,
            Text("Notifications.Billing.Greeting", culture, tenantName),
            Text("Notifications.Billing.EnteredGrace.Status", culture, plan, expiredOn, graceEndsOn),
            action);
        return (subject, body, text);
    }

    public static (string Subject, string Body, string TextBody) Expired(string tenantName, string? planKey, DateTime validUpto, CultureInfo culture)
    {
        var subject = Text("Notifications.Billing.Expired.Subject", culture);
        var plan = planKey ?? Text("Notifications.Billing.CurrentPlan", culture);
        var expiredOn = Date(validUpto, culture);
        var action = Text("Notifications.Billing.Expired.Action", culture);
        var body = Wrap(subject, culture,
            Paragraph("Notifications.Billing.Greeting", culture, HtmlEmail.Encode(tenantName)) +
            Paragraph("Notifications.Billing.Expired.Status", culture, Strong(plan), Strong(expiredOn)) +
            $"<p>{HtmlEmail.Encode(action)}</p>");
        var text = PlainText(subject, culture,
            Text("Notifications.Billing.Greeting", culture, tenantName),
            Text("Notifications.Billing.Expired.Status", culture, plan, expiredOn),
            action);
        return (subject, body, text);
    }

    public static (string Subject, string Body, string TextBody) InvoiceIssued(string invoiceNumber, decimal amount, string currency, DateTime? dueAtUtc, CultureInfo culture)
    {
        var subject = Text("Notifications.Billing.InvoiceIssued.Subject", culture, invoiceNumber);
        var amountText = $"{amount.ToString(Text("Notifications.Billing.AmountFormat", culture), culture)} {currency}";
        var dueOn = dueAtUtc is null ? null : Date(dueAtUtc.Value, culture);
        var action = Text("Notifications.Billing.InvoiceIssued.Action", culture);
        var body = Wrap(subject, culture,
            // amountText embeds the currency, which is data rather than a literal, so it is encoded
            // for the HTML part while the text/plain twin below keeps it verbatim.
            Paragraph("Notifications.Billing.InvoiceIssued.Status", culture, Strong(invoiceNumber), Strong(amountText)) +
            (dueOn is null ? string.Empty : Paragraph("Notifications.Billing.InvoiceIssued.Due", culture, Strong(dueOn))) +
            $"<p>{HtmlEmail.Encode(action)}</p>");
        var text = PlainText(subject, culture,
            Text("Notifications.Billing.InvoiceIssued.Status", culture, invoiceNumber, amountText),
            dueOn is null ? string.Empty : Text("Notifications.Billing.InvoiceIssued.Due", culture, dueOn),
            action);
        return (subject, body, text);
    }

    private static string Date(DateTime utc, CultureInfo culture) =>
        utc.ToString(Text("Notifications.Billing.DateFormat", culture), culture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Strong(string value) => $"<strong>{HtmlEmail.Encode(value)}</strong>";

    /// <summary>
    /// A catalog sentence as an HTML paragraph. The template is encoded like any value, then filled with
    /// arguments the caller already encoded (or wrapped in <see cref="Strong"/>), so markup can only come
    /// from this file.
    /// </summary>
    private static string Paragraph(string key, CultureInfo culture, params string[] htmlArgs) =>
        $"<p>{string.Format(culture, HtmlEmail.Encode(Template(key, culture)), htmlArgs)}</p>";

    /// <summary>
    /// The billing copy inside the shared document shell. Only the automated-message footer is
    /// specific to these e-mails; the doctype, charset and card come from <see cref="HtmlEmail.Shell"/>.
    /// </summary>
    private static string Wrap(string heading, CultureInfo culture, string innerHtml) =>
        HtmlEmail.Shell(
            heading,
            "<div style=\"font-size: 15px; line-height: 1.6; color: #334155;\">" +
            innerHtml +
            $"<p style=\"margin-top:24px;color:#6b7280;font-size:12px\">{HtmlEmail.Encode(Text("Notifications.Billing.Footer", culture))}</p>" +
            "</div>");

    /// <summary>
    /// The text/plain twin of <see cref="Wrap"/>: same copy, no markup, empty paragraphs dropped so an
    /// optional line (an absent due date) does not leave a blank gap.
    /// </summary>
    private static string PlainText(string heading, CultureInfo culture, params string[] paragraphs)
    {
        var lines = new List<string> { heading };
        lines.AddRange(paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)));
        lines.Add(Text("Notifications.Billing.Footer", culture));
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string Text(string key, CultureInfo culture, params string[] args) =>
        args.Length == 0 ? Template(key, culture) : string.Format(culture, Template(key, culture), args);

    /// <summary>A catalog entry in an explicit culture. A key missing from every catalog throws instead of mailing a key name.</summary>
    private static string Template(string key, CultureInfo culture) =>
        Resources.GetString(key, culture)
        ?? throw new InvalidOperationException($"The Notifications catalog has no entry {key}.");
}
