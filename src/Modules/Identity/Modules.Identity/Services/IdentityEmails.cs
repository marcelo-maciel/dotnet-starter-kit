using System.Globalization;
using System.Resources;
using FSH.Framework.Core.Localization;
using FSH.Framework.Mailing;
using FSH.Modules.Identity.Localization;

namespace FSH.Modules.Identity.Services;

/// <summary>
/// The welcome, confirm-email and reset-password messages in the recipient's language. Each takes an
/// explicit culture: the welcome mail goes out from the outbox and the others from a Hangfire job, neither
/// with a request culture, and the ambient one is never switched for them. Texts live in the Identity
/// catalog under <c>Identity.Email.*</c>; the neutral catalog keeps the kit's English copy.
/// </summary>
internal static class IdentityEmails
{
    private static readonly ResourceManager Resources = new(typeof(IdentityResources));

    /// <summary>The user's stored locale when it is a supported tag, otherwise the deployment's default culture.</summary>
    public static CultureInfo CultureFor(string? locale, CultureInfo deploymentDefault)
    {
        ArgumentNullException.ThrowIfNull(deploymentDefault);
        return locale is not null && SupportedCultures.Tags.Contains(locale)
            ? CultureInfo.GetCultureInfo(locale)
            : deploymentDefault;
    }

    public static (string Subject, string Html, string Text) Welcome(string firstName, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(firstName);
        string subject = Text("Identity.Email.Welcome.Subject", culture);
        string greeting = Text("Identity.Email.Welcome.Greeting", culture, firstName);
        return (subject, HtmlEmail.Notice(subject, greeting), greeting);
    }

    public static (string Subject, string Html, string Text) ResetPassword(string resetUrl, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(resetUrl);
        return (
            Text("Identity.Email.Reset.Subject", culture),
            HtmlEmail.LinkAction(
                heading: Text("Identity.Email.Reset.Heading", culture),
                intro: Text("Identity.Email.Reset.Intro", culture),
                actionUrl: resetUrl,
                actionLabel: Text("Identity.Email.Reset.Action", culture),
                linkFallback: Text("Identity.Email.Reset.LinkFallback", culture)),
            Text("Identity.Email.Reset.Text", culture, resetUrl));
    }

    /// <summary>The confirm-email message; a null <c>userName</c> greets with the catalog's generic name.</summary>
    public static (string Subject, string Html, string Text) ConfirmEmail(string? userName, string confirmationUrl, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(confirmationUrl);
        string name = userName ?? Text("Identity.Email.Confirm.DefaultName", culture);
        return (
            Text("Identity.Email.Confirm.Subject", culture),
            ConfirmEmailHtml(name, confirmationUrl, culture),
            Text("Identity.Email.Confirm.Text", culture, confirmationUrl));
    }

    // The kit's own layout, kept apart from HtmlEmail.Shell on purpose (see HtmlEmail's summary): only the
    // words moved to the catalog. Catalog text is encoded like any value, so an apostrophe in a translation
    // cannot break the markup.
    private static string ConfirmEmailHtml(string userName, string confirmationUrl, CultureInfo culture)
    {
        string title = HtmlEmail.Encode(Text("Identity.Email.Confirm.Title", culture));
        string heading = HtmlEmail.Encode(Text("Identity.Email.Confirm.Subject", culture));
        string greeting = HtmlEmail.Encode(Text("Identity.Email.Confirm.Greeting", culture, userName));
        string intro = HtmlEmail.Encode(Text("Identity.Email.Confirm.Intro", culture));
        string action = HtmlEmail.Encode(Text("Identity.Email.Confirm.Action", culture));
        string fallback = HtmlEmail.Encode(Text("Identity.Email.Confirm.LinkFallback", culture));
        string ignore = HtmlEmail.Encode(Text("Identity.Email.Confirm.Ignore", culture));
        string footer = HtmlEmail.Encode(Text("Identity.Email.Confirm.Footer", culture));
        string url = HtmlEmail.Encode(confirmationUrl);

        return $"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>{title}</title>
            </head>
            <body style="margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif; background-color: #f8fafc;">
                <table role="presentation" style="width: 100%; border-collapse: collapse;">
                    <tr>
                        <td align="center" style="padding: 40px 0;">
                            <table role="presentation" style="width: 100%; max-width: 600px; border-collapse: collapse; background-color: #ffffff; border-radius: 8px; box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);">
                                <tr>
                                    <td style="padding: 40px 40px 30px 40px; text-align: center; background-color: #2563eb; border-radius: 8px 8px 0 0;">
                                        <h1 style="margin: 0; color: #ffffff; font-size: 24px; font-weight: 600;">{heading}</h1>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="padding: 40px;">
                                        <p style="margin: 0 0 20px 0; color: #334155; font-size: 16px; line-height: 1.6;">
                                            {greeting}
                                        </p>
                                        <p style="margin: 0 0 20px 0; color: #334155; font-size: 16px; line-height: 1.6;">
                                            {intro}
                                        </p>
                                        <table role="presentation" style="width: 100%; border-collapse: collapse;">
                                            <tr>
                                                <td align="center" style="padding: 30px 0;">
                                                    <a href="{url}" style="display: inline-block; padding: 14px 32px; background-color: #2563eb; color: #ffffff; text-decoration: none; font-size: 16px; font-weight: 600; border-radius: 6px;">
                                                        {action}
                                                    </a>
                                                </td>
                                            </tr>
                                        </table>
                                        <p style="margin: 0 0 20px 0; color: #64748b; font-size: 14px; line-height: 1.6;">
                                            {fallback}
                                        </p>
                                        <p style="margin: 0 0 20px 0; color: #2563eb; font-size: 14px; line-height: 1.6; word-break: break-all;">
                                            {url}
                                        </p>
                                        <p style="margin: 30px 0 0 0; color: #64748b; font-size: 14px; line-height: 1.6;">
                                            {ignore}
                                        </p>
                                    </td>
                                </tr>
                                <tr>
                                    <td style="padding: 20px 40px; background-color: #f1f5f9; border-radius: 0 0 8px 8px; text-align: center;">
                                        <p style="margin: 0; color: #94a3b8; font-size: 12px;">
                                            {footer}
                                        </p>
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>
            """;
    }

    /// <summary>An Identity catalog entry in an explicit culture. A key missing from every catalog throws instead of mailing a key name.</summary>
    private static string Text(string key, CultureInfo culture, params string[] args)
    {
        string template = Resources.GetString(key, culture)
            ?? throw new InvalidOperationException($"The Identity catalog has no entry {key}.");
        return args.Length == 0 ? template : string.Format(culture, template, args);
    }
}
