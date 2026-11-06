namespace FSH.Modules.PAF.Contracts.v1.TempoReal;

/// <summary>
/// The session's current unit changed on the server without the screen asking (a revived session opened in
/// another unit of the cascade, RF22): every window of the user drops what it shows and re-reads its context.
/// Sent to the PafHub user group under <see cref="Metodo"/>.
/// </summary>
public sealed record UnidadeCorrenteAlterada(Guid EventoId, Guid UnidadeId)
{
    public const string Metodo = "UnidadeCorrenteAlterada";
}
