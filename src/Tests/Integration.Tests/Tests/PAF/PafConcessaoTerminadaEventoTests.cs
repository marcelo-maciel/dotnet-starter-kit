using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Abstractions;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Impersonation;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Services;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Hybrid;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// S02 (ADR 0008, ARQ 4.3 "O vínculo de impersonação segue a vida da concessão"): the kit records
/// <see cref="ImpersonationGrantTerminatedIntegrationEvent"/> in the outbox in the same transaction that ends or revokes
/// the grant, exactly once per grant, and only marks the cache after the commit. The PAF consumer (S12) relies on it.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafConcessaoTerminadaEventoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(30);

    private AppTenantInfo? _root;

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        _root = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(TestConstants.RootTenantId);
        _root.ShouldNotBeNull();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Revogacao_GravaUmEvento_ComOsDadosDaConcessaoEOMotivo()
    {
        var concessao = await CriarConcessaoAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var revogada = await Servico(scope).RevokeAsync(concessao.Id, "operador-1", "Operador", "teste", CancellationToken.None);
            revogada.Status.ShouldBe(ImpersonationGrantStatus.Revoked);
        }

        var eventos = await EventosAsync(concessao.Id);
        var evento = eventos.ShouldHaveSingleItem();
        evento.Motivo.ShouldBe(ImpersonationGrantStatus.Revoked);
        evento.Jti.ShouldBe(concessao.Jti);
        evento.ImpersonatedUserId.ShouldBe(concessao.ImpersonatedUserId);
        evento.TenantId.ShouldBe(concessao.ImpersonatedTenantId, "o tenant do evento é o impersonado, onde o PAF solta o guichê");
        (await EncerradaAsync(concessao.Jti)).ShouldBeTrue();
    }

    [Fact]
    public async Task Encerramento_GravaUmEvento_ComMotivoEncerrada()
    {
        var concessao = await CriarConcessaoAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var encerrada = await Servico(scope).MarkEndedByJtiAsync(concessao.Jti, CancellationToken.None);
            encerrada.ShouldNotBeNull().Status.ShouldBe(ImpersonationGrantStatus.Ended);
        }

        var evento = (await EventosAsync(concessao.Id)).ShouldHaveSingleItem();
        evento.Motivo.ShouldBe(ImpersonationGrantStatus.Ended);
        evento.GrantId.ShouldBe(concessao.Id);
    }

    [Fact]
    public async Task ConcessaoJaTerminal_NaoGravaSegundoEvento()
    {
        var concessao = await CriarConcessaoAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var servico = Servico(scope);
            await servico.MarkEndedByJtiAsync(concessao.Jti, CancellationToken.None);
            (await servico.MarkEndedByJtiAsync(concessao.Jti, CancellationToken.None)).ShouldNotBeNull().Status.ShouldBe(ImpersonationGrantStatus.Ended);
        }

        // A fresh scope: the terminal state comes from the database, not from a tracked instance.
        using (var scope = factory.Services.CreateScope())
        {
            var revogada = await Servico(scope).RevokeAsync(concessao.Id, "operador-1", "Operador", "depois do fim", CancellationToken.None);
            revogada.Status.ShouldBe(ImpersonationGrantStatus.Ended, "concessão terminal volta como está");
        }

        (await EventosAsync(concessao.Id)).ShouldHaveSingleItem().Motivo.ShouldBe(ImpersonationGrantStatus.Ended);
    }

    [Fact]
    public async Task JtiOuIdInexistente_NaoGravaNada()
    {
        var id = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var servico = Servico(scope);

        (await servico.MarkEndedByJtiAsync(Guid.NewGuid().ToString("N"), CancellationToken.None)).ShouldBeNull();
        await Should.ThrowAsync<FSH.Framework.Core.Exceptions.NotFoundException>(
            () => servico.RevokeAsync(id, "operador-1", "Operador", "inexistente", CancellationToken.None));
        (await EventosAsync(id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CaixaDeSaidaQueFalha_DesfazATerminacao_ESemMarcaNoCache()
    {
        var concessao = await CriarConcessaoAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = Identity(scope);
            var real = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            var servico = new ImpersonationGrantService(
                db,
                scope.ServiceProvider.GetRequiredService<HybridCache>(),
                TimeProvider.System,
                new CaixaDeSaidaQueFalha(real));

            await Should.ThrowAsync<InvalidOperationException>(
                () => servico.RevokeAsync(concessao.Id, "operador-1", "Operador", "falha", CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(
                () => servico.MarkEndedByJtiAsync(concessao.Jti, CancellationToken.None));

            // The kit's EndImpersonation swallows a MarkEnded failure and goes on writing in the same scope:
            // a later SaveChanges must not persist the rolled-back termination.
            (await db.SaveChangesAsync()).ShouldBe(0, "a terminação desfeita não fica rastreada para o próximo SaveChanges");
        }

        (await EventosAsync(concessao.Id)).ShouldBeEmpty("a linha que o writer real gravou caiu com o rollback");
        (await EncerradaAsync(concessao.Jti)).ShouldBeFalse("o cache só é marcado depois do commit");

        using (var scope = factory.Services.CreateScope())
        {
            var linha = await Identity(scope).ImpersonationGrants.AsNoTracking().SingleAsync(g => g.Id == concessao.Id);
            linha.EndedAtUtc.ShouldBeNull();
            linha.RevokedAtUtc.ShouldBeNull();
        }

        // Once the outbox is healthy, the same grant still terminates with its single event.
        using (var scope = factory.Services.CreateScope())
        {
            await Servico(scope).RevokeAsync(concessao.Id, "operador-1", "Operador", "de novo", CancellationToken.None);
        }

        (await EventosAsync(concessao.Id)).ShouldHaveSingleItem().Motivo.ShouldBe(ImpersonationGrantStatus.Revoked);
    }

    [Fact]
    public async Task RevogacaoEEncerramentoSimultaneos_GravamUmEventoNoTotal()
    {
        var concessao = await CriarConcessaoAsync();

        // The test's own transaction holds the row lock, so both terminations reach the database and queue
        // behind it before either can read the row; releasing it lets them race for real.
        Task<ImpersonationGrantDto> revogar;
        Task<ImpersonationGrantDto?> encerrar;
        using (var barreiraScope = factory.Services.CreateScope())
        {
            var barreira = Identity(barreiraScope);
            await using (await barreira.Database.BeginTransactionAsync())
            {
                await barreira.Database.ExecuteSqlAsync(
                    $"""SELECT 1 FROM identity."ImpersonationGrants" WHERE "Id" = {concessao.Id} FOR UPDATE""");

                revogar = Task.Run(async () =>
                {
                    using var scope = factory.Services.CreateScope();
                    return await Servico(scope).RevokeAsync(concessao.Id, "operador-1", "Operador", "corrida", CancellationToken.None);
                });
                encerrar = Task.Run(async () =>
                {
                    using var scope = factory.Services.CreateScope();
                    return await Servico(scope).MarkEndedByJtiAsync(concessao.Jti, CancellationToken.None);
                });

                await EsperarNaTravaAsync(esperando: 2, () => revogar.IsCompleted || encerrar.IsCompleted);
                revogar.IsCompleted.ShouldBeFalse("a revogação precisa estar na fila da trava");
                encerrar.IsCompleted.ShouldBeFalse("o encerramento precisa estar na fila da trava");
            }
        }

        var revogada = await revogar.WaitAsync(Prazo);
        var encerrada = (await encerrar.WaitAsync(Prazo)).ShouldNotBeNull();
        revogada.Status.ShouldBe(encerrada.Status, "o perdedor relê a linha terminal do vencedor");

        var evento = (await EventosAsync(concessao.Id)).ShouldHaveSingleItem();
        evento.Motivo.ShouldBe(revogada.Status);
    }

    private async Task<ImpersonationGrantDto> CriarConcessaoAsync()
    {
        using var scope = factory.Services.CreateScope();
        var agora = DateTime.UtcNow;
        return await Servico(scope).CreateAsync(new CreateGrantInput(
            Jti: Guid.NewGuid().ToString("N"),
            ActorUserId: Guid.NewGuid().ToString(),
            ActorUserName: "suporte",
            ActorTenantId: TestConstants.RootTenantId,
            ImpersonatedUserId: Guid.NewGuid().ToString(),
            ImpersonatedUserName: "atendente",
            ImpersonatedTenantId: $"paf-imp-{Guid.NewGuid():N}"[..20],
            Reason: "S02",
            StartedAtUtc: agora,
            ExpiresAtUtc: agora.AddMinutes(30),
            ClientId: null,
            IpAddress: null,
            UserAgent: null), CancellationToken.None);
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal set for the caller's flow (see PafBanco).
    private IImpersonationGrantService Servico(IServiceScope scope)
    {
        DefinirRoot(scope);
        return scope.ServiceProvider.GetRequiredService<IImpersonationGrantService>();
    }

    private IdentityDbContext Identity(IServiceScope scope)
    {
        DefinirRoot(scope);
        return scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    }

    private void DefinirRoot(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(_root ?? throw new InvalidOperationException("InitializeAsync não rodou."));

    private async Task<bool> EncerradaAsync(string jti)
    {
        using var scope = factory.Services.CreateScope();
        return await Servico(scope).IsRevokedOrEndedAsync(jti, CancellationToken.None);
    }

    /// <summary>Outbox rows of the terminated-grant event for one grant, processed or not, deserialized.</summary>
    private async Task<List<ImpersonationGrantTerminatedIntegrationEvent>> EventosAsync(Guid grantId)
    {
        using var scope = factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        var serializer = scope.ServiceProvider.GetRequiredService<IEventSerializer>();
        var tipo = nameof(ImpersonationGrantTerminatedIntegrationEvent);
        var id = grantId.ToString();
        var linhas = await eventing.OutboxMessages.AsNoTracking()
            .Where(m => m.Type.Contains(tipo) && m.Payload.Contains(id))
            .ToListAsync();
        return linhas
            .Select(m => (ImpersonationGrantTerminatedIntegrationEvent)serializer.Deserialize(m.Payload, m.Type).ShouldNotBeNull())
            .ToList();
    }

    /// <summary>Until <paramref name="esperando"/> backends wait on a lock touching the grants table, or a call already answered.</summary>
    private async Task EsperarNaTravaAsync(int esperando, Func<bool> respondeu)
    {
        var limite = DateTime.UtcNow + Prazo;
        while (DateTime.UtcNow < limite)
        {
            if (respondeu())
            {
                return;
            }

            using var scope = factory.Services.CreateScope();
            var bloqueados = await Identity(scope).Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                 WHERE datname = current_database() AND pid <> pg_backend_pid()
                   AND wait_event_type = 'Lock' AND query LIKE '%ImpersonationGrants%'
                """).SingleAsync();
            if (bloqueados >= esperando)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{esperando} terminação(ões) não chegaram à trava em {Prazo}.");
    }

    /// <summary>Writes the outbox row for real, then fails: proves the row and the grant change roll back together.</summary>
    private sealed class CaixaDeSaidaQueFalha(IOutboxWriter real) : IOutboxWriter
    {
        public async Task AddAsync(IIntegrationEvent @event, CancellationToken ct = default)
        {
            await real.AddAsync(@event, ct);
            throw new InvalidOperationException("caixa de saída indisponível");
        }
    }
}
