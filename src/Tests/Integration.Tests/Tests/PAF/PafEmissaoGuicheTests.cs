using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFichaNoGuiche;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Mediator;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF11 manual emission at the guichê (F12; requisitos RF11 l.364, l.380-384, RF02 l.117 and l.121, RF04 l.185, 4.1):
/// canal Guiche and the session's guichê as the emitting station, numbered in the totem's sequence, refused without a
/// guichê, outside the window, without coverage and for another unidade's fila; the panel grid and the reprint of
/// the guichê's last ficha, which never creates a ficha.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEmissaoGuicheTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaEmitir = "/api/v1/paf/guiche/fichas";
    private const string RotaPainel = "/api/v1/paf/guiche/emissao";

    private readonly PafTenantCliente _tenant = new(factory, "paf-emg");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    // ─── emissão ─────────────────────────────────────────────────────

    [Fact]
    public async Task Emitir_CanalGuicheEEstacaoDaSessao_NumeroNaSequenciaDoTotem()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);

        var doTotem = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var doGuiche = await EmitirOkAsync(guiche, c.Caixa, c.Normal);
        var deNovoNoTotem = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        doTotem.Senha.ShouldBe("A1");
        doGuiche.Senha.ShouldBe("A2", "o guichê numera na mesma sequência do totem");
        deNovoNoTotem.Senha.ShouldBe("A3");
        doGuiche.Fila.ShouldBe("Caixa");
        doGuiche.Prioridade.ShouldBe("Normal");
        doGuiche.EmitidaEm.Offset.ShouldBe(TimeSpan.FromHours(-3));
        doGuiche.ImprimirQr.ShouldBeFalse("o QR segue desligado até a página pública existir");

        var ficha = await FichaAsync(c, doGuiche.FichaId);
        ficha.Canal.ShouldBe(CanalEmissao.Guiche);
        ficha.EstacaoId.ShouldBe(c.GuicheA, "a estação emissora é o guichê do vínculo da sessão");
        (await FichaAsync(c, doTotem.FichaId)).Canal.ShouldBe(CanalEmissao.Totem);
        var atendimento = await _op.AtendimentoAsync(c, doGuiche.AtendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        atendimento.FichaId.ShouldBe(doGuiche.FichaId);
    }

    [Fact]
    public async Task Emitir_SemGuicheSelecionado_409SemFicha()
    {
        var c = await _op.CriarAsync();
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var semGuiche = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        using var resposta = await EmitirAsync(semGuiche, c.Caixa, c.Normal, Chave());
        await ShouldBeConflitoAsync(resposta, MensagensGuiche.MensagemSemGuiche);
        using var painel = await semGuiche.GetAsync(RotaPainel);
        await ShouldBeConflitoAsync(painel, MensagensGuiche.MensagemSemGuiche);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(0);
    }

    [Fact]
    public async Task Emitir_ForaDoHorario_409_EOBotaoSomeDaGrade()
    {
        var c = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        // A window that cannot contain the current Sao Paulo hour.
        var horaLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).Hour;
        var (inicio, fim) = horaLocal < 12 ? (new TimeOnly(14, 0), new TimeOnly(15, 0)) : (new TimeOnly(2, 0), new TimeOnly(3, 0));
        await _tenant.ExecutarAsync($"""UPDATE paf."Filas" SET "HoraInicial" = {inicio}, "HoraFinal" = {fim} WHERE "Id" = {c.Caixa}""");

        using var resposta = await EmitirAsync(guiche, c.Caixa, c.Normal, Chave());

        await ShouldBeConflitoAsync(resposta, EmitirFichaCommandHandler.MensagemForaDoHorario);
        var caixa = (await PainelAsync(guiche)).Filas.Single(f => f.FilaId == c.Caixa);
        caixa.ForaDoHorario.ShouldBeTrue();
        caixa.Emissivel.ShouldBeFalse();
        caixa.Ativa.ShouldBeTrue("fora do horário não é inativa");
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(0);
    }

    [Fact]
    public async Task Emitir_SemCoberturaDeGuiche_409_EAGradeNaoOfereceAPrioridade()
    {
        var c = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);

        // Retaguarda/Normal exists, but no guichê lists Retaguarda in its guichê role (C07).
        using var resposta = await EmitirAsync(guiche, c.Retaguarda, c.Normal, Chave());

        await ShouldBeConflitoAsync(resposta, CoberturaGuiche.MensagemSemCobertura);
        var painel = await PainelAsync(guiche);
        painel.Filas.Single(f => f.FilaId == c.Retaguarda).Prioridades.ShouldBeEmpty();
        painel.Filas.Single(f => f.FilaId == c.Caixa).Prioridades.Select(p => p.Letra).ShouldBe(["A", "AP"]);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(0);
    }

    [Fact]
    public async Task Emitir_FilaDeOutraUnidade_404SemFicha()
    {
        var c = await _op.CriarAsync();
        var outra = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);

        using var resposta = await EmitirAsync(guiche, outra.Caixa, outra.Normal, Chave());

        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, corpo);
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(EmitirFichaNoGuicheCommandHandler.MensagemFilaNaoEncontrada);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(0);
        (await _op.ContarAsync(outra, db => db.Fichas)).ShouldBe(0);
        (await PainelAsync(guiche)).Filas.ShouldNotContain(f => f.FilaId == outra.Caixa);
    }

    [Fact]
    public async Task Emitir_FilaInativaOuEstacaoInativaOuSemChave_Recusa()
    {
        var c = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);

        using (var semChave = await EmitirAsync(guiche, c.Caixa, c.Normal, chave: null))
        {
            semChave.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await semChave.Content.ReadAsStringAsync());
        }

        await _tenant.ExecutarAsync($"""UPDATE paf."Filas" SET "Ativa" = false WHERE "Id" = {c.Protocolo}""");
        using (var filaInativa = await EmitirAsync(guiche, c.Protocolo, c.Normal, Chave()))
        {
            await ShouldBeConflitoAsync(filaInativa, EmitirFichaCommandHandler.MensagemIndisponivel);
        }

        var protocolo = (await PainelAsync(guiche)).Filas.Single(f => f.FilaId == c.Protocolo);
        protocolo.Ativa.ShouldBeFalse("a grade mostra a fila inativa com o indicador");
        protocolo.Emissivel.ShouldBeFalse();

        await _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Ativa" = false WHERE "Id" = {c.GuicheA}""");
        using (var estacaoInativa = await EmitirAsync(guiche, c.Caixa, c.Normal, Chave()))
        {
            await ShouldBeConflitoAsync(estacaoInativa, EmitirFichaNoGuicheCommandHandler.MensagemEstacaoInativa);
        }

        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(0);
    }

    [Fact]
    public async Task Emitir_MesmaChave_DevolveAMesmaFicha_PeloKitEPeloBanco()
    {
        var c = await _op.CriarAsync();
        var atendente = await AtendenteComSessaoAsync(c, c.GuicheA);
        using var guiche = atendente.Cliente;

        // Kit layer: the second request with the same key is replayed.
        var chaveHttp = Chave();
        using var primeira = await EmitirAsync(guiche, c.Caixa, c.Normal, chaveHttp);
        using var repetida = await EmitirAsync(guiche, c.Caixa, c.Normal, chaveHttp);
        primeira.StatusCode.ShouldBe(HttpStatusCode.OK, await primeira.Content.ReadAsStringAsync());
        repetida.StatusCode.ShouldBe(HttpStatusCode.OK, await repetida.Content.ReadAsStringAsync());
        repetida.Headers.GetValues("Idempotency-Replayed").ShouldBe(["true"]);
        var original = (await primeira.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
        (await repetida.Content.ReadFromJsonAsync<FichaEmitidaDto>())!.FichaId.ShouldBe(original.FichaId);

        // Database layer, as with the cache down: the handler twice with one key, no kit filter in front.
        var chave = Chave();
        var noBanco = await EmitirPeloHandlerAsync(c, atendente, chave);
        var reenvio = await EmitirPeloHandlerAsync(c, atendente, chave);
        reenvio.FichaId.ShouldBe(noBanco.FichaId, "a violação de UX_Fichas_ChaveIdempotencia devolve a ficha gravada");
        reenvio.Senha.ShouldBe(noBanco.Senha);
        reenvio.AtendimentoId.ShouldBe(noBanco.AtendimentoId);

        var seguinte = await EmitirOkAsync(guiche, c.Caixa, c.Normal);
        seguinte.Numero.ShouldBe(noBanco.Numero + 1, "o reenvio não consumiu número");
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(3);
        (await _op.ContarAsync(c, db => db.Atendimentos)).ShouldBe(3);
    }

    [Fact]
    public async Task Emitir_TotemEGuichesEmParalelo_50NumerosDistintosEConsecutivos()
    {
        const int Rajadas = 5;
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);

        // Five bursts of ten simultaneous emissions on the same (unidade, fila, prioridade, dia) counter: five from the
        // totem (one credential: a second link would replace the device) and five from the two guichês. Ten at once is
        // the bound ADR 0005 sizes MaxTentativas for (N simultaneous emitters need at most N attempts); an emitter
        // looping with no pause can starve past it, which no totem or attendant does.
        var todas = new List<FichaEmitidaDto>();
        var doGuiche = 0;
        for (var rajada = 0; rajada < Rajadas; rajada++)
        {
            todas.AddRange(await Task.WhenAll(
                Enumerable.Range(0, 5).Select(_ => Task.Run(() => PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal)))
                    .Concat(Enumerable.Range(0, 3).Select(_ => Task.Run(() => EmitirOkAsync(guicheA, c.Caixa, c.Normal))))
                    .Concat(Enumerable.Range(0, 2).Select(_ => Task.Run(() => EmitirOkAsync(guicheB, c.Caixa, c.Normal))))));
            doGuiche += 5;
        }

        todas.Select(e => e.Numero).Order().ShouldBe(Enumerable.Range(1, Rajadas * 10));
        todas.Select(e => e.FichaId).Distinct().Count().ShouldBe(Rajadas * 10);
        (await _op.ContarAsync(c, db => db.Fichas.Where(f => f.Canal == CanalEmissao.Guiche))).ShouldBe(doGuiche);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(Rajadas * 10);
    }

    // ─── painel de emissão ───────────────────────────────────────────

    [Fact]
    public async Task Painel_ChamadaDaFilaAtualizaAUltimaChamada_FilaNuncaChamadaVazia()
    {
        var c = await _op.CriarAsync();
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        (await PainelAsync(guicheA)).Filas.ShouldAllBe(f => f.UltimaChamadaEm == null);
        await EmitirOkAsync(guicheA, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        await PafOperacao.ChamarOkAsync(guicheA);

        var painel = await PainelAsync(guicheB);
        var caixa = painel.Filas.Single(f => f.FilaId == c.Caixa);
        caixa.UltimaChamadaEm.ShouldNotBeNull();
        caixa.UltimaChamadaEm.Value.Offset.ShouldBe(TimeSpan.FromHours(-3), "data e hora em America/Sao_Paulo");
        (DateTimeOffset.UtcNow - caixa.UltimaChamadaEm.Value).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
        painel.Filas.Single(f => f.FilaId == c.Protocolo).UltimaChamadaEm.ShouldBeNull("fila nunca chamada mostra vazio");
    }

    [Theory]
    [InlineData(CatalogoParametros.OrdenacaoPorOrdem, new[] { "Protocolo", "Caixa", "Retaguarda" })]
    [InlineData(CatalogoParametros.OrdenacaoPorNome, new[] { "Caixa", "Protocolo", "Retaguarda" })]
    public async Task Painel_OrdenaPeloParametroDaUnidade(string ordenacao, string[] esperado)
    {
        var c = await _op.CriarAsync();
        // Guichê 2 calls Protocolo (order 0) then Caixa (order 1); Retaguarda is on no guichê.
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            db.Parametros.Add(Parametro.CriarDeUnidade(c.UnidadeId, CatalogoParametros.OrdenacaoFilas.Chave, ordenacao));
            await db.SaveChangesAsync();
        }

        (await PainelAsync(guicheB)).Filas.Select(f => f.NomeExibicao).ShouldBe(esperado);
    }

    [Fact]
    public async Task Painel_UltimaFichaDoGuicheParaReimprimir_SemCriarFicha()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        (await PainelAsync(guicheA)).UltimaFicha.ShouldBeNull("nenhuma ficha emitida hoje neste guichê");

        await EmitirOkAsync(guicheA, c.Caixa, c.Normal);
        var ultima = await EmitirOkAsync(guicheA, c.Caixa, c.Preferencial);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await EmitirOkAsync(guicheB, c.Protocolo, c.Normal);
        var fichasAntes = await _op.ContarAsync(c, db => db.Fichas);

        var reimpressao = (await PainelAsync(guicheA)).UltimaFicha;
        var deNovo = (await PainelAsync(guicheA)).UltimaFicha;

        reimpressao.ShouldNotBeNull();
        reimpressao.FichaId.ShouldBe(ultima.FichaId, "a última do próprio guichê, nem a do totem nem a do outro guichê");
        reimpressao.Senha.ShouldBe(ultima.Senha);
        reimpressao.Numero.ShouldBe(ultima.Numero);
        reimpressao.IdentificadorPublico.ShouldBe(ultima.IdentificadorPublico);
        reimpressao.AtendimentoId.ShouldBe(ultima.AtendimentoId);
        reimpressao.EmitidaEm.ShouldBe(ultima.EmitidaEm);
        deNovo!.FichaId.ShouldBe(ultima.FichaId);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(fichasAntes, "reimprimir não cria ficha");
    }

    [Fact]
    public async Task Painel_FichaDeOntemDoGuiche_NaoEAUltimaDeHoje()
    {
        var c = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        var ontem = await EmitirOkAsync(guiche, c.Caixa, c.Normal);
        await _tenant.ExecutarAsync($"""UPDATE paf."Fichas" SET "CriadoEm" = "CriadoEm" - interval '1 day' WHERE "Id" = {ontem.FichaId}""");

        (await PainelAsync(guiche)).UltimaFicha.ShouldBeNull("a última ficha é a de hoje (America/Sao_Paulo)");
    }

    // ─── apoio ───────────────────────────────────────────────────────

    private sealed record Atendente(HttpClient Cliente, Guid UsuarioId, Guid SessaoId);

    private static string Chave() => Guid.NewGuid().ToString("N");

    private async Task<Atendente> AtendenteComSessaoAsync(CenarioOperacao c, Guid guiche)
    {
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var token = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var cliente = _tenant.Cliente(token);
        using var selecao = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = guiche });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        return new Atendente(cliente, Guid.Parse(usuario.UserId), PafTenantCliente.SessaoDoToken(token));
    }

    private async Task<FichaEmitidaDto> EmitirPeloHandlerAsync(CenarioOperacao c, Atendente atendente, string chave)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        scope.ServiceProvider.GetRequiredService<SessaoCorrente>().Definir(_tenant.TenantId, atendente.SessaoId, atendente.UsuarioId, c.UnidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new EmitirFichaNoGuicheCommand(c.Caixa, c.Normal, chave));
    }

    private static async Task<HttpResponseMessage> EmitirAsync(HttpClient guiche, Guid fila, Guid prioridade, string? chave)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RotaEmitir);
        request.Content = JsonContent.Create(new EmitirFichaNoGuicheRequest(fila, prioridade));
        if (chave is not null)
        {
            request.Headers.Add("Idempotency-Key", chave);
        }

        return await guiche.SendAsync(request);
    }

    private static async Task<FichaEmitidaDto> EmitirOkAsync(HttpClient guiche, Guid fila, Guid prioridade)
    {
        using var resposta = await EmitirAsync(guiche, fila, prioridade, Chave());
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
    }

    private static async Task<EmissaoGuicheDto> PainelAsync(HttpClient guiche)
    {
        using var resposta = await guiche.GetAsync(RotaPainel);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<EmissaoGuicheDto>())!;
    }

    private async Task<Ficha> FichaAsync(CenarioOperacao c, Guid fichaId)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, c.UnidadeId).Fichas.AsNoTracking().SingleAsync(f => f.Id == fichaId);
    }

    private static async Task ShouldBeConflitoAsync(HttpResponseMessage resposta, string mensagem)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, corpo);
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(mensagem);
    }
}
