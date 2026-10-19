using System.Diagnostics;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF04 "selecionar ficha para atendimento" (requisitos seção 4 l.714-724, ARQ l.296): the waiting list in call order and
/// the chosen call, which shares the eligibility predicate with chamar-próximo, waits for the ficha lock instead of
/// skipping it, and answers 409 with no write and no retry when another guichê got the ficha first.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafSelecaoFichaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaLista = "/api/v1/paf/guiche/fichas-em-espera";
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);

    private readonly PafTenantCliente _tenant = new(factory, "paf-sel");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    private static string RotaEscolher(Guid atendimentoId) => PafOperacao.Rota(atendimentoId, "chamar");

    // ─── lista ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lista_NaOrdemDaChamada_CadaPrimeiroEOQueChamarProximoChama(bool respeitaOrdem)
    {
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheB, selecionar: true, respeitaOrdem);
        using var totem = await _op.TotemAsync(c);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        var lista = await ListarOkAsync(guicheB);
        var esperado = respeitaOrdem
            ? new[] { "B1", "B2", "AP1", "A1" }
            : new[] { "AP1", "A1", "B1", "B2" };
        lista.Select(f => f.Senha).ShouldBe(esperado);

        foreach (var item in lista)
        {
            var primeiro = (await ListarOkAsync(guicheB))[0];
            primeiro.AtendimentoId.ShouldBe(item.AtendimentoId, "a lista relida começa pelo próximo da ordem");
            var chamada = await PafOperacao.ChamarOkAsync(guicheB);
            chamada.AtendimentoId.ShouldBe(primeiro.AtendimentoId, "o primeiro da lista é o que chamar-próximo chama");
            await PafOperacao.AgirOkAsync(guicheB, chamada.AtendimentoId, "iniciar");
            await PafOperacao.AgirOkAsync(guicheB, chamada.AtendimentoId, "encerrar");
        }
    }

    [Fact]
    public async Task Lista_SoComOQueOGuichePodeChamar()
    {
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        var caixa = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        var ontem = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await ElegivelOntemAsync(ontem.AtendimentoId);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);

        var lista = await ListarOkAsync(guicheA);

        lista.Select(f => f.AtendimentoId).ShouldBe([caixa.AtendimentoId], "fila fora do papel, ficha de ontem e ficha de menos de 5 s ficam fora");
        lista[0].Senha.ShouldBe("A1");
        lista[0].FichaId.ShouldBe(caixa.FichaId);
    }

    [Fact]
    public async Task GuicheSemAOpcao_ListaEChamadaRecusadas_SemEscrita()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        foreach (var configuracao in new[] { "{}", """{"selecionarFicha": false}""", """{"selecionarFicha": "true"}""" })
        {
            await _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Configuracao" = {configuracao}::jsonb WHERE "Id" = {c.GuicheA}""");

            using var lista = await guicheA.GetAsync(RotaLista);
            await ShouldBeConflitoAsync(lista, ChamadaDeFicha.MensagemSemSelecao);
            using var escolha = await guicheA.PostAsync(RotaEscolher(ficha.AtendimentoId), content: null);
            await ShouldBeConflitoAsync(escolha, ChamadaDeFicha.MensagemSemSelecao);
        }

        await ShouldBeIntactoAsync(c, ficha.AtendimentoId);
    }

    // ─── recusas da escolha ──────────────────────────────────────────

    [Fact]
    public async Task Escolher_FichaQueAChamadaNaoChamaria_Recusa409SemEscrita()
    {
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        var foraDoPapel = await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        var ontem = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var semPrioridade = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await ElegivelOntemAsync(ontem.AtendimentoId);
        // Guichê B still serves Caixa Preferencial, so the pair stays covered; only guichê A stops enabling it.
        await _tenant.ExecutarAsync($"""DELETE FROM paf."EstacaoPrioridades" WHERE "EstacaoId" = {c.GuicheA} AND "PrioridadeId" = {c.Preferencial}""");
        var recente = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        foreach (var atendimentoId in new[] { recente.AtendimentoId, foraDoPapel.AtendimentoId, ontem.AtendimentoId, semPrioridade.AtendimentoId })
        {
            using var resposta = await guicheA.PostAsync(RotaEscolher(atendimentoId), content: null);
            await ShouldBeConflitoAsync(resposta, ChamarFichaEscolhidaCommandHandler.MensagemNaoElegivel);
            await ShouldBeIntactoAsync(c, atendimentoId);
        }

        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.EstacaoId == c.GuicheA))).ShouldBe(0);
    }

    [Fact]
    public async Task Escolher_AtendimentoDeOutraUnidadeOuInexistente_404()
    {
        var a = await _op.CriarAsync();
        var b = await _op.CriarAsync();
        await ConfigurarAsync(a.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totemB = await _op.TotemAsync(b);
        using var guicheA = await _op.AtendenteAsync(a, a.GuicheA);
        var deB = await PafOperacao.EmitirOkAsync(totemB, b.Caixa, b.Normal);
        await _op.EnvelhecerFichasAsync(b.UnidadeId);

        using (var outraUnidade = await guicheA.PostAsync(RotaEscolher(deB.AtendimentoId), content: null))
        {
            outraUnidade.StatusCode.ShouldBe(HttpStatusCode.NotFound, await outraUnidade.Content.ReadAsStringAsync());
        }

        using (var inexistente = await guicheA.PostAsync(RotaEscolher(Guid.NewGuid()), content: null))
        {
            inexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound, await inexistente.Content.ReadAsStringAsync());
        }

        await ShouldBeIntactoAsync(b, deB.AtendimentoId);
    }

    [Fact]
    public async Task Escolher_ComAtendimentoEmCursoNoGuiche_Recusa409()
    {
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var escolhida = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var emCurso = await PafOperacao.ChamarOkAsync(guicheA);

        using (var comChamado = await guicheA.PostAsync(RotaEscolher(escolhida.AtendimentoId), content: null))
        {
            await ShouldBeConflitoAsync(comChamado, ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        await PafOperacao.AgirOkAsync(guicheA, emCurso.AtendimentoId, "iniciar");
        using (var emAtendimento = await guicheA.PostAsync(RotaEscolher(escolhida.AtendimentoId), content: null))
        {
            await ShouldBeConflitoAsync(emAtendimento, ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        await ShouldBeIntactoAsync(c, escolhida.AtendimentoId);
        await PafOperacao.AgirOkAsync(guicheA, emCurso.AtendimentoId, "encerrar");
        (await EscolherOkAsync(guicheA, escolhida.AtendimentoId)).Versao.ShouldBe(1);
    }

    [Fact]
    public async Task Escolher_FichaJaChamadaPorOutroGuiche_409SemEscrita()
    {
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(guicheB)).AtendimentoId.ShouldBe(ficha.AtendimentoId);

        using var resposta = await guicheA.PostAsync(RotaEscolher(ficha.AtendimentoId), content: null);

        await ShouldBeConflitoAsync(resposta, ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
        var atendimento = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
        atendimento.EstacaoId.ShouldBe(c.GuicheB);
        atendimento.Versao.ShouldBe(1);
    }

    [Fact]
    public async Task Escolher_FichaAtrasDeOutras_ChamaAEscolhidaComOsMarcosDoBanco()
    {
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        var primeira = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        var escolhida = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        var chamada = await EscolherOkAsync(guicheA, escolhida.AtendimentoId);

        chamada.AtendimentoId.ShouldBe(escolhida.AtendimentoId);
        chamada.Senha.ShouldBe("A1");
        chamada.Versao.ShouldBe(1);
        var atendimento = await _op.AtendimentoAsync(c, escolhida.AtendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Chamado);
        atendimento.EstacaoId.ShouldBe(c.GuicheA);
        atendimento.ChamadoEm.ShouldNotBeNull();
        await ShouldBeIntactoAsync(c, primeira.AtendimentoId);
        (await _op.ContarAsync(c, db => db.Filas.Where(f => f.Id == c.Caixa && f.UltimaChamadaEm != null))).ShouldBe(1);
    }

    // ─── concorrência ────────────────────────────────────────────────

    /// <summary>Plan F10: two guichês choosing the same ficha, 50 rounds. One Chamado; the loser gets 409 and writes nothing.</summary>
    [Fact]
    public async Task Escolher_DoisGuichesNaMesmaFicha_50Rodadas_UmChamadoEOPerdedorSemEscrita()
    {
        const int rodadas = 50;
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        await ConfigurarAsync(c.GuicheB, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        var fichas = new List<Guid>();
        for (var i = 0; i < rodadas; i++)
        {
            fichas.Add((await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal)).AtendimentoId);
        }

        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var relogio = Stopwatch.StartNew();
        var clientes = new[] { guicheA, guicheB };
        var estacoes = new[] { c.GuicheA, c.GuicheB };
        var vitorias = new int[2];
        foreach (var atendimentoId in fichas)
        {
            var largada = new Largada(2);
            var respostas = await Task.WhenAll(clientes.Select(cliente => Task.Run(async () =>
            {
                await largada.ChegarAsync();
                return await cliente.PostAsync(RotaEscolher(atendimentoId), content: null);
            }))).WaitAsync(Prazo);

            respostas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1, $"uma vitória por rodada ({atendimentoId})");
            var vencedor = Array.FindIndex(respostas, r => r.StatusCode == HttpStatusCode.OK);
            await ShouldBeConflitoAsync(respostas[1 - vencedor], ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
            var atendimento = await _op.AtendimentoAsync(c, atendimentoId);
            atendimento.Estado.ShouldBe(EstadoAtendimento.Chamado);
            atendimento.EstacaoId.ShouldBe(estacoes[vencedor]);
            atendimento.Versao.ShouldBe(1, "o perdedor não escreveu");
            vitorias[vencedor]++;
            await PafOperacao.AgirOkAsync(clientes[vencedor], atendimentoId, "iniciar");
            await PafOperacao.AgirOkAsync(clientes[vencedor], atendimentoId, "encerrar");
            foreach (var resposta in respostas)
            {
                resposta.Dispose();
            }
        }

        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(2), "a disputa não fica presa em trava");
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.Estado == EstadoAtendimento.Encerrado))).ShouldBe(rodadas);
    }

    /// <summary>The chosen call against chamar-próximo on the only ficha: exactly one Chamado, whichever wins.</summary>
    [Fact]
    public async Task EscolherXChamarProximo_NaMesmaFicha_UmChamadoSo()
    {
        const int rodadas = 20;
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);

        for (var rodada = 0; rodada < rodadas; rodada++)
        {
            var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
            await _op.EnvelhecerFichasAsync(c.UnidadeId);
            var largada = new Largada(2);
            var escolher = Task.Run(async () => { await largada.ChegarAsync(); return await guicheA.PostAsync(RotaEscolher(ficha.AtendimentoId), content: null); });
            var proximo = Task.Run(async () => { await largada.ChegarAsync(); return await PafOperacao.ChamarAsync(guicheB); });
            using var escolha = await escolher.WaitAsync(Prazo);
            using var chamada = await proximo.WaitAsync(Prazo);

            var atendimento = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
            atendimento.Estado.ShouldBe(EstadoAtendimento.Chamado);
            atendimento.Versao.ShouldBe(1);
            if (escolha.StatusCode == HttpStatusCode.OK)
            {
                chamada.StatusCode.ShouldBe(HttpStatusCode.NotFound, await chamada.Content.ReadAsStringAsync());
                atendimento.EstacaoId.ShouldBe(c.GuicheA);
                await EncerrarAsync(guicheA, ficha.AtendimentoId);
            }
            else
            {
                await ShouldBeConflitoAsync(escolha, ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
                chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
                atendimento.EstacaoId.ShouldBe(c.GuicheB);
                await EncerrarAsync(guicheB, ficha.AtendimentoId);
            }
        }
    }

    /// <summary>
    /// The chosen call waits for the ficha lock (chamar-próximo would skip) and re-validates under it. The other operation
    /// here is the test's own transaction holding a row of the ficha: released untouched, the choice goes through; cancelled
    /// meanwhile (the RF12 cancel of F04 writes the same state under the same lock), the choice is refused with no write.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Escolher_FichaTravadaPorOutraOperacao_EsperaERevalida(bool canceladaNaEspera)
    {
        var c = await _op.CriarAsync();
        await ConfigurarAsync(c.GuicheA, selecionar: true, respeitaOrdem: false);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        Task<HttpResponseMessage> escolher;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.ExecutarAsync($"""SELECT "Id" FROM paf."Atendimentos" WHERE "Id" = {ficha.AtendimentoId} FOR UPDATE""");
            escolher = Task.Run(() => guicheA.PostAsync(RotaEscolher(ficha.AtendimentoId), content: null));
            await EsperarNaTravaAsync("FOR UPDATE", () => escolher.IsCompleted);
            escolher.IsCompleted.ShouldBeFalse("a escolha espera a trava da ficha em vez de pular");

            if (canceladaNaEspera)
            {
                await barreira.ExecutarAsync($"""
                    UPDATE paf."Atendimentos" SET "Estado" = 'Cancelado', "CanceladoEm" = now(), "Versao" = "Versao" + 1
                     WHERE "Id" = {ficha.AtendimentoId}
                    """);
                await barreira.ConfirmarAsync();
            }
        }

        using var resposta = await escolher.WaitAsync(Prazo);
        var atendimento = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
        if (canceladaNaEspera)
        {
            await ShouldBeConflitoAsync(resposta, ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
            atendimento.Estado.ShouldBe(EstadoAtendimento.Cancelado);
            atendimento.Versao.ShouldBe(1, "só o cancelamento escreveu");
            atendimento.EstacaoId.ShouldBeNull();
        }
        else
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            atendimento.Estado.ShouldBe(EstadoAtendimento.Chamado);
            atendimento.EstacaoId.ShouldBe(c.GuicheA);
        }
    }

    // ─── apoio ───────────────────────────────────────────────────────

    private Task<int> ConfigurarAsync(Guid estacaoId, bool selecionar, bool respeitaOrdem)
    {
        var configuracao = $$"""{"{{ChamadaDeFicha.ChaveSelecionarFicha}}": {{(selecionar ? "true" : "false")}}, "{{ChamarProximaFichaCommandHandler.ChaveRespeitarOrdemFilas}}": {{(respeitaOrdem ? "true" : "false")}}}""";
        return _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Configuracao" = {configuracao}::jsonb WHERE "Id" = {estacaoId}""");
    }

    private Task<int> ElegivelOntemAsync(Guid atendimentoId) =>
        _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "Id" = {atendimentoId}""");

    private static async Task<List<FichaEmEsperaDto>> ListarOkAsync(HttpClient guiche)
    {
        using var resposta = await guiche.GetAsync(RotaLista);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<List<FichaEmEsperaDto>>())!;
    }

    private static async Task<ChamadaDto> EscolherOkAsync(HttpClient guiche, Guid atendimentoId)
    {
        using var resposta = await guiche.PostAsync(RotaEscolher(atendimentoId), content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;
    }

    private static async Task EncerrarAsync(HttpClient guiche, Guid atendimentoId)
    {
        await PafOperacao.AgirOkAsync(guiche, atendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(guiche, atendimentoId, "encerrar");
    }

    private static async Task ShouldBeConflitoAsync(HttpResponseMessage resposta, string mensagem)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, corpo);
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(mensagem);
    }

    private async Task ShouldBeIntactoAsync(CenarioOperacao c, Guid atendimentoId)
    {
        var atendimento = await _op.AtendimentoAsync(c, atendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        atendimento.Versao.ShouldBe(0);
        atendimento.EstacaoId.ShouldBeNull();
    }

    private async Task<Barreira> BarreiraAsync()
    {
        var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        db.Database.SetCommandTimeout(Prazo);
        var transacao = await db.Database.BeginTransactionAsync();
        return new Barreira(scope, db, transacao);
    }

    /// <summary>Until a backend waits on a lock running a statement with <paramref name="trecho"/>, or the request already answered.</summary>
    private async Task EsperarNaTravaAsync(string trecho, Func<bool> respondeu)
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
            if (bloqueados >= 1)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"A escolha não chegou à trava ({trecho}) em {Prazo}.");
    }

    /// <summary>The test's own transaction: its locks hold until it is committed or disposed (which rolls back).</summary>
    private sealed class Barreira(IServiceScope scope, PafDbContext db, IDbContextTransaction transacao) : IAsyncDisposable
    {
        public Task<int> ExecutarAsync(FormattableString sql) => db.Database.ExecuteSqlAsync(sql);

        public Task ConfirmarAsync() => transacao.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transacao.DisposeAsync();
            scope.Dispose();
        }
    }
}
