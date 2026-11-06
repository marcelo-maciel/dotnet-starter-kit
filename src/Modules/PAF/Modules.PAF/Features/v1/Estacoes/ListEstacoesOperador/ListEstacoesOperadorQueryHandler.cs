using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Estacoes;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoesOperador;

/// <summary>
/// Support screen of RNF20 (Codefi operator): one client tenant per request, the tenant filter stays and only the unit
/// filter goes off, by name, because the operator has no current unidade (arquitetura seção 6). Present state comes
/// from the connection registry, which sees every instance and drops the entries of one that died; the columns, which
/// only the hub's connect and disconnect write, decide when the registry does not answer.
/// </summary>
public sealed class ListEstacoesOperadorQueryHandler(
    PafDbContext db,
    RegistroConexoes registro,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : IQueryHandler<ListEstacoesOperadorQuery, MonitoramentoEstacoesDto>
{
    private static readonly string[] SoOFiltroDeUnidade = [PafQueryFilters.Unidade];

    public async ValueTask<MonitoramentoEstacoesDto> Handle(ListEstacoesOperadorQuery query, CancellationToken cancellationToken)
    {
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new InvalidOperationException("Monitoramento de estação sem tenant resolvido.");

        var unidades = await db.Unidades.AsNoTracking()
            .OrderByDescending(u => u.Inicial)
            .ThenBy(u => u.CriadoEm)
            .Select(u => new { u.Id, u.Nome })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var estacoes = await db.Estacoes.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .OrderBy(e => e.Nome)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var filas = await db.Filas.IgnoreQueryFilters(SoOFiltroDeUnidade).AsNoTracking()
            .Select(f => new { f.UnidadeId, Janela = new JanelaDaFila(f.Ativa, f.HoraInicial, f.HoraFinal) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var agora = await PafRelogio.AgoraEmSaoPauloAsync(db, cancellationToken).ConfigureAwait(false);
        var hora = TimeOnly.FromDateTime(agora);
        var janelas = filas.ToLookup(f => f.UnidadeId, f => f.Janela);
        var porUnidade = estacoes.ToLookup(e => e.UnidadeId);

        var peloRegistro = true;
        var papeis = new List<PapelEstacaoMonitoradoDto>();
        foreach (var unidade in unidades)
        {
            var emOperacao = HorarioDeOperacao.EmOperacao(janelas[unidade.Id], hora);
            foreach (var estacao in porUnidade[unidade.Id])
            {
                foreach (var papel in PapeisDaEstacao.ComCredencial(estacao.Tipo))
                {
                    var (conexao, queda) = Colunas(estacao, papel);
                    var claim = PapeisDaEstacao.ValorDaClaim(papel);

                    // Once the registry fails, the rest of the list reads the columns instead of waiting on it again.
                    var vivas = peloRegistro
                        ? await registro.DaEstacaoAsync(tenantId, estacao.Id, claim, cancellationToken).ConfigureAwait(false)
                        : null;
                    peloRegistro = vivas is not null;
                    var conectada = vivas is not null
                        ? vivas.Count > 0
                        : conexao is { } c && (queda is null || queda < c);

                    papeis.Add(new PapelEstacaoMonitoradoDto(
                        unidade.Id, unidade.Nome, estacao.Id, estacao.Nome, estacao.Tipo.ToString(), estacao.Ativa, claim,
                        conexao, queda, conectada, estacao.Ativa && !conectada && emOperacao));
                }
            }
        }

        return new MonitoramentoEstacoesDto(agora, peloRegistro, papeis);
    }

    private static (DateTimeOffset? Conexao, DateTimeOffset? Queda) Colunas(Estacao estacao, PapelEstacao papel) => papel switch
    {
        PapelEstacao.Emissao => (estacao.UltimaConexaoEmissaoEm, estacao.UltimaQuedaEmissaoEm),
        PapelEstacao.Painel => (estacao.UltimaConexaoPainelEm, estacao.UltimaQuedaPainelEm),
        _ => throw new ArgumentOutOfRangeException(nameof(papel), papel, "Papel sem credencial de estação."),
    };
}
