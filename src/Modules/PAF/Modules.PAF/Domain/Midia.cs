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

#pragma warning disable S1144 // EF Core writes these setters via reflection
    public bool Ativa { get; private set; }
    public int Versao { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
#pragma warning restore S1144

    private Midia() { }

    /// <summary>Unidade, tipo e chave vêm do envio, para a mídia nunca contradizer o objeto que aponta.</summary>
    public static Midia Criar(EnvioMidia envio, string descricao, int duracaoSegundos)
    {
        ArgumentNullException.ThrowIfNull(envio);
        ArgumentException.ThrowIfNullOrWhiteSpace(descricao);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(descricao.Length, DescricaoMaxLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(duracaoSegundos, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duracaoSegundos, DuracaoMaximaSegundos);
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
}
