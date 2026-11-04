using System.Net;
using System.Threading.RateLimiting;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Web.RateLimiting;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Identity.Services;

/// <summary>
/// Limits of the routes that take an e-mail without a tenant (login and forgot password), section
/// <c>EmailLoginRateLimit</c>. Two windows, both always on, whatever <c>RateLimitingOptions:Enabled</c> says:
/// <list type="bullet">
/// <item>per e-mail, tight: 5 attempts a minute. Nobody signs in five times a minute on purpose; Identity's
/// lockout (5 wrong passwords, 15 minutes) still guards the account itself.</item>
/// <item>per IP, loose: 100 attempts a minute. The guichês of one unit leave through one NAT and all sign in
/// when the turn opens: 100 covers some 50 stations with a mistyped password each in the same minute, where
/// the kit's <c>auth</c> policy (10 a minute per IP) would lock most of them out.</item>
/// </list>
/// </summary>
public sealed class EmailLoginRateLimitOptions
{
    public FixedWindowPolicyOptions PerEmail { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60, QueueLimit = 0 };

    public FixedWindowPolicyOptions PerIp { get; set; } = new() { PermitLimit = 100, WindowSeconds = 60, QueueLimit = 0 };
}

/// <summary>
/// Both windows of <see cref="EmailLoginRateLimitOptions"/>, keyed by flow so a login attempt does not spend the
/// forgot-password allowance. Checked in the handler because the e-mail is in the body, out of reach of the
/// ASP.NET rate-limiting middleware's partitioner.
/// </summary>
// ponytail: in-memory, per API instance; two instances double the effective limit. Upgrade path: a Valkey
// fixed-window counter keyed the same way.
public sealed class EmailLoginRateLimiter : IDisposable
{
    public const string Mensagem = "Muitas tentativas. Aguarde um minuto e tente de novo.";

    private readonly PartitionedRateLimiter<string> _perEmail;
    private readonly PartitionedRateLimiter<string> _perIp;

    public EmailLoginRateLimiter(IOptions<EmailLoginRateLimitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _perEmail = Partitioned(options.Value.PerEmail);
        _perIp = Partitioned(options.Value.PerIp);
    }

    /// <summary>Spends one attempt of <paramref name="flow"/>; throws 429 when either window is exhausted.</summary>
    public void ThrowIfExceeded(string flow, string? ip, string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        // An unknown IP (no connection address) is not limited by IP, as in the kit's limiter; the e-mail still is.
        if (!string.IsNullOrWhiteSpace(ip))
        {
            using var porIp = _perIp.AttemptAcquire($"{flow}:{ip}");
            if (!porIp.IsAcquired)
            {
                throw TooManyRequests();
            }
        }

        using var porEmail = _perEmail.AttemptAcquire($"{flow}:{email.Trim().Normalize().ToUpperInvariant()}");
        if (!porEmail.IsAcquired)
        {
            throw TooManyRequests();
        }
    }

    public void Dispose()
    {
        _perEmail.Dispose();
        _perIp.Dispose();
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
