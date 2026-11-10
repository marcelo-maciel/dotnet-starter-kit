using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// Grava em <c>Estacao</c> o instante da última conexão e da última queda de cada papel de credencial (RNF20, ARQ
/// seção 7, decidido em 03/10/2026), a partir dos eventos de conexão do <see cref="PafHub"/>. O instante vem do
/// PostgreSQL e só a coluna do papel da credencial muda: a queda do painel de uma estação combinada não toca a emissão.
/// As colunas são o histórico; o estado presente de uma instância que morreu sem desconectar vem do registro de
/// conexões (<c>ListEstacoesOperador</c>), cuja entrada vence sozinha.
/// </summary>
public sealed partial class MonitoramentoEstacoes(
    ConexoesPaf conexoes,
    RegistroConexoes registro,
    IServiceScopeFactory escopos,
    ILogger<MonitoramentoEstacoes> logger)
{
    // ponytail: fixed ceiling for one UPDATE by primary key; tune once production latency is measured.
    private static readonly TimeSpan PrazoEscrita = TimeSpan.FromSeconds(5);

    /// <summary>Chamado depois que a conexão entrou no registro e na lista local.</summary>
    public Task RegistrarConexaoAsync(ConexaoEstacao estacao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(estacao);
        return GravarAsync(estacao, SqlConexao(estacao), ct);
    }

    /// <summary>
    /// Chamado depois que a conexão saiu do registro e da lista local. Outra conexão viva do mesmo papel (a reconexão
    /// rápida que chegou antes de a antiga cair) mantém o papel conectado, e a queda da antiga não é gravada.
    /// </summary>
    public async Task RegistrarQuedaAsync(ConexaoEstacao estacao, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(estacao);
        if (await OutraConexaoVivaAsync(estacao, ct).ConfigureAwait(false))
        {
            LogQuedaComOutraConexao(logger, estacao.TenantId, estacao.EstacaoId, estacao.Papel);
            return;
        }

        await GravarAsync(estacao, SqlQueda(estacao), ct).ConfigureAwait(false);
    }

    // The local list covers the test host and a Valkey outage; the registry covers a reconnection on another instance.
    private async Task<bool> OutraConexaoVivaAsync(ConexaoEstacao estacao, CancellationToken ct)
    {
        if (conexoes.Locais().Any(l => l.Conexao is ConexaoEstacao outra && MesmoPapel(outra, estacao)))
        {
            return true;
        }

        var doRegistro = await registro.DaEstacaoAsync(estacao.TenantId, estacao.EstacaoId, estacao.Papel, ct).ConfigureAwait(false);
        return doRegistro is { Count: > 0 };
    }

    private static bool MesmoPapel(ConexaoEstacao a, ConexaoEstacao b) =>
        string.Equals(a.TenantId, b.TenantId, StringComparison.Ordinal) && a.EstacaoId == b.EstacaoId
        && string.Equals(a.Papel, b.Papel, StringComparison.Ordinal);

    private async Task GravarAsync(ConexaoEstacao estacao, FormattableString sql, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(PrazoEscrita);
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var tenant = await escopo.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
                .GetAsync(estacao.TenantId).WaitAsync(limite.Token).ConfigureAwait(false);
            if (tenant is null)
            {
                LogTenantAusente(logger, estacao.TenantId);
                return;
            }

            Instalar(escopo.ServiceProvider, tenant);
            var linhas = await escopo.ServiceProvider.GetRequiredService<PafDbContext>().Database
                .ExecuteSqlAsync(sql, limite.Token).ConfigureAwait(false);
            if (linhas == 0)
            {
                LogEstacaoAusente(logger, estacao.TenantId, estacao.EstacaoId, estacao.Papel);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Monitoring is a side record: a failed or slow write must neither refuse the station's connection nor
            // break its disconnect. The next connection event of the role writes again.
            LogGravacaoFalhou(logger, ex, estacao.TenantId, estacao.EstacaoId, estacao.Papel);
        }
    }

    // Tenant and unidade in the SQL (raw SQL skips the query filters); the column is fixed by the claim's role, never
    // built from it. ConexaoPaf.Ler accepts only these two roles.
    private static FormattableString SqlConexao(ConexaoEstacao e) => e.Papel switch
    {
        PafPapeisEstacao.Emissao => $"""
            UPDATE paf."Estacoes" SET "UltimaConexaoEmissaoEm" = now()
             WHERE "TenantId" = {e.TenantId} AND "UnidadeId" = {e.UnidadeId} AND "Id" = {e.EstacaoId}
            """,
        PafPapeisEstacao.Painel => $"""
            UPDATE paf."Estacoes" SET "UltimaConexaoPainelEm" = now()
             WHERE "TenantId" = {e.TenantId} AND "UnidadeId" = {e.UnidadeId} AND "Id" = {e.EstacaoId}
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.Papel, "Papel sem credencial de estação."),
    };

    private static FormattableString SqlQueda(ConexaoEstacao e) => e.Papel switch
    {
        PafPapeisEstacao.Emissao => $"""
            UPDATE paf."Estacoes" SET "UltimaQuedaEmissaoEm" = now()
             WHERE "TenantId" = {e.TenantId} AND "UnidadeId" = {e.UnidadeId} AND "Id" = {e.EstacaoId}
            """,
        PafPapeisEstacao.Painel => $"""
            UPDATE paf."Estacoes" SET "UltimaQuedaPainelEm" = now()
             WHERE "TenantId" = {e.TenantId} AND "UnidadeId" = {e.UnidadeId} AND "Id" = {e.EstacaoId}
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.Papel, "Papel sem credencial de estação."),
    };

    // Finbuckle's DbContext reads the tenant when built: installed before the resolve. Synchronous so the AsyncLocal
    // stays set in this method (an awaited helper would drop it on return).
    private static void Instalar(IServiceProvider servicos, AppTenantInfo tenant) =>
        servicos.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

    [LoggerMessage(EventId = 9130, Level = LogLevel.Error,
        Message = "PAF: monitoramento nao gravou o evento da estacao tenant={TenantId} estacao={EstacaoId} papel={Papel}")]
    private static partial void LogGravacaoFalhou(ILogger logger, Exception ex, string tenantId, Guid estacaoId, string papel);

    [LoggerMessage(EventId = 9131, Level = LogLevel.Information,
        Message = "PAF: queda ignorada, outra conexao viva do papel tenant={TenantId} estacao={EstacaoId} papel={Papel}")]
    private static partial void LogQuedaComOutraConexao(ILogger logger, string tenantId, Guid estacaoId, string papel);

    [LoggerMessage(EventId = 9132, Level = LogLevel.Warning,
        Message = "PAF: monitoramento sem estacao na unidade da credencial tenant={TenantId} estacao={EstacaoId} papel={Papel}")]
    private static partial void LogEstacaoAusente(ILogger logger, string tenantId, Guid estacaoId, string papel);

    [LoggerMessage(EventId = 9133, Level = LogLevel.Warning, Message = "PAF: tenant {TenantId} ausente no monitoramento de estacao")]
    private static partial void LogTenantAusente(ILogger logger, string tenantId);
}
