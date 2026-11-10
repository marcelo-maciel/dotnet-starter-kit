using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Integration.Tests.Infrastructure;
using Npgsql;

namespace Integration.Tests.Tests.PAF;

/// <summary>Cadastro mínimo de uma unidade nova, isolada das demais execuções.</summary>
internal sealed record CadastroPaf(Guid UnidadeId, Guid FilaId, Guid PrioridadeId, Guid GuicheA, Guid GuicheB);

/// <summary>
/// Direct access to PafDbContext against the Testcontainers PostgreSQL, in the root tenant.
/// Each scope owns its own connection, so two scopes are two real, concurrent transactions.
/// </summary>
internal sealed class PafBanco(FshWebApplicationFactory factory)
{
    private AppTenantInfo? _root;

    public async Task InicializarAsync()
    {
        using var scope = factory.Services.CreateScope();
        _root = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        _root.ShouldNotBeNull();
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal, and a value set in a sync method
    // flows back to the caller, while one set inside an awaited async helper would be lost.
    public PafDbContext Contexto(IServiceScope scope, Guid? unidadeCorrente)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(_root ?? throw new InvalidOperationException("InicializarAsync não foi chamado."));
        if (unidadeCorrente is { } unidadeId)
        {
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidadeId);
        }

        return scope.ServiceProvider.GetRequiredService<PafDbContext>();
    }

    public async Task<CadastroPaf> CriarCadastroAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);

        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var fila = Fila.Criar(unidade.Id, "Caixa", "Caixa", setorId: null);
        var prioridade = Prioridade.Criar(unidade.Id, "Normal", valor: 0);
        var guicheA = Estacao.Criar(unidade.Id, "Guichê 1", TipoEstacao.Guiche);
        var guicheB = Estacao.Criar(unidade.Id, "Guichê 2", TipoEstacao.Guiche);

        db.Unidades.Add(unidade);
        db.Filas.Add(fila);
        db.Prioridades.Add(prioridade);
        db.Estacoes.AddRange(guicheA, guicheB);
        db.FilaPrioridades.Add(FilaPrioridade.Criar(unidade.Id, fila.Id, prioridade.Id, "A", numeroInicial: 1, ordem: 1));
        await db.SaveChangesAsync();

        return new CadastroPaf(unidade.Id, fila.Id, prioridade.Id, guicheA.Id, guicheB.Id);
    }

    public static Ficha NovaFicha(CadastroPaf c, int numero, Guid estacaoId, string chave) =>
        Ficha.Criar(c.UnidadeId, c.FilaId, c.PrioridadeId, "A", numero, CanalEmissao.Totem, estacaoId, chave);

    public async Task<(Ficha Ficha, Atendimento Atendimento)> EmitirAsync(CadastroPaf c, int numero)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var ficha = NovaFicha(c, numero, c.GuicheA, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(c.UnidadeId, ficha.Id, c.FilaId, c.PrioridadeId, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        return (ficha, atendimento);
    }

    public async Task<Atendimento> NovoAtendimentoAsync(CadastroPaf c, Guid fichaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var atendimento = Atendimento.Criar(c.UnidadeId, fichaId, c.FilaId, c.PrioridadeId, atendimentoOrigemId: null);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        return atendimento;
    }

    // Same shape the call path will use: state, station and mark written by one UPDATE stamped by now().
    public static Task<int> ChamarAsync(PafDbContext db, Guid atendimentoId, Guid estacaoId) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Chamado', "EstacaoId" = {estacaoId}, "ChamadoEm" = now(), "Versao" = "Versao" + 1
             WHERE "Id" = {atendimentoId}
            """);

    public static Task<int> EncerrarAsync(PafDbContext db, Guid atendimentoId) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Encerrado', "EncerradoEm" = now(), "Versao" = "Versao" + 1
             WHERE "Id" = {atendimentoId}
            """);

    /// <summary>Runs one write in its own scope and transaction; null on commit, the PostgresException otherwise.</summary>
    public Task<PostgresException?> TentarAsync(Func<PafDbContext, Task> escrita) =>
        TentarAsync(escrita, largada: null);

    /// <summary>
    /// Concurrent variant: every attempt waits on <paramref name="largada"/> before writing, then holds its
    /// transaction open long enough for the rival to block on the same index entry.
    /// </summary>
    public async Task<PostgresException?> TentarAsync(Func<PafDbContext, Task> escrita, Largada? largada)
    {
        using var scope = factory.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            if (largada is not null)
            {
                await largada.ChegarAsync();
            }

            await escrita(db);
            if (largada is not null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            await tx.CommitAsync();
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return pg;
        }
        catch (PostgresException pg)
        {
            return pg;
        }
    }
}

/// <summary>Releases all participants together once every one of them has arrived.</summary>
internal sealed class Largada(int participantes)
{
    private readonly TaskCompletionSource _todos = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _chegaram;

    public Task ChegarAsync()
    {
        if (Interlocked.Increment(ref _chegaram) == participantes)
        {
            _todos.SetResult();
        }

        return _todos.Task;
    }
}
