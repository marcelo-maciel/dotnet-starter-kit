using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Data.Configurations;
using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.PAF.Provisionamento;

/// <summary>
/// What a new client tenant gets from the PAF (RF23, ARQ seção 5): the initial unidade, and its initial admin with
/// escopo empresa and that unidade as principal. Each step is idempotent and safe under concurrent runs.
/// </summary>
internal sealed class ProvisionamentoTenantPaf(PafDbContext db)
{
    /// <summary>The full tenant (name, admin e-mail, connection) from the catalog; null when it is gone.</summary>
    public static async Task<AppTenantInfo?> LerTenantAsync(IServiceScopeFactory scopeFactory, string tenantId)
    {
        await using var escopo = scopeFactory.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId);
    }

    /// <summary>
    /// Scope with the full tenant installed before any DbContext is built: the event bus installs only the id, and the
    /// job runs with no tenant at all. Synchronous on purpose: the Finbuckle context is an AsyncLocal, and a value set
    /// inside an async method does not flow back to its caller.
    /// </summary>
    public static AsyncServiceScope AbrirEscopo(IServiceScopeFactory scopeFactory, AppTenantInfo tenant)
    {
        var escopo = scopeFactory.CreateAsyncScope();
        escopo.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);
        return escopo;
    }

    /// <summary>The initial unidade of the tenant, created when missing; a concurrent creator wins and its row is returned.</summary>
    public async Task<Guid> GarantirUnidadeInicialAsync(string nome, CancellationToken ct)
    {
        if (await UnidadeInicialAsync(ct) is { } existente)
        {
            return existente;
        }

        db.Unidades.Add(Unidade.CriarInicial(nome));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PafRestricoes.Violou(ex, UnidadeConfiguration.IndiceInicial))
        {
            db.ChangeTracker.Clear();
        }

        return await UnidadeInicialAsync(ct)
            ?? throw new InvalidOperationException("A unidade inicial sumiu logo depois de criada.");
    }

    public Task<Guid?> UnidadeInicialAsync(CancellationToken ct) =>
        db.Unidades.AsNoTracking().Where(u => u.Inicial).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Escopo empresa with the initial unidade as principal (Ordem 0), only for an admin with no scope yet: anything
    /// already there was written by someone with authority over it and is kept. False when nothing was written.
    /// </summary>
    public async Task<bool> AssociarAdministradorAsync(string tenantId, Guid adminId, Guid unidadeInicialId, CancellationToken ct)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct);
        // Same key as SetUsuarioUnidades: two runs of this job, or a scope edit racing it, serialize per user.
        var chave = $"paf-escopo:{tenantId}:{adminId}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))", ct);

        if (await db.UsuariosUnidade.AnyAsync(u => u.UsuarioId == adminId, ct))
        {
            return false;
        }

        var perfil = await db.PerfisUsuario.FirstOrDefaultAsync(p => p.UsuarioId == adminId, ct);
        if (perfil is null)
        {
            perfil = PerfilUsuario.Criar(adminId);
            db.PerfisUsuario.Add(perfil);
        }

        perfil.DefinirEscopoEmpresa();
        db.UsuariosUnidade.Add(UsuarioUnidade.Criar(adminId, unidadeInicialId, 0));
        await db.SaveChangesAsync(ct);
        await transacao.CommitAsync(ct);
        return true;
    }
}
