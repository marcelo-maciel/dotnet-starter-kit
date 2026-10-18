using Microsoft.EntityFrameworkCore.Storage;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Vaga reservada e ainda não gravada, dentro da transação que segura os bloqueios. Descartada sem
/// <see cref="ConfirmarAsync"/>, a transação volta, o membro provisório sai do conjunto e nada fica ocupado.
/// </summary>
public sealed class ReservaLicenca : IAsyncDisposable
{
    private readonly Licencas _licencas;
    private bool _confirmada;

    internal ReservaLicenca(
        Licencas licencas,
        IDbContextTransaction transacao,
        string tenantId,
        Guid usuarioId,
        CandidataLicenca candidata,
        string? membroReserva,
        DesfechoOcupacao desfecho)
    {
        _licencas = licencas;
        Transacao = transacao;
        TenantId = tenantId;
        UsuarioId = usuarioId;
        Candidata = candidata;
        MembroReserva = membroReserva;
        Desfecho = desfecho;
    }

    public Guid UnidadeId => Candidata.UnidadeId;

    internal IDbContextTransaction Transacao { get; }
    internal string TenantId { get; }
    internal Guid UsuarioId { get; }
    internal CandidataLicenca Candidata { get; }

    /// <summary>Nulo quando a vaga foi conferida no modo degradado.</summary>
    internal string? MembroReserva { get; }

    /// <summary>Como a vaga foi reservada: no conjunto, ou no modo degradado com o Valkey fora ou não reconstruído.</summary>
    internal DesfechoOcupacao Desfecho { get; }

    /// <summary>
    /// Ocupa a licença para a sessão e solta, no mesmo ato, licenças e guichês das sessões anteriores do usuário
    /// e o guichê que a própria sessão ainda tiver, porque a licença dela já tinha sido solta.
    /// </summary>
    public async Task ConfirmarAsync(Guid sessaoId, IReadOnlyCollection<Guid> sessoesAnteriores, CancellationToken ct)
    {
        await _licencas.ConfirmarAsync(this, sessaoId, sessoesAnteriores, ct);
        _confirmada = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_confirmada)
            {
                await _licencas.DescartarAsync(this);
            }
        }
        finally
        {
            await Transacao.DisposeAsync();
        }
    }
}
