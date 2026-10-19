namespace FSH.Modules.PAF.Contracts.v1.TempoReal;

/// <summary>
/// Something a painel of the unidade plays changed (a mídia, a programação or a station's list): every painel of the
/// unidade re-reads <c>estacao/painel/midias</c>. Sent to the PafHub unidade group under <see cref="Metodo"/>; arrives
/// twice by design (direct send and outbox) with the same <paramref name="EventoId"/>.
/// </summary>
public sealed record ProgramacaoAlterada(Guid EventoId, Guid UnidadeId)
{
    public const string Metodo = "ProgramacaoAlterada";
}
