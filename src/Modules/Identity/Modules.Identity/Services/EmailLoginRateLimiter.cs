using System.Net;
using System.Threading.RateLimiting;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Web.RateLimiting;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Identity.Services;

/// <summary>
/// Limits of the routes that take an e-mail without a tenant (login and forgot password), section
/// <c>EmailLoginRateLimit</c>. All windows are always on, whatever <c>RateLimitingOptions:Enabled</c> says:
/// <list type="bullet">
/// <item>login per e-mail, tight: 5 attempts a minute. Nobody signs in five times a minute on purpose; Identity's
/// lockout (5 wrong passwords, 15 minutes) still guards the account itself.</item>
/// <item>per IP, loose: 100 attempts a minute, for each route. The guichês of one unit leave through one NAT and
/// all sign in when the turn opens: 100 covers some 50 stations with a mistyped password each in the same minute,
/// where the kit's <c>auth</c> policy (10 a minute per IP) would lock most of them out.</item>
/// <item>forgot password per e-mail: 3 reset e-mails an hour, so nobody floods a mailbox. Over it the request
/// still gets the usual 200 and sends nothing, so the limit tells nothing about the address.</item>
/// </list>
/// </summary>
public sealed class EmailLoginRateLimitOptions
{
    public FixedWindowPolicyOptions PerEmail { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60, QueueLimit = 0 };

    public FixedWindowPolicyOptions PerIp { get; set; } = new() { PermitLimit = 100, WindowSeconds = 60, QueueLimit = 0 };

    public FixedWindowPolicyOptions ForgotPerEmail { get; set; } = new() { PermitLimit = 3, WindowSeconds = 3600, QueueLimit = 0 };
}

/// <summary>
/// The windows of <see cref="EmailLoginRateLimitOptions"/>. Checked in the handlers because the e-mail is in the
/// body, out of reach of the ASP.NET rate-limiting middleware's partitioner.
/// </summary>
// ponytail: in-memory, per API instance; two instances double the effective limit. Upgrade path: a Valkey
// fixed-window counter keyed the same way.
public sealed class EmailLoginRateLimiter : IDisposable
{
    public const string Mensagem = "Muitas tentativas. Aguarde um minuto e tente de novo.";

    private readonly PartitionedRateLimiter<string> _loginPerEmail;
    private readonly PartitionedRateLimiter<string> _perIp;
    private readonly PartitionedRateLimiter<string> _forgotPerEmail;

    public EmailLoginRateLimiter(IOptions<EmailLoginRateLimitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _loginPerEmail = Partitioned(options.Value.PerEmail);
        _perIp = Partitioned(options.Value.PerIp);
        _forgotPerEmail = Partitioned(options.Value.ForgotPerEmail);
    }

    /// <summary>Spends one login attempt; throws 429 when the IP or the e-mail window is exhausted.</summary>
    public void ThrowIfLoginExceeded(string? ip, string email)
    {
        ThrowIfIpExceeded("login", ip);
        using var porEmail = _loginPerEmail.AttemptAcquire(EmailKey(email));
        if (!porEmail.IsAcquired)
        {
            throw TooManyRequests();
        }
    }

    /// <summary>Spends one forgot-password request of the IP; throws 429 when exhausted. Says nothing of the e-mail.</summary>
    public void ThrowIfForgotIpExceeded(string? ip) => ThrowIfIpExceeded("forgot", ip);

    /// <summary>Whether this e-mail may get one more reset e-mail now; false means send nothing, answer as usual.</summary>
    public bool TryAcquireForgotEmail(string email)
    {
        using var porEmail = _forgotPerEmail.AttemptAcquire(EmailKey(email));
        return porEmail.IsAcquired;
    }

    public void Dispose()
    {
        _loginPerEmail.Dispose();
        _perIp.Dispose();
        _forgotPerEmail.Dispose();
    }

    // An unknown IP (no connection address) is not limited by IP, as in the kit's limiter; the e-mail still is.
    private void ThrowIfIpExceeded(string route, string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return;
        }

        using var porIp = _perIp.AttemptAcquire($"{route}:{ip}");
        if (!porIp.IsAcquired)
        {
            throw TooManyRequests();
        }
    }

    private static string EmailKey(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().Normalize().ToUpperInvariant();
    }

    private static CustomException TooManyRequests() =>
        new(Mensagem, (IEnumerable<string>?)null, HttpStatusCode.TooManyRequests);

    private static PartitionedRateLimiter<string> Partitioned(FixedWindowPolicyOptions policy) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = TimeSpan.FromSeconds(policy.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
}
