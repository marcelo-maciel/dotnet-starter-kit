using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>Contrato da unidade (RF22). Unidade sem contrato vigente não tem saldo de licença.</summary>
public sealed class ContratoUnidade : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public DateOnly InicioVigencia { get; private set; }
    public DateOnly? FimVigencia { get; private set; }
    public string Plano { get; private set; } = default!;
    public int LimiteSessoes { get; private set; }
    public PoliticaLimiteLicenca Politica { get; private set; }

    private ContratoUnidade() { }

    public static ContratoUnidade Criar(
        Guid unidadeId,
        DateOnly inicioVigencia,
        DateOnly? fimVigencia,
        string plano,
        int limiteSessoes,
        PoliticaLimiteLicenca politica)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plano);
        ArgumentOutOfRangeException.ThrowIfNegative(limiteSessoes);
        return new ContratoUnidade
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            InicioVigencia = inicioVigencia,
            FimVigencia = fimVigencia,
            Plano = plano,
            LimiteSessoes = limiteSessoes,
            Politica = politica,
        };
    }
}
