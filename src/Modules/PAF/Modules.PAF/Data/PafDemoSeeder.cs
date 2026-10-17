using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Data;

/// <summary>
/// Homologation and demo data of the PAF (requisitos seção 9, "massa de homologação"): one unidade, one
/// fila, one prioridade with its letter, a totem+painel station, a guichê station, a contract with a
/// session limit and the tenant admin in the unidade scope. Run by the DbMigrator <c>seed-demo</c> verb,
/// never by tenant provisioning, so real tenants never get it. Idempotent: every row is looked up first.
/// </summary>
public sealed partial class PafDemoSeeder(IServiceScopeFactory scopeFactory, ILogger<PafDemoSeeder> logger)
{
    public const string NomeUnidade = "Loja Demo";
    public const string NomeFila = "Atendimento";
    public const string NomePrioridade = "Normal";
    public const string Letra = "A";
    public const string NomeTotemPainel = "Totem e Painel";
    public const string NomeGuiche = "Guichê 1";
    public const int LimiteSessoes = 5;

    // A fixed past date keeps reruns identical; the contract is open-ended.
    private static readonly DateOnly InicioVigencia = new(2026, 1, 1);

    public async Task SemearAsync(string tenantId, string adminEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminEmail);

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var tenant = await services.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant '{tenantId}' não existe; rode o seed-demo do kit antes.");

        // Set before resolving any DbContext of the scope: the tenant filter and connection come from it.
        services.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var usuarios = await services.GetRequiredService<IUserService>().GetListAsync(cancellationToken).ConfigureAwait(false);
        var admin = usuarios.FirstOrDefault(u => string.Equals(u.Email, adminEmail, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Administrador do tenant '{tenantId}' não encontrado.");
        var adminId = Guid.Parse(admin.Id!);

        var db = services.GetRequiredService<PafDbContext>();
        var unidade = await db.Unidades.FirstOrDefaultAsync(u => u.Nome == NomeUnidade, cancellationToken).ConfigureAwait(false);
        if (unidade is null)
        {
            unidade = Unidade.Criar(NomeUnidade, documentoFiscal: null);
            db.Unidades.Add(unidade);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        services.GetRequiredService<IUnidadeCorrente>().Definir(unidade.Id);

        var prioridade = await db.Prioridades.FirstOrDefaultAsync(p => p.Nome == NomePrioridade, cancellationToken).ConfigureAwait(false);
        if (prioridade is null)
        {
            prioridade = Prioridade.Criar(unidade.Id, NomePrioridade, valor: 0);
            db.Prioridades.Add(prioridade);
        }

        var fila = await db.Filas.FirstOrDefaultAsync(f => f.Nome == NomeFila, cancellationToken).ConfigureAwait(false);
        if (fila is null)
        {
            fila = Fila.Criar(unidade.Id, NomeFila, NomeFila, setorId: null);
            db.Filas.Add(fila);
            db.FilaPrioridades.Add(FilaPrioridade.Criar(unidade.Id, fila.Id, prioridade.Id, Letra, numeroInicial: 1, ordem: 0));
        }

        if (!await db.Estacoes.AnyAsync(e => e.Nome == NomeTotemPainel, cancellationToken).ConfigureAwait(false))
        {
            var totemPainel = Estacao.Criar(unidade.Id, NomeTotemPainel, TipoEstacao.EmissaoPainel);
            db.Estacoes.Add(totemPainel);
            db.EstacaoFilas.Add(EstacaoFila.Criar(unidade.Id, totemPainel.Id, PapelEstacao.Emissao, fila.Id, ordem: 0));
            db.EstacaoFilas.Add(EstacaoFila.Criar(unidade.Id, totemPainel.Id, PapelEstacao.Painel, fila.Id, ordem: 0));
        }

        if (!await db.Estacoes.AnyAsync(e => e.Nome == NomeGuiche, cancellationToken).ConfigureAwait(false))
        {
            var guiche = Estacao.Criar(unidade.Id, NomeGuiche, TipoEstacao.Guiche);
            db.Estacoes.Add(guiche);
            db.EstacaoFilas.Add(EstacaoFila.Criar(unidade.Id, guiche.Id, PapelEstacao.Guiche, fila.Id, ordem: 0));
            db.EstacaoPrioridades.Add(EstacaoPrioridade.Criar(unidade.Id, guiche.Id, prioridade.Id, ordem: 0));
        }

        if (!await db.ContratosUnidade.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            db.ContratosUnidade.Add(ContratoUnidade.Criar(
                unidade.Id, InicioVigencia, fimVigencia: null, "Demo", LimiteSessoes, PoliticaLimiteLicenca.Recusar));
        }

        if (!await db.UsuariosUnidade.AnyAsync(u => u.UsuarioId == adminId && u.UnidadeId == unidade.Id, cancellationToken).ConfigureAwait(false))
        {
            int? ultimaOrdem = await db.UsuariosUnidade.Where(u => u.UsuarioId == adminId)
                .MaxAsync(u => (int?)u.Ordem, cancellationToken).ConfigureAwait(false);
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(adminId, unidade.Id, (ultimaOrdem ?? -1) + 1));
        }

        int gravadas = await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogSemeado(logger, tenantId, unidade.Id, gravadas);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[PAF demo-seed] [{TenantId}] unidade {UnidadeId} ready, {Rows} rows written")]
    private static partial void LogSemeado(ILogger logger, string tenantId, Guid unidadeId, int rows);
}
