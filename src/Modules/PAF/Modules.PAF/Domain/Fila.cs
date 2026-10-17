using FSH.Framework.Core.Domain;

namespace FSH.Modules.PAF.Domain;

/// <summary>Fila de atendimento da unidade (RF02). A janela de horário restringe só a emissão.</summary>
public sealed class Fila : BaseEntity<Guid>, IEscopoUnidade
{
    public Guid UnidadeId { get; private set; }
    public Guid? SetorId { get; private set; }
    public string Nome { get; private set; } = default!;
    public string NomeExibicao { get; private set; } = default!;

#pragma warning disable S1144 // EF Core writes these setters via reflection
    /// <summary>Nulas as duas = sem restrição de horário.</summary>
    public TimeOnly? HoraInicial { get; private set; }
    public TimeOnly? HoraFinal { get; private set; }

    public string? Fonte { get; private set; }
    public string? CorFonte { get; private set; }
    public string? CorBotaoSelecionado { get; private set; }
    public bool Ativa { get; private set; }
    public DateTimeOffset? UltimaChamadaEm { get; private set; }
#pragma warning restore S1144

    private Fila() { }

    public static Fila Criar(Guid unidadeId, string nome, string nomeExibicao, Guid? setorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeExibicao);
        return new Fila
        {
            Id = Guid.CreateVersion7(),
            UnidadeId = unidadeId,
            SetorId = setorId,
            Nome = nome,
            NomeExibicao = nomeExibicao,
            Ativa = true,
        };
    }

    public void Atualizar(string nome, string nomeExibicao)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        ArgumentException.ThrowIfNullOrWhiteSpace(nomeExibicao);
        Nome = nome;
        NomeExibicao = nomeExibicao;
    }

    /// <summary>Both null = no restriction. The final hour is after the initial one: no window crosses midnight (RF02).</summary>
    public void DefinirJanela(TimeOnly? horaInicial, TimeOnly? horaFinal)
    {
        if (horaInicial.HasValue != horaFinal.HasValue)
        {
            throw new ArgumentException("A janela de horário precisa das duas horas, ou de nenhuma.", nameof(horaFinal));
        }

        if (horaInicial >= horaFinal)
        {
            throw new ArgumentException("A hora final precisa ser posterior à inicial.", nameof(horaFinal));
        }

        HoraInicial = horaInicial;
        HoraFinal = horaFinal;
    }

    public void Ativar() => Ativa = true;

    public void Desativar() => Ativa = false;
}
