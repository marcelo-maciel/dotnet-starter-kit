using System.Linq.Expressions;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Persistence.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FSH.Modules.PAF.Data;

public sealed class PafDbContext : BaseDbContext
{
    public const string Schema = "paf";

    // Resolved from the request scope the same way BaseDbContext.OnConfiguring finds its connection
    // provider, because the constructor signature is fixed (Architecture.Tests builds every
    // BaseDbContext by reflection). Null in design-time tooling: the filter then returns nothing.
    private readonly IUnidadeCorrente? _unidadeCorrente;

    public PafDbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<PafDbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment) : base(multiTenantContextAccessor, options, settings, environment)
    {
        _unidadeCorrente = options?.FindExtension<CoreOptionsExtension>()?
            .ApplicationServiceProvider?
            .GetService<IUnidadeCorrente>();
    }

    public DbSet<Unidade> Unidades => Set<Unidade>();
    public DbSet<Setor> Setores => Set<Setor>();
    public DbSet<Fila> Filas => Set<Fila>();
    public DbSet<Prioridade> Prioridades => Set<Prioridade>();
    public DbSet<FilaPrioridade> FilaPrioridades => Set<FilaPrioridade>();
    public DbSet<Estacao> Estacoes => Set<Estacao>();
    public DbSet<EstacaoFila> EstacaoFilas => Set<EstacaoFila>();
    public DbSet<EstacaoPrioridade> EstacaoPrioridades => Set<EstacaoPrioridade>();
    public DbSet<VinculoEstacao> VinculosEstacao => Set<VinculoEstacao>();
    public DbSet<Ficha> Fichas => Set<Ficha>();
    public DbSet<Atendimento> Atendimentos => Set<Atendimento>();
    public DbSet<Rechamada> Rechamadas => Set<Rechamada>();
    public DbSet<Parametro> Parametros => Set<Parametro>();
    public DbSet<ContratoUnidade> ContratosUnidade => Set<ContratoUnidade>();
    public DbSet<UsuarioUnidade> UsuariosUnidade => Set<UsuarioUnidade>();
    public DbSet<PerfilUsuario> PerfisUsuario => Set<PerfilUsuario>();
    public DbSet<SessaoEstacao> SessoesEstacao => Set<SessaoEstacao>();
    public DbSet<AtividadeSessao> AtividadesSessao => Set<AtividadeSessao>();
    public DbSet<AtoImpersonacao> AtosImpersonacao => Set<AtoImpersonacao>();
    public DbSet<ConsumoLicenca> ConsumosLicenca => Set<ConsumoLicenca>();

    public DbSet<EnvioMidia> EnviosMidia => Set<EnvioMidia>();
    public DbSet<Midia> Midias => Set<Midia>();
    public DbSet<ProgramacaoMidia> ProgramacoesMidia => Set<ProgramacaoMidia>();
    public DbSet<PainelMidia> PainelMidias => Set<PainelMidia>();

    // Read by the query filter on the executing context instance, once per query.
    private Guid? UnidadeCorrenteId => _unidadeCorrente?.UnidadeId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PafDbContext).Assembly);
        AplicarFiltroDeUnidade(modelBuilder);
        // base.OnModelCreating runs LAST so BaseDbContext's auto-apply sees
        // fully-configured entities (including HasMany child types).
        base.OnModelCreating(modelBuilder);
    }

    // Driven by the IEscopoUnidade marker, not a hand-kept list, so a new operational entity cannot
    // ship without the filter. Equality against a null current unit matches no row: fail-closed.
    private void AplicarFiltroDeUnidade(ModelBuilder modelBuilder)
    {
        Expression<Func<IEscopoUnidade, bool>> filtro = e => e.UnidadeId == UnidadeCorrenteId;

        var entidadesComUnidade = modelBuilder.Model.GetEntityTypes()
            .Select(e => e.ClrType)
            .Where(t => typeof(IEscopoUnidade).IsAssignableFrom(t))
            .ToList();

        foreach (var tipo in entidadesComUnidade)
        {
            var parametro = Expression.Parameter(tipo, "e");
            var corpo = ReplacingExpressionVisitor.Replace(filtro.Parameters.Single(), parametro, filtro.Body);
            modelBuilder.Entity(tipo).HasQueryFilter(PafQueryFilters.Unidade, Expression.Lambda(corpo, parametro));
        }

        // Empresa-level rows (UnidadeId NULL) belong to the whole tenant; unit-level rows follow the same rule as above.
        modelBuilder.Entity<Parametro>().HasQueryFilter(PafQueryFilters.Unidade,
            p => p.UnidadeId == null || p.UnidadeId == UnidadeCorrenteId);
    }
}
