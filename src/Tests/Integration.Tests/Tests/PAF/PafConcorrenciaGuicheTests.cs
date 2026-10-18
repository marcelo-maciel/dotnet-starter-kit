using FSH.Framework.Eventing.Persistence;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore.Storage;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// ADR 0005 consequences for the pairs that exist in the guichê today (requisitos seção 4, RF21): two real
/// connections, the test's own transaction as the barrier that fixes which operation reaches the ficha lock
/// first, and the painel checked on the hub, in the outbox and in the state query. The pairs with the RF12
/// cancel, rehabilitation and forwarding belong to F16, after those operations exist.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafConcorrenciaGuicheTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-cc");
    private readonly List<HubConnection> _conexoes = [];
    private readonly List<HttpClient> _clientes = [];
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        foreach (var cliente in _clientes)
        {
            cliente.Dispose();
        }

        await _op.DrenarCaixaDeSaidaAsync();
    }

    // ─── rechamar x cancelar ─────────────────────────────────────────

    [Fact]
    public async Task RechamarXCancelar_CancelarConfirmadoPrimeiro_RechamarRecebe409ENenhumaRechamadaSai()
    {
        var p = await PrepararChamadaAsync();
        using var rechamadas = new Caixa<SenhaRechamada>(p.Painel, PafHubMetodos.SenhaRechamada);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(p.Painel, PafHubMetodos.AtendimentoAtualizado);

        var (cancelar, rechamar) = await EnfileirarAtrasDaFichaAsync(
            p.Chamada.FichaId,
            () => PafOperacao.AgirAsync(p.Atendente, p.Chamada.AtendimentoId, "cancelar"),
            () => PafOperacao.AgirAsync(p.Atendente, p.Chamada.AtendimentoId, "rechamar"));

        using (cancelar)
        using (rechamar)
        {
            cancelar.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelar.Content.ReadAsStringAsync());
            var cancelado = (await cancelar.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!;
            cancelado.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
            cancelado.Versao.ShouldBe(2);

            rechamar.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a rechamada que esperou o cancelamento revalida sob a trava");
            PafSessaoLicencaTests.Detalhe(await rechamar.Content.ReadAsStringAsync()).ShouldBe(MensagensGuiche.MensagemNaoChamado);
        }

        var atendimento = await _op.AtendimentoAsync(p.Cenario, p.Chamada.AtendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        atendimento.Versao.ShouldBe(2);
        atendimento.UltimaRechamadaEm.ShouldBeNull();
        (await _op.ContarAsync(p.Cenario, db => db.Rechamadas.Where(r => r.AtendimentoId == p.Chamada.AtendimentoId))).ShouldBe(0);
        (await AvisosNaCaixaDeSaidaAsync(nameof(SenhaRechamadaPaf), p.Chamada.AtendimentoId)).ShouldBe(0, "nenhum aviso de rechamada gravado");
        (await AvisosNaCaixaDeSaidaAsync(nameof(AtendimentoAtualizadoPaf), p.Chamada.AtendimentoId)).ShouldBe(1);

        var aviso = await atualizacoes.EsperarAsync(a => a.AtendimentoId == p.Chamada.AtendimentoId, Prazo);
        aviso.ShouldNotBeNull("o painel recebe o cancelamento confirmado");
        aviso.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
        aviso.Versao.ShouldBe(2);
        await Task.Delay(SilencioEsperado);
        rechamadas.Recebidos.ShouldNotContain(r => r.AtendimentoId == p.Chamada.AtendimentoId, "o painel nunca anuncia senha cancelada");
        await ShouldBeForaDoPainelAsync(p.PainelHttp, p.Chamada.AtendimentoId);
    }

    [Fact]
    public async Task RechamarXCancelar_RechamarConfirmadoPrimeiro_CancelarEsperaECancelaNaVersao3()
    {
        var p = await PrepararChamadaAsync();
        using var rechamadas = new Caixa<SenhaRechamada>(p.Painel, PafHubMetodos.SenhaRechamada);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(p.Painel, PafHubMetodos.AtendimentoAtualizado);

        var (rechamar, cancelar) = await EnfileirarAtrasDaFichaAsync(
            p.Chamada.FichaId,
            () => PafOperacao.AgirAsync(p.Atendente, p.Chamada.AtendimentoId, "rechamar"),
            () => PafOperacao.AgirAsync(p.Atendente, p.Chamada.AtendimentoId, "cancelar"));

        using (rechamar)
        using (cancelar)
        {
            rechamar.StatusCode.ShouldBe(HttpStatusCode.OK, await rechamar.Content.ReadAsStringAsync());
            (await rechamar.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!.Versao.ShouldBe(2);
            cancelar.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelar.Content.ReadAsStringAsync());
            var cancelado = (await cancelar.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!;
            cancelado.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
            cancelado.Versao.ShouldBe(3, "o cancelamento que esperou a rechamada encontra a versão 2 e a incrementa");
        }

        (await _op.AtendimentoAsync(p.Cenario, p.Chamada.AtendimentoId)).Versao.ShouldBe(3);
        (await _op.ContarAsync(p.Cenario, db => db.Rechamadas.Where(r => r.AtendimentoId == p.Chamada.AtendimentoId))).ShouldBe(1);
        (await rechamadas.EsperarAsync(r => r.AtendimentoId == p.Chamada.AtendimentoId && r.Versao == 2, Prazo)).ShouldNotBeNull();
        var aviso = await atualizacoes.EsperarAsync(a => a.AtendimentoId == p.Chamada.AtendimentoId, Prazo);
        aviso.ShouldNotBeNull();
        aviso.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
        aviso.Versao.ShouldBe(3);
        await ShouldBeForaDoPainelAsync(p.PainelHttp, p.Chamada.AtendimentoId);
    }

    // ─── chamar x cancelar ───────────────────────────────────────────

    /// <summary>
    /// The guichê cancel only reaches an atendimento already committed as Chamado on its guichê (OperacaoGuiche.TravarAsync
    /// filters before locking), so against a call still open it answers 404 at once instead of waiting; the cancel that
    /// waits for the call is the RF12 one (F16). The call held open never reaches the painel or the outbox.
    /// </summary>
    [Fact]
    public async Task ChamarXCancelar_ChamadaAbertaNaoEAnunciada_CancelarDepoisDaConfirmacaoTiraASenhaDoPainel()
    {
        var p = await PrepararAsync();
        using var chamadas = new Caixa<SenhaChamada>(p.Painel, PafHubMetodos.SenhaChamada);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(p.Painel, PafHubMetodos.AtendimentoAtualizado);

        Task<HttpResponseMessage> chamar;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.TravarAsync($"""SELECT "Id" AS "Value" FROM paf."Filas" WHERE "Id" = {p.Cenario.Caixa} FOR UPDATE""");
            chamar = Task.Run(() => PafOperacao.ChamarAsync(p.Atendente));
            await EsperarNaTravaAsync("UPDATE paf.\"Filas\"", esperando: 1, () => chamar.IsCompleted);

            // The call has marked the atendimento Chamado and holds the ficha rows, but has not committed.
            using (var cancelarCedo = await PafOperacao.AgirAsync(p.Atendente, p.Ficha.AtendimentoId, "cancelar").WaitAsync(Prazo))
            {
                cancelarCedo.StatusCode.ShouldBe(HttpStatusCode.NotFound, "o cancelamento do guichê não age sobre chamada não confirmada");
            }

            var visto = await _op.AtendimentoAsync(p.Cenario, p.Ficha.AtendimentoId);
            visto.Estado.ShouldBe(EstadoAtendimento.Aguardando);
            visto.Versao.ShouldBe(0);
            (await AvisosNaCaixaDeSaidaAsync(nameof(SenhaChamadaPaf), p.Ficha.AtendimentoId)).ShouldBe(0, "aviso de transação aberta não é visível");
            await Task.Delay(SilencioEsperado);
            chamadas.Recebidos.ShouldBeEmpty("nenhum aviso sai antes da confirmação");
            chamar.IsCompleted.ShouldBeFalse();
        }

        using (var chamada = await chamar.WaitAsync(Prazo))
        {
            chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
            (await chamada.Content.ReadFromJsonAsync<ChamadaDto>())!.Versao.ShouldBe(1);
        }

        (await chamadas.EsperarAsync(s => s.AtendimentoId == p.Ficha.AtendimentoId && s.Versao == 1, Prazo)).ShouldNotBeNull();
        var cancelado = await PafOperacao.AgirOkAsync(p.Atendente, p.Ficha.AtendimentoId, "cancelar");
        cancelado.Versao.ShouldBe(2);
        var aviso = await atualizacoes.EsperarAsync(a => a.AtendimentoId == p.Ficha.AtendimentoId, Prazo);
        aviso.ShouldNotBeNull();
        aviso.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
        aviso.Versao.ShouldBe(2);
        await ShouldBeForaDoPainelAsync(p.PainelHttp, p.Ficha.AtendimentoId);
    }

    /// <summary>Seção 4: the call never waits. A ficha with any row held by another operation is skipped for the next one.</summary>
    [Fact]
    public async Task Chamar_FichaComLinhaTravadaPorOutraOperacao_PulaParaAProximaSemEsperar()
    {
        var p = await PrepararAsync();
        var outra = await PafOperacao.EmitirOkAsync(p.Totem, p.Cenario.Caixa, p.Cenario.Normal);
        // Forwarding (F08) is the path that gives a ficha a second atendimento; written here, older than the
        // other ficha's, so it is the candidate the call meets first.
        var segundo = Atendimento.Criar(p.Cenario.UnidadeId, p.Ficha.FichaId, p.Cenario.Caixa, p.Cenario.Normal, atendimentoOrigemId: null);
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, p.Cenario.UnidadeId);
            db.Atendimentos.Add(segundo);
            await db.SaveChangesAsync();
        }

        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "CriadoEm" = "CriadoEm" - interval '1 hour' WHERE "Id" = {segundo.Id}""");
        await _op.EnvelhecerFichasAsync(p.Cenario.UnidadeId);

        await using (var barreira = await BarreiraAsync())
        {
            await barreira.TravarAsync($"""SELECT "Id" AS "Value" FROM paf."Atendimentos" WHERE "Id" = {p.Ficha.AtendimentoId} FOR UPDATE""");

            using var resposta = await PafOperacao.ChamarAsync(p.Atendente).WaitAsync(Prazo);
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!.AtendimentoId
                .ShouldBe(outra.AtendimentoId, "a ficha com linha travada é pulada, sem esperar a trava");
        }

        (await _op.AtendimentoAsync(p.Cenario, segundo.Id)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
        (await _op.AtendimentoAsync(p.Cenario, p.Ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
    }

    // ─── impasse ─────────────────────────────────────────────────────

    /// <summary>
    /// A real 40P01: the call waits on the Filas row the test holds, then the test asks for the guichê link row the
    /// call holds. The call waited first, so its deadlock check fires first and it is the one aborted; it retries in a
    /// new transaction, which queues on the link row until the test lets go, and the operator gets the call.
    /// </summary>
    [Fact]
    public async Task Chamar_ImpasseForcado_ViraNovaTentativaSemErroAoOperador()
    {
        var p = await PrepararAsync();
        using var chamadas = new Caixa<SenhaChamada>(p.Painel, PafHubMetodos.SenhaChamada);
        var impassesAntes = await ImpassesNoBancoAsync();

        Task<HttpResponseMessage> chamar;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.TravarAsync($"""SELECT "Id" AS "Value" FROM paf."Filas" WHERE "Id" = {p.Cenario.Caixa} FOR UPDATE""");
            chamar = Task.Run(() => PafOperacao.ChamarAsync(p.Atendente));
            await EsperarNaTravaAsync("UPDATE paf.\"Filas\"", esperando: 1, () => chamar.IsCompleted);

            // Closes the cycle. Throws 40P01 here if PostgreSQL picked the test instead: the test fails loudly.
            await barreira.TravarAsync($"""
                SELECT "Id" AS "Value" FROM paf."SessoesEstacao"
                 WHERE "EstacaoId" = {p.Cenario.GuicheA} AND "LiberadoEm" IS NULL
                   FOR UPDATE
                """);
            await EsperarNaTravaAsync("SessoesEstacao", esperando: 1, () => chamar.IsCompleted);
            chamar.IsCompleted.ShouldBeFalse("a nova tentativa espera a trava do vínculo que o teste ainda segura");
        }

        using (var resposta = await chamar.WaitAsync(Prazo))
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            var chamada = (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;
            chamada.AtendimentoId.ShouldBe(p.Ficha.AtendimentoId);
            chamada.Versao.ShouldBe(1, "a transação abortada não deixou incremento");
        }

        (await EsperarImpasseAsync(impassesAntes)).ShouldBeGreaterThan(impassesAntes, "o impasse aconteceu de fato no PostgreSQL");
        (await _op.ContarAsync(p.Cenario, db => db.Atendimentos.Where(a => a.EstacaoId == p.Cenario.GuicheA))).ShouldBe(1);
        (await AvisosNaCaixaDeSaidaAsync(nameof(SenhaChamadaPaf), p.Ficha.AtendimentoId)).ShouldBe(1, "o aviso da tentativa abortada foi desfeito com ela");
        (await chamadas.EsperarAsync(s => s.AtendimentoId == p.Ficha.AtendimentoId, Prazo)).ShouldNotBeNull();
        await Task.Delay(SilencioEsperado);
        chamadas.Recebidos.Where(s => s.AtendimentoId == p.Ficha.AtendimentoId).Select(s => s.EventoId).Distinct().Count()
            .ShouldBe(1, "um só aviso, entregue pelo envio direto e pela caixa de saída com o mesmo EventoId");
    }

    // ─── apoio ───────────────────────────────────────────────────────

    private sealed record Preparo(
        CenarioOperacao Cenario,
        HttpClient Totem,
        HttpClient Atendente,
        HttpClient PainelHttp,
        HubConnection Painel,
        FichaEmitidaDto Ficha);

    private sealed record PreparoChamado(CenarioOperacao Cenario, HttpClient Atendente, HttpClient PainelHttp, HubConnection Painel, ChamadaDto Chamada);

    /// <summary>One aged ficha of Caixa waiting, guichê A selected, the Caixa painel connected over the hub.</summary>
    private async Task<Preparo> PrepararAsync()
    {
        var c = await _op.CriarAsync();
        var totem = await _op.TotemAsync(c);
        var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var tokenPainel = await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var painel = await _op.ConectarAsync(tokenPainel);
        _conexoes.Add(painel);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var painelHttp = _tenant.Cliente(tokenPainel);
        _clientes.AddRange([totem, atendente, painelHttp]);
        return new Preparo(c, totem, atendente, painelHttp, painel, ficha);
    }

    private async Task<PreparoChamado> PrepararChamadaAsync()
    {
        var p = await PrepararAsync();
        var chamada = await PafOperacao.ChamarOkAsync(p.Atendente);
        chamada.Versao.ShouldBe(1);
        return new PreparoChamado(p.Cenario, p.Atendente, p.PainelHttp, p.Painel, chamada);
    }

    /// <summary>
    /// Holds every atendimento row of the ficha from the test, sends <paramref name="primeira"/>, waits until it is
    /// seen waiting, then <paramref name="segunda"/>, and only then lets go: PostgreSQL grants a contended row in the
    /// order the waiters arrived, so the first one sent is the first one confirmed.
    /// </summary>
    private async Task<(HttpResponseMessage Primeira, HttpResponseMessage Segunda)> EnfileirarAtrasDaFichaAsync(
        Guid fichaId, Func<Task<HttpResponseMessage>> primeira, Func<Task<HttpResponseMessage>> segunda)
    {
        Task<HttpResponseMessage> tarefaPrimeira;
        Task<HttpResponseMessage> tarefaSegunda;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.TravarAsync($"""
                SELECT "Id" AS "Value" FROM paf."Atendimentos" WHERE "FichaId" = {fichaId} ORDER BY "Id" FOR UPDATE
                """);
            tarefaPrimeira = Task.Run(primeira);
            await EsperarNaTravaAsync("FOR UPDATE", esperando: 1, () => tarefaPrimeira.IsCompleted);
            tarefaSegunda = Task.Run(segunda);
            await EsperarNaTravaAsync("FOR UPDATE", esperando: 2, () => tarefaPrimeira.IsCompleted || tarefaSegunda.IsCompleted);
            tarefaPrimeira.IsCompleted.ShouldBeFalse("a primeira operação precisa estar na fila da trava");
            tarefaSegunda.IsCompleted.ShouldBeFalse("a segunda operação precisa estar na fila da trava");
        }

        return (await tarefaPrimeira.WaitAsync(Prazo), await tarefaSegunda.WaitAsync(Prazo));
    }

    private async Task<Barreira> BarreiraAsync()
    {
        var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        db.Database.SetCommandTimeout(Prazo);
        var transacao = await db.Database.BeginTransactionAsync();
        return new Barreira(scope, db, transacao);
    }

    /// <summary>The test's own transaction: the locks it takes hold until it is disposed, which rolls it back.</summary>
    private sealed class Barreira(IServiceScope scope, PafDbContext db, IDbContextTransaction transacao) : IAsyncDisposable
    {
        public Task<List<Guid>> TravarAsync(FormattableString sql) => db.Database.SqlQuery<Guid>(sql).ToListAsync();

        public async ValueTask DisposeAsync()
        {
            await transacao.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>Until <paramref name="esperando"/> backends wait on a lock running a statement with <paramref name="trecho"/>, or the request already answered.</summary>
    private async Task EsperarNaTravaAsync(string trecho, int esperando, Func<bool> respondeu)
    {
        var limite = DateTime.UtcNow + Prazo;
        var padrao = $"%{trecho}%";
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
                 WHERE datname = current_database() AND pid <> pg_backend_pid()
                   AND wait_event_type = 'Lock' AND query LIKE {padrao}
                """).SingleAsync();
            if (bloqueados >= esperando)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{esperando} operação(ões) não chegaram à trava ({trecho}) em {Prazo}.");
    }

    private async Task<long> ImpassesNoBancoAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Database.SqlQuery<long>($"""
            SELECT deadlocks AS "Value" FROM pg_stat_database WHERE datname = current_database()
            """).SingleAsync();
    }

    /// <summary>pg_stat_database is flushed by the backends at most once a second: read until it moves or the deadline.</summary>
    private async Task<long> EsperarImpasseAsync(long antes)
    {
        var limite = DateTime.UtcNow + Prazo;
        long atual;
        do
        {
            atual = await ImpassesNoBancoAsync();
            if (atual > antes)
            {
                return atual;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < limite);

        return atual;
    }

    /// <summary>Outbox rows of one *Paf notice type about one atendimento, processed or not.</summary>
    private async Task<int> AvisosNaCaixaDeSaidaAsync(string tipo, Guid atendimentoId)
    {
        using var scope = factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        var id = atendimentoId.ToString();
        return await eventing.OutboxMessages.CountAsync(m => m.Type.Contains(tipo) && m.Payload.Contains(id));
    }

    /// <summary>The painel's state query, which a reconnect re-reads, shows no live entry for the cancelled atendimento.</summary>
    private static async Task ShouldBeForaDoPainelAsync(HttpClient painel, Guid atendimentoId)
    {
        using var resposta = await painel.GetAsync(PafOperacao.RotaEstadoPainel);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var estado = (await resposta.Content.ReadFromJsonAsync<EstadoPainelDto>())!;
        estado.Chamadas.Where(ch => ch.AtendimentoId == atendimentoId)
            .ShouldAllBe(ch => ch.Estado == nameof(EstadoAtendimento.Cancelado), "o painel termina sem a senha cancelada");
    }
}
