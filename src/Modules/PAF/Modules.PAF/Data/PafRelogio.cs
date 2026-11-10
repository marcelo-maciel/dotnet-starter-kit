using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Data;

/// <summary>
/// Wall clock of America/Sao_Paulo read from PostgreSQL (seção 4.2). Inside a transaction now() is the
/// transaction start, so the value matches the now() default of every row the same transaction inserts.
/// </summary>
internal static class PafRelogio
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static async Task<DateTime> AgoraEmSaoPauloAsync(PafDbContext db, CancellationToken ct)
    {
        var agora = await db.Database
            .SqlQuery<DateTime>($"SELECT (now() AT TIME ZONE 'America/Sao_Paulo') AS \"Value\"")
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return agora.Single();
    }

    public static async Task<DateOnly> HojeAsync(PafDbContext db, CancellationToken ct) =>
        DateOnly.FromDateTime(await AgoraEmSaoPauloAsync(db, ct).ConfigureAwait(false));

    /// <summary>A São Paulo wall clock read from the database, with its offset, so a screen gets the instant and the wall clock.</summary>
    public static DateTimeOffset ComFusoDeSaoPaulo(DateTime agoraSaoPaulo) =>
        new(DateTime.SpecifyKind(agoraSaoPaulo, DateTimeKind.Unspecified), SaoPaulo.GetUtcOffset(agoraSaoPaulo));

    /// <summary>Presentation only: the instant itself always comes from the database.</summary>
    public static DateTimeOffset EmSaoPaulo(DateTimeOffset instante) =>
        TimeZoneInfo.ConvertTime(instante, SaoPaulo);
}
