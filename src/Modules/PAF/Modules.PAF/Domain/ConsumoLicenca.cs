using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Registro histórico do uso de licenças da unidade (RF22, ARQ seção 9): a amostra periódica do tamanho do conjunto e
/// uma linha por ocupação acima do limite contratado. Mede uso, nunca serve para faturar. A ocupação e o job gravam
/// por SQL bruto, na transação deles (<c>Licencas</c> e <c>ManutencaoLicencas</c>).
/// </summary>
public sealed class ConsumoLicenca : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public TipoConsumoLicenca Tipo { get; private set; }
    public int Ocupadas { get; private set; }
    public int Limite { get; private set; }

    /// <summary>Sessão que ocupou acima do limite; nula na amostra.</summary>
    public Guid? SessaoId { get; private set; }

#pragma warning disable S1144 // EF Core writes these setters via reflection
    /// <summary>Instante da ocupação no excedente; início da janela de amostragem na amostra.</summary>
    public DateTimeOffset RegistradoEm { get; private set; }
#pragma warning restore S1144

    private ConsumoLicenca() { }

    public static ConsumoLicenca CriarAmostra(Guid unidadeId, int ocupadas, int limite) =>
        new() { Id = Guid.CreateVersion7(), UnidadeId = unidadeId, Tipo = TipoConsumoLicenca.Amostra, Ocupadas = ocupadas, Limite = limite };

    public static ConsumoLicenca CriarExcedente(Guid unidadeId, Guid sessaoId, int ocupadas, int limite) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            Tipo = TipoConsumoLicenca.Excedente,
            Ocupadas = ocupadas,
            Limite = limite,
            SessaoId = sessaoId,
        };
}
