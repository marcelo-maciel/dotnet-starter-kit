namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Operational row owned by one unidade. PafDbContext applies the named unit filter to every
/// entity carrying this marker (ADR 0003).
/// </summary>
public interface IEscopoUnidade
{
    Guid UnidadeId { get; }
}
