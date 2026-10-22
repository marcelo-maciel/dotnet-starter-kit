using FSH.Framework.Core.Domain;
using FSH.Modules.PAF.Midias;

namespace FSH.Modules.PAF.Domain;

/// <summary>
/// Arquivo de mídia cadastrado (RF03). Aponta para o objeto pelo <see cref="EnvioMidia"/> da mesma
/// unidade (FK composta que leva também a chave, então a cópia da chave aqui nunca diverge do envio), e
/// uma chave serve a uma mídia só. <see cref="Versao"/> sobe a cada
/// edição e troca de arquivo.
/// </summary>
public sealed class Midia : BaseEntity<Guid>, IEscopoUnidade
{
    public const int DescricaoMaxLength = 200;

    /// <summary>Um dia: a programação recusa terminar depois da meia-noite (D13), então nada maior cabe nela.</summary>
    public const int DuracaoMaximaSegundos = 86_400;

    public Guid UnidadeId { get; private set; }
    public string Descricao { get; private set; } = default!;
    public TipoMidia Tipo { get; private set; }
    public Guid EnvioId { get; private set; }
    public string ChaveObjeto { get; private set; } = default!;
    public int DuracaoSegundos { get; private set; }

    public bool Ativa { get; private set; }
    public int Versao { get; private set; }

#pragma warning disable S1144 // EF Core writes these setters via reflection
    public DateTimeOffset CriadoEm { get; private set; }
#pragma warning restore S1144

    private Midia() { }

    /// <summary>Unidade, tipo e chave vêm do envio, para a mídia nunca contradizer o objeto que aponta.</summary>
    public static Midia Criar(EnvioMidia envio, string descricao, int duracaoSegundos)
    {
        ArgumentNullException.ThrowIfNull(envio);
        ValidarDescricaoEDuracao(descricao, duracaoSegundos);
        return new Midia
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = envio.UnidadeId,
            Descricao = descricao,
            Tipo = envio.Tipo,
            EnvioId = envio.Id,
            ChaveObjeto = envio.ChaveObjeto,
            DuracaoSegundos = duracaoSegundos,
            Ativa = true,
        };
    }

    public void Editar(string descricao, int duracaoSegundos)
    {
        ValidarDescricaoEDuracao(descricao, duracaoSegundos);
        Descricao = descricao;
        DuracaoSegundos = duracaoSegundos;
        Versao++;
    }

    /// <summary>
    /// Points to a new upload of the same unidade and type: a programação or painel list built around a video must not
    /// start showing an image unseen. The old envio stays in the store until the cleanup after the 30-day lock.
    /// </summary>
    public void TrocarArquivo(EnvioMidia envio)
    {
        ArgumentNullException.ThrowIfNull(envio);
        if (envio.UnidadeId != UnidadeId || envio.Tipo != Tipo)
        {
            throw new ArgumentException("O envio da troca precisa ser da mesma unidade e do mesmo tipo da mídia.", nameof(envio));
        }

        EnvioId = envio.Id;
        ChaveObjeto = envio.ChaveObjeto;
        Versao++;
    }

    /// <summary>Activation also moves <see cref="Versao"/>, so an edit read before it is refused instead of applied unseen.</summary>
    public void Ativar()
    {
        Ativa = true;
        Versao++;
    }

    public void Desativar()
    {
        Ativa = false;
        Versao++;
    }

    private static void ValidarDescricaoEDuracao(string descricao, int duracaoSegundos)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descricao);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(descricao.Length, DescricaoMaxLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(duracaoSegundos, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duracaoSegundos, DuracaoMaximaSegundos);
    }
}
