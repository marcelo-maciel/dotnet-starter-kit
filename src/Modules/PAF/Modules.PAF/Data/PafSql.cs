namespace FSH.Modules.PAF.Data;

/// <summary>
/// SQL fragments of the PAF schema. Instants come from the PostgreSQL clock, never from the app
/// process, so two instances cannot reorder the queue or split midnight differently (seção 4.2, ADR 0005).
/// </summary>
internal static class PafSql
{
    public const string Agora = "now()";

    /// <summary>
    /// Body of a STORED generated date column derived from an instant column. WHY generated and not a
    /// default: a generated column is recomputed on every UPDATE of its source, so DataElegibilidade
    /// follows each rewrite of ElegivelDesde in the same write with no code path able to forget it, and
    /// DataOperacional can never disagree with CriadoEm. PostgreSQL accepts it because
    /// timezone(text, timestamptz) is IMMUTABLE (checked on postgres:17-alpine, the test image).
    /// </summary>
    public static string DataEmSaoPaulo(string colunaInstante) =>
        $"(\"{colunaInstante}\" AT TIME ZONE 'America/Sao_Paulo')::date";

    public const string EstadoEmCurso = "\"Estado\" IN ('Chamado', 'EmAtendimento')";

    public const string VinculoAtivo = "\"LiberadoEm\" IS NULL";
}
