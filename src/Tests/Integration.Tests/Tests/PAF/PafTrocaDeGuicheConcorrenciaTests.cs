using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Switching guichê while the same session calls the next ficha (seção 5, seção 8 item 1). Without a shared lock
/// the switch reads "nothing in progress", the call commits a Chamado, and the switch then releases the guichê
/// with that atendimento hanging on it. The test holds the link row itself so both requests line up behind it
/// in a known order, then lets them go.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafTrocaDeGuicheConcorrenciaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);

    private readonly PafTenantCliente _tenant = new(factory, "paf-troca");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task TrocaDeGuicheEChamada_Simultaneas_NuncaSoltamGuicheComAtendimentoChamado()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var vinculoId = await VinculoAtivoAsync(c.GuicheA);

        // The switch queues first, then the call.
        var (respostaTroca, respostaChamada) = await EnfileirarAtrasDaTravaAsync(
            vinculoId,
            () => atendente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheB }),
            () => PafOperacao.ChamarAsync(atendente));
        using (respostaTroca)
        using (respostaChamada)
        {
            // The invariant, whatever the order: a released guichê A holds no called or started atendimento.
            var pendurados = await _op.ContarAsync(c, db =>
                from a in db.Atendimentos
                where a.EstacaoId == c.GuicheA && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
                from v in db.SessoesEstacao
                where v.Id == vinculoId && v.LiberadoEm != null
                select a.Id);
            pendurados.ShouldBe(0, $"troca={respostaTroca.StatusCode} chamada={respostaChamada.StatusCode}");

            // The switch was first in line: it releases guichê A, and the call finds no guichê.
            respostaTroca.StatusCode.ShouldBe(HttpStatusCode.NoContent, await respostaTroca.Content.ReadAsStringAsync());
            respostaChamada.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            PafSessaoLicencaTests.Detalhe(await respostaChamada.Content.ReadAsStringAsync()).ShouldBe(MensagensGuiche.MensagemSemGuiche);
            (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.Estado == EstadoAtendimento.Aguardando))).ShouldBe(1);
        }
    }

    [Fact]
    public async Task ChamadaAntesDaTroca_TrocaEsperaEVeOAtendimento()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var vinculoId = await VinculoAtivoAsync(c.GuicheA);

        var (respostaChamada, respostaTroca) = await EnfileirarAtrasDaTravaAsync(
            vinculoId,
            () => PafOperacao.ChamarAsync(atendente),
            () => atendente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheB }));
        using (respostaChamada)
        using (respostaTroca)
        {
            respostaChamada.StatusCode.ShouldBe(HttpStatusCode.OK, await respostaChamada.Content.ReadAsStringAsync());
            respostaTroca.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            PafSessaoLicencaTests.Detalhe(await respostaTroca.Content.ReadAsStringAsync())
                .ShouldBe(SetGuicheCommandHandler.MensagemAtendimentoEmCurso);
            (await VinculoAtivoAsync(c.GuicheA)).ShouldBe(vinculoId);
        }
    }

    /// <summary>
    /// Holds the link row from the test, sends <paramref name="primeira"/>, waits until it is seen waiting (or
    /// answered), then <paramref name="segunda"/>, and only then lets them go: the order they reach the lock is known.
    /// </summary>
    private async Task<(HttpResponseMessage Primeira, HttpResponseMessage Segunda)> EnfileirarAtrasDaTravaAsync(
        Guid vinculoId, Func<Task<HttpResponseMessage>> primeira, Func<Task<HttpResponseMessage>> segunda)
    {
        Task<HttpResponseMessage> tarefaPrimeira;
        Task<HttpResponseMessage> tarefaSegunda;
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, unidadeCorrente: null);
            await using var trava = await db.Database.BeginTransactionAsync();
            await db.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM paf."SessoesEstacao" WHERE "Id" = {vinculoId} FOR UPDATE
                """).ToListAsync();

            tarefaPrimeira = Task.Run(primeira);
            await EsperarAsync(() => tarefaPrimeira.IsCompleted, esperando: 1);
            tarefaSegunda = Task.Run(segunda);
            await EsperarAsync(() => tarefaPrimeira.IsCompleted || tarefaSegunda.IsCompleted, esperando: 2);

            await trava.CommitAsync();
        }

        return (await tarefaPrimeira, await tarefaSegunda);
    }


    private async Task<Guid> VinculoAtivoAsync(Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.AsNoTracking()
            .Where(v => v.EstacaoId == estacaoId && v.LiberadoEm == null)
            .Select(v => v.Id)
            .SingleAsync();
    }

    /// <summary>Until <paramref name="esperando"/> backends wait on a lock over SessoesEstacao, or the request already answered.</summary>
    private async Task EsperarAsync(Func<bool> respondeu, int esperando)
    {
        var limite = DateTime.UtcNow + Prazo;
        while (DateTime.UtcNow < limite)
        {
            if (respondeu())
            {
                return;
            }

            using var scope = factory.Services.CreateScope();
            var db = _tenant.Contexto(scope, unidadeCorrente: null);
            var bloqueados = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                 WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%SessoesEstacao%'
                """).SingleAsync();
            if (bloqueados >= esperando)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Nenhuma requisição chegou à trava do vínculo em {Prazo}.");
    }
}
