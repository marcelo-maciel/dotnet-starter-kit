using System.Diagnostics;
using System.Runtime.CompilerServices;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.EncaminharAtendimento;
using Integration.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF17 through the real host and PostgreSQL: forwarding the started atendimento to one or more filas, same
/// ficha and senha, the exclusivity between the destinations (seção 4) and the day turn (arquitetura 4.1).
/// Guichê C, created here, serves only Retaguarda/Normal, which in the base scenario has no coverage.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEncaminhamentoTests(FshWebApplicationFactory factory, ITestOutputHelper output) : IAsyncLifetime
{
    private const string RotaDestinos = "/api/v1/paf/guiche/encaminhamento/destinos";
    private static readonly TimeSpan TetoDaCorrida = TimeSpan.FromMinutes(3);

    private readonly PafTenantCliente _tenant = new(factory, "paf-enc");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    // ─── destinos ────────────────────────────────────────────────────

    [Fact]
    public async Task Destinos_FilasAtivasMenosAOrigem_SoPrioridadesAtivasComCobertura_ForaDoHorarioMarcado()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var origem = await IniciadoAsync(c, atendente, c.Caixa);

        var destinos = await DestinosAsync(atendente);
        destinos.AtendimentoId.ShouldBe(origem.AtendimentoId);
        destinos.Senha.ShouldBe(origem.Senha);
        destinos.FilaOrigemId.ShouldBe(c.Caixa);
        destinos.PrioridadeOriginalId.ShouldBe(c.Normal);
        destinos.Destinos.Select(d => d.FilaId).ShouldBe([c.Protocolo, c.Retaguarda], ignoreOrder: true);
        var protocolo = destinos.Destinos.Single(d => d.FilaId == c.Protocolo);
        protocolo.Prioridades.Select(p => p.PrioridadeId).ShouldBe([c.Normal]);
        protocolo.ForaDoHorario.ShouldBeFalse();
        destinos.Destinos.Single(d => d.FilaId == c.Retaguarda).Prioridades.ShouldBeEmpty("Retaguarda não tem guichê que atenda");

        await FecharJanelaAsync(c.Protocolo);
        await _tenant.ExecutarAsync($"""UPDATE paf."Filas" SET "Ativa" = false WHERE "Id" = {c.Retaguarda}""");
        var depois = await DestinosAsync(atendente);
        depois.Destinos.Select(d => d.FilaId).ShouldBe([c.Protocolo], "fila inativa não é destino");
        depois.Destinos.Single().ForaDoHorario.ShouldBeTrue("a janela fechada só marca, não esconde");

        await _tenant.ExecutarAsync($"""UPDATE paf."Prioridades" SET "Ativa" = false WHERE "Id" = {c.Normal}""");
        (await DestinosAsync(atendente)).Destinos.Single().Prioridades.ShouldBeEmpty("prioridade inativa não entra em fila nova");
    }

    [Fact]
    public async Task Destinos_SemAtendimentoIniciado_409_ECredencialDeEstacao401()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);

        using var soChamado = await atendente.GetAsync(RotaDestinos);
        soChamado.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await soChamado.Content.ReadAsStringAsync()).ShouldContain(EncaminharAtendimentoCommandHandler.MensagemSemAtendimentoIniciado);

        using var painel = _tenant.Cliente(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));
        using var lista = await painel.GetAsync(RotaDestinos);
        using var encaminhar = await EncaminharAsync(painel, chamada.AtendimentoId, [c.Protocolo], c.Normal);
        // The permission check looks the station id up as a user; the lookup fails and the kit answers 401.
        lista.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        encaminhar.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── encaminhar ──────────────────────────────────────────────────

    [Fact]
    public async Task Encaminhar_DuasFilas_EncerraAOrigem_CriaUmAguardandoPorDestino_MesmaFichaESenhaSemNumeroNovo()
    {
        var c = await _op.CriarAsync();
        var guicheC = await CriarGuicheRetaguardaAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var origem = await IniciadoAsync(c, atendente, c.Caixa);
        var fichasAntes = await _op.ContarAsync(c, db => db.Fichas);

        var resultado = await EncaminharOkAsync(atendente, origem.AtendimentoId, [c.Protocolo, c.Retaguarda], c.Normal);

        resultado.AtendimentoId.ShouldBe(origem.AtendimentoId);
        resultado.Versao.ShouldBe(3, "chamar 1, iniciar 2, encerrar pelo encaminhamento 3");
        resultado.Destinos.Select(d => d.FilaId).ShouldBe([c.Protocolo, c.Retaguarda]);
        var fechada = await _op.AtendimentoAsync(c, origem.AtendimentoId);
        fechada.Estado.ShouldBe(EstadoAtendimento.Encerrado);
        fechada.EncerradoEm.ShouldNotBeNull();
        foreach (var destino in resultado.Destinos)
        {
            var novo = await _op.AtendimentoAsync(c, destino.AtendimentoId);
            novo.Estado.ShouldBe(EstadoAtendimento.Aguardando);
            novo.FichaId.ShouldBe(fechada.FichaId);
            novo.FilaId.ShouldBe(destino.FilaId);
            novo.PrioridadeId.ShouldBe(c.Normal);
            novo.AtendimentoOrigemId.ShouldBe(origem.AtendimentoId);
            novo.EstacaoId.ShouldBeNull();
            novo.Versao.ShouldBe(0);
        }

        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(fichasAntes, "encaminhar não emite");
        using var outro = await _op.AtendenteAsync(c, guicheC);
        var chamadaNoDestino = await PafOperacao.ChamarOkAsync(outro);
        chamadaNoDestino.Senha.ShouldBe(origem.Senha, "o cliente segue com a senha impressa");
        chamadaNoDestino.FichaId.ShouldBe(fechada.FichaId);
    }

    [Fact]
    public async Task Encaminhar_SoChamado_409_ListaVazia400_ERepetida400_SemGravar()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);

        using var chamado = await EncaminharAsync(atendente, chamada.AtendimentoId, [c.Protocolo], c.Normal);
        chamado.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await chamado.Content.ReadAsStringAsync()).ShouldContain(EncaminharAtendimentoCommandHandler.MensagemSemAtendimentoIniciado);

        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        using var vazia = await EncaminharAsync(atendente, chamada.AtendimentoId, [], c.Normal);
        vazia.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await vazia.Content.ReadAsStringAsync()).ShouldContain(EncaminharAtendimentoCommandHandler.MensagemSemFilas);
        using var repetida = await EncaminharAsync(atendente, chamada.AtendimentoId, [c.Protocolo, c.Protocolo], c.Normal);
        repetida.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await ShouldSeguirIniciadoSemDestinosAsync(c, chamada.AtendimentoId, versao: 2);
    }

    [Fact]
    public async Task Encaminhar_DestinoIgualInativoOuDeOutraUnidade_409_SemGravar()
    {
        var c = await _op.CriarAsync();
        var outraUnidade = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var origem = await IniciadoAsync(c, atendente, c.Caixa);
        await _tenant.ExecutarAsync($"""UPDATE paf."Filas" SET "Ativa" = false WHERE "Id" = {c.Retaguarda}""");

        foreach (var destino in new[] { c.Caixa, c.Retaguarda, outraUnidade.Protocolo })
        {
            using var recusa = await EncaminharAsync(atendente, origem.AtendimentoId, [c.Protocolo, destino], c.Normal);
            recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict, $"destino {destino}");
            (await recusa.Content.ReadAsStringAsync()).ShouldContain(EncaminharAtendimentoCommandHandler.MensagemDestinoIndisponivel);
        }

        await ShouldSeguirIniciadoSemDestinosAsync(c, origem.AtendimentoId, versao: 2);
    }

    [Fact]
    public async Task Encaminhar_PrioridadeSemCoberturaForaDaIntersecaoOuInativa_409_SemGravar()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var origem = await IniciadoAsync(c, atendente, c.Caixa);

        // Retaguarda/Normal is associated but no guichê serves it; Protocolo has no Preferencial at all.
        using var semCobertura = await EncaminharAsync(atendente, origem.AtendimentoId, [c.Retaguarda], c.Normal);
        using var foraDaIntersecao = await EncaminharAsync(atendente, origem.AtendimentoId, [c.Protocolo], c.Preferencial);
        foreach (var recusa in new[] { semCobertura, foraDaIntersecao })
        {
            recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await recusa.Content.ReadAsStringAsync()).ShouldContain(EncaminharAtendimentoCommandHandler.MensagemPrioridadeForaDaIntersecao);
        }

        await _tenant.ExecutarAsync($"""UPDATE paf."Prioridades" SET "Ativa" = false WHERE "Id" = {c.Normal}""");
        using var inativa = await EncaminharAsync(atendente, origem.AtendimentoId, [c.Protocolo], c.Normal);
        inativa.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await ShouldSeguirIniciadoSemDestinosAsync(c, origem.AtendimentoId, versao: 2);
    }

    [Fact]
    public async Task Encaminhar_FilaForaDoHorario_Aceita()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var origem = await IniciadoAsync(c, atendente, c.Caixa);
        await FecharJanelaAsync(c.Protocolo);

        var resultado = await EncaminharOkAsync(atendente, origem.AtendimentoId, [c.Protocolo], c.Normal);

        (await _op.AtendimentoAsync(c, resultado.Destinos.Single().AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
    }

    [Fact]
    public async Task Encaminhar_FalhaNoInsertDoDestino_DesfazTudo_SemAviso()
    {
        var c = await _op.CriarAsync();
        await CriarGuicheRetaguardaAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var origem = await IniciadoAsync(c, atendente, c.Caixa);
        var gatilho = $"paf_f08_falha_{Guid.NewGuid():N}";

        // Fails only the Retaguarda insert, after the origin UPDATE and the Protocolo insert of the same flush.
        await _tenant.ExecutarAsync(FormattableStringFactory.Create($"""
            CREATE FUNCTION paf.{gatilho}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'falha forcada F08'; END $$;
            CREATE TRIGGER {gatilho} BEFORE INSERT ON paf."Atendimentos"
               FOR EACH ROW WHEN (NEW."FilaId" = '{c.Retaguarda}') EXECUTE FUNCTION paf.{gatilho}();
            """));
        try
        {
            using var falha = await EncaminharAsync(atendente, origem.AtendimentoId, [c.Protocolo, c.Retaguarda], c.Normal);
            falha.IsSuccessStatusCode.ShouldBeFalse(await falha.Content.ReadAsStringAsync());
        }
        finally
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"""
                DROP TRIGGER {gatilho} ON paf."Atendimentos";
                DROP FUNCTION paf.{gatilho}();
                """));
        }

        await ShouldSeguirIniciadoSemDestinosAsync(c, origem.AtendimentoId, versao: 2);
        (await AvisosDoAtendimentoAsync(origem.AtendimentoId)).ShouldBe(0, "o aviso gravado antes do commit sai com a rolagem");

        await EncaminharOkAsync(atendente, origem.AtendimentoId, [c.Protocolo, c.Retaguarda], c.Normal);
        (await AvisosDoAtendimentoAsync(origem.AtendimentoId)).ShouldBe(1, "sem o gatilho o mesmo pedido passa e avisa a origem");
    }

    [Fact]
    public async Task Encaminhar_DoisPedidosSimultaneos_UmVenceOOutro409_SemDestinoDuplicado()
    {
        var c = await _op.CriarAsync();
        await CriarGuicheRetaguardaAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var origem = await IniciadoAsync(c, atendente, c.Caixa);

        var respostas = await Task.WhenAll(
            Task.Run(() => EncaminharAsync(atendente, origem.AtendimentoId, [c.Protocolo, c.Retaguarda], c.Normal)),
            Task.Run(() => EncaminharAsync(atendente, origem.AtendimentoId, [c.Protocolo, c.Retaguarda], c.Normal)));
        try
        {
            respostas.Select(r => r.StatusCode).ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict], ignoreOrder: true);
        }
        finally
        {
            foreach (var resposta in respostas)
            {
                resposta.Dispose();
            }
        }

        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.AtendimentoOrigemId == origem.AtendimentoId))).ShouldBe(2);
    }

    // ─── exclusividade entre os destinos ─────────────────────────────

    [Fact]
    public async Task Exclusividade_DestinoEmCursoEscondeOIrmao_ELiberacaoVoltaNaOrdemDeCriacao()
    {
        var c = await _op.CriarAsync();
        var guicheC = await CriarGuicheRetaguardaAsync(c);
        using var atendenteA = await _op.AtendenteAsync(c, c.GuicheA);
        using var atendenteB = await _op.AtendenteAsync(c, c.GuicheB);
        using var atendenteC = await _op.AtendenteAsync(c, guicheC);
        var origem = await IniciadoAsync(c, atendenteA, c.Caixa);
        var resultado = await EncaminharOkAsync(atendenteA, origem.AtendimentoId, [c.Protocolo, c.Retaguarda], c.Normal);
        var noProtocolo = resultado.Destinos.Single(d => d.FilaId == c.Protocolo).AtendimentoId;
        var naRetaguarda = resultado.Destinos.Single(d => d.FilaId == c.Retaguarda).AtendimentoId;

        // Created after the forwarding, so behind it in the Retaguarda order.
        var depois1 = await InserirNaRetaguardaAsync(c, numero: 101);
        var depois2 = await InserirNaRetaguardaAsync(c, numero: 102);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        (await PafOperacao.ChamarOkAsync(atendenteB)).AtendimentoId.ShouldBe(noProtocolo);
        var enquantoEmCurso = await PafOperacao.ChamarOkAsync(atendenteC);
        enquantoEmCurso.AtendimentoId.ShouldBe(depois1, "o irmão em curso no Protocolo esconde a ficha da Retaguarda");
        await PafOperacao.AgirOkAsync(atendenteC, depois1, "cancelar");

        await PafOperacao.AgirOkAsync(atendenteB, noProtocolo, "iniciar");
        var antesDaLiberacao = await _op.AtendimentoAsync(c, naRetaguarda);
        await PafOperacao.AgirOkAsync(atendenteB, noProtocolo, "encerrar");

        var liberado = await _op.AtendimentoAsync(c, naRetaguarda);
        liberado.ElegivelDesde.ShouldBeGreaterThan(antesDaLiberacao.ElegivelDesde, "a liberação reescreve o marco de elegibilidade");
        liberado.CriadoEm.ShouldBe(antesDaLiberacao.CriadoEm);
        (await PafOperacao.ChamarOkAsync(atendenteC)).AtendimentoId.ShouldBe(naRetaguarda,
            "volta com a antiguidade da criação, à frente de quem chegou depois");
        (await _op.AtendimentoAsync(c, depois2)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
    }

    [Fact]
    public async Task Exclusividade_DoisGuichesChamandoAMesmaFicha_Em50Rodadas_UmChamadoPorFicha()
    {
        const int rodadas = 50;
        var c = await _op.CriarAsync();
        var guicheC = await CriarGuicheRetaguardaAsync(c);
        using var atendenteA = await _op.AtendenteAsync(c, c.GuicheA);
        using var atendenteB = await _op.AtendenteAsync(c, c.GuicheB);
        using var atendenteC = await _op.AtendenteAsync(c, guicheC);
        var relogio = Stopwatch.StartNew();
        var ambosRecuaram = 0;

        for (var rodada = 0; rodada < rodadas; rodada++)
        {
            var origemId = await InserirNaRetaguardaAsync(c, numero: rodada + 1);
            await _op.EnvelhecerFichasAsync(c.UnidadeId);
            (await PafOperacao.ChamarOkAsync(atendenteC)).AtendimentoId.ShouldBe(origemId);
            await PafOperacao.AgirOkAsync(atendenteC, origemId, "iniciar");
            var destinos = (await EncaminharOkAsync(atendenteC, origemId, [c.Caixa, c.Protocolo], c.Normal)).Destinos;

            var chamadas = await Task.WhenAll(
                Task.Run(() => PafOperacao.ChamarAsync(atendenteA)),
                Task.Run(() => PafOperacao.ChamarAsync(atendenteB)));
            var codigos = chamadas.Select(r => r.StatusCode).ToList();
            var corpos = string.Join(" | ", await Task.WhenAll(chamadas.Select(r => r.Content.ReadAsStringAsync())));
            foreach (var chamada in chamadas)
            {
                chamada.Dispose();
            }

            // Exclusivity is F08's invariant: never two calls of one ficha. Both guichês backing off (each holds the
            // sibling row the other needs in TravarFichaSemEsperarAsync) is a liveness gap of the call core, owned
            // by F10; the ficha must still not be lost, so the round recovers it with a sequential call.
            codigos.ShouldAllBe(s => s == HttpStatusCode.OK || s == HttpStatusCode.NotFound, $"rodada {rodada}: {corpos}");
            codigos.Count(s => s == HttpStatusCode.OK).ShouldBeLessThanOrEqualTo(1, $"rodada {rodada}: {corpos}");
            var fichaId = (await _op.AtendimentoAsync(c, origemId)).FichaId;
            var emCurso = await EmCursoDaFichaAsync(c, fichaId);
            emCurso.Count.ShouldBe(codigos.Count(s => s == HttpStatusCode.OK), $"rodada {rodada}");
            if (emCurso.Count == 0)
            {
                ambosRecuaram++;
                var recuperada = await PafOperacao.ChamarOkAsync(atendenteA);
                recuperada.FichaId.ShouldBe(fichaId, $"rodada {rodada}: a ficha não some da fila");
                emCurso = await EmCursoDaFichaAsync(c, fichaId);
            }

            // The holder ends its pass, the sibling is released and a guichê of its fila takes it (only B serves
            // Protocolo): the round leaves nothing behind.
            var vencedor = emCurso.Single().EstacaoId == c.GuicheA ? atendenteA : atendenteB;
            await PafOperacao.AgirOkAsync(vencedor, emCurso[0].Id, "cancelar");
            var irmao = destinos.Single(d => d.AtendimentoId != emCurso[0].Id);
            var tomador = irmao.FilaId == c.Protocolo ? atendenteB : atendenteA;
            (await PafOperacao.ChamarOkAsync(tomador)).AtendimentoId.ShouldBe(irmao.AtendimentoId, $"rodada {rodada}");
            await PafOperacao.AgirOkAsync(tomador, irmao.AtendimentoId, "cancelar");
            (await _op.ContarAsync(c, db => db.Atendimentos.Where(x => x.AtendimentoOrigemId == origemId))).ShouldBe(2, $"rodada {rodada}");
        }

        output.WriteLine($"rodadas em que os dois guichês recuaram: {ambosRecuaram} de {rodadas}");
        relogio.Elapsed.ShouldBeLessThan(TetoDaCorrida);
    }

    // ─── virada do dia ───────────────────────────────────────────────

    [Fact]
    public async Task Virada_FichaDeOntemEncaminhadaHoje_DestinoNasceHoje_FichaMantemOntem_EEhChamavel()
    {
        var c = await _op.CriarAsync();
        using var atendenteA = await _op.AtendenteAsync(c, c.GuicheA);
        using var atendenteB = await _op.AtendenteAsync(c, c.GuicheB);
        var origem = await IniciadoAsync(c, atendenteA, c.Caixa);

        // Emitted at 23:59:58 yesterday (São Paulo, by the database clock), forwarded now, past midnight.
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Fichas"
               SET "CriadoEm" = (((now() AT TIME ZONE 'America/Sao_Paulo')::date - 1) + time '23:59:58') AT TIME ZONE 'America/Sao_Paulo'
             WHERE "UnidadeId" = {c.UnidadeId}
            """);
        var destino = (await EncaminharOkAsync(atendenteA, origem.AtendimentoId, [c.Protocolo], c.Normal)).Destinos.Single();

        var novo = await _op.AtendimentoAsync(c, destino.AtendimentoId);
        var ficha = await FichaAsync(c, novo.FichaId);
        ficha.DataOperacional.ShouldBe(novo.DataOperacional.AddDays(-1), "a ficha é de ontem e nunca muda de dia");
        novo.DataElegibilidade.ShouldBe(novo.DataOperacional);
        var chamada = await PafOperacao.ChamarOkAsync(atendenteB);
        chamada.AtendimentoId.ShouldBe(destino.AtendimentoId, "com somente fichas da data atual ligado, o encaminhado de hoje é chamável");
        chamada.Senha.ShouldBe(origem.Senha);
    }

    [Fact]
    public async Task Virada_IrmaoCriadoOntemLiberadoHoje_ElegibilidadeHoje_DataOperacionalDeOntem()
    {
        var c = await _op.CriarAsync();
        var guicheC = await CriarGuicheRetaguardaAsync(c);
        using var atendenteA = await _op.AtendenteAsync(c, c.GuicheA);
        using var atendenteB = await _op.AtendenteAsync(c, c.GuicheB);
        using var atendenteC = await _op.AtendenteAsync(c, guicheC);
        var origem = await IniciadoAsync(c, atendenteA, c.Caixa);
        var resultado = await EncaminharOkAsync(atendenteA, origem.AtendimentoId, [c.Protocolo, c.Retaguarda], c.Normal);
        var noProtocolo = resultado.Destinos.Single(d => d.FilaId == c.Protocolo).AtendimentoId;
        var naRetaguarda = resultado.Destinos.Single(d => d.FilaId == c.Retaguarda).AtendimentoId;

        // The Retaguarda pass as if forwarded at 23:59:59 yesterday and held by the Protocolo pass over midnight.
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos"
               SET "CriadoEm" = (((now() AT TIME ZONE 'America/Sao_Paulo')::date - 1) + time '23:59:59') AT TIME ZONE 'America/Sao_Paulo',
                   "ElegivelDesde" = (((now() AT TIME ZONE 'America/Sao_Paulo')::date - 1) + time '23:59:59') AT TIME ZONE 'America/Sao_Paulo'
             WHERE "Id" = {naRetaguarda}
            """);
        var deOntem = await _op.AtendimentoAsync(c, naRetaguarda);

        (await PafOperacao.ChamarOkAsync(atendenteB)).AtendimentoId.ShouldBe(noProtocolo);
        await PafOperacao.AgirOkAsync(atendenteB, noProtocolo, "iniciar");
        await PafOperacao.AgirOkAsync(atendenteB, noProtocolo, "encerrar");

        var liberado = await _op.AtendimentoAsync(c, naRetaguarda);
        liberado.DataOperacional.ShouldBe(deOntem.DataOperacional, "a data operacional é imutável");
        liberado.DataElegibilidade.ShouldBe(deOntem.DataOperacional.AddDays(1), "a liberação traz a elegibilidade para hoje");
        (await PafOperacao.ChamarOkAsync(atendenteC)).AtendimentoId.ShouldBe(naRetaguarda);
    }

    // ─── apoio ───────────────────────────────────────────────────────

    private static async Task<DestinosEncaminhamentoDto> DestinosAsync(HttpClient atendente)
    {
        using var resposta = await atendente.GetAsync(RotaDestinos);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<DestinosEncaminhamentoDto>())!;
    }

    private static Task<HttpResponseMessage> EncaminharAsync(HttpClient atendente, Guid atendimentoId, Guid[] filaIds, Guid prioridadeId) =>
        atendente.PostAsJsonAsync(PafOperacao.Rota(atendimentoId, "encaminhar"), new EncaminharAtendimentoRequest(filaIds, prioridadeId));

    private static async Task<AtendimentoEncaminhadoDto> EncaminharOkAsync(HttpClient atendente, Guid atendimentoId, Guid[] filaIds, Guid prioridadeId)
    {
        using var resposta = await EncaminharAsync(atendente, atendimentoId, filaIds, prioridadeId);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<AtendimentoEncaminhadoDto>())!;
    }

    /// <summary>A ficha emitted at the totem, called and started on the attendant's guichê.</summary>
    private async Task<ChamadaDto> IniciadoAsync(CenarioOperacao c, HttpClient atendente, Guid fila)
    {
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, fila, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        return chamada;
    }

    /// <summary>Guichê C serving only Retaguarda with Normal, which gives Retaguarda/Normal coverage.</summary>
    private async Task<Guid> CriarGuicheRetaguardaAsync(CenarioOperacao c)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var guiche = Estacao.Criar(c.UnidadeId, "Guichê 3", TipoEstacao.Guiche);
        db.Estacoes.Add(guiche);
        db.EstacaoFilas.Add(EstacaoFila.Criar(c.UnidadeId, guiche.Id, PapelEstacao.Guiche, c.Retaguarda, 0));
        db.EstacaoPrioridades.Add(EstacaoPrioridade.Criar(c.UnidadeId, guiche.Id, c.Normal, 0));
        await db.SaveChangesAsync();
        return guiche.Id;
    }

    /// <summary>A Retaguarda ficha (no totem emits it) written directly, Aguardando.</summary>
    private async Task<Guid> InserirNaRetaguardaAsync(CenarioOperacao c, int numero)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, c.Retaguarda, c.Normal, "R", numero, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(c.UnidadeId, ficha.Id, c.Retaguarda, c.Normal, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        return atendimento.Id;
    }

    /// <summary>A one-hour window in the half of the day away from now, by the database clock: never crosses midnight.</summary>
    private Task<int> FecharJanelaAsync(Guid filaId) =>
        _tenant.ExecutarAsync($"""
            UPDATE paf."Filas"
               SET "HoraInicial" = CASE WHEN (now() AT TIME ZONE 'America/Sao_Paulo')::time < time '12:00' THEN time '13:00' ELSE time '01:00' END,
                   "HoraFinal"   = CASE WHEN (now() AT TIME ZONE 'America/Sao_Paulo')::time < time '12:00' THEN time '14:00' ELSE time '02:00' END
             WHERE "Id" = {filaId}
            """);

    private async Task ShouldSeguirIniciadoSemDestinosAsync(CenarioOperacao c, Guid atendimentoId, int versao)
    {
        var origem = await _op.AtendimentoAsync(c, atendimentoId);
        origem.Estado.ShouldBe(EstadoAtendimento.EmAtendimento);
        origem.Versao.ShouldBe(versao);
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.AtendimentoOrigemId == atendimentoId))).ShouldBe(0);
    }

    private async Task<List<Atendimento>> EmCursoDaFichaAsync(CenarioOperacao c, Guid fichaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        return await db.Atendimentos.AsNoTracking()
            .Where(a => a.FichaId == fichaId && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento))
            .ToListAsync();
    }

    private async Task<Ficha> FichaAsync(CenarioOperacao c, Guid fichaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        return await db.Fichas.AsNoTracking().SingleAsync(f => f.Id == fichaId);
    }

    /// <summary>Outbox rows that carry the atendimento id: the AtendimentoAtualizado of its state changes.</summary>
    private async Task<int> AvisosDoAtendimentoAsync(Guid atendimentoId)
    {
        using var scope = factory.Services.CreateScope();
        var texto = atendimentoId.ToString();
        var encerrado = EstadoAtendimento.Encerrado.ToString();
        return await scope.ServiceProvider.GetRequiredService<FSH.Framework.Eventing.Persistence.EventingDbContext>()
            .OutboxMessages.CountAsync(m => m.Payload.Contains(texto) && m.Payload.Contains(encerrado));
    }
}
