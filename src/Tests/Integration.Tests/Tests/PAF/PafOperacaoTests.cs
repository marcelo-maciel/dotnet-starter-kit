using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;
using Integration.Tests.Infrastructure;
using Mediator;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Operational flow of the first slice through the real host and PostgreSQL: emission at the totem (RF11,
/// seção 4.1) and the guichê operations (RF15, RF16, RF21, seção 4, arquitetura seção 8).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafOperacaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-op");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    // ─── emissão ─────────────────────────────────────────────────────

    [Fact]
    public async Task Emitir_NoTotem_DevolveDadosDeImpressaoECriaAtendimentoAguardando()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);

        var primeira = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var preferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        var segunda = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        primeira.Senha.ShouldBe("A1");
        primeira.Numero.ShouldBe(1);
        primeira.Fila.ShouldBe("Caixa");
        primeira.Prioridade.ShouldBe("Normal");
        primeira.Unidade.ShouldStartWith("Loja ");
        primeira.IdentificadorPublico.ShouldNotBe(Guid.Empty);
        primeira.IdentificadorPublico.ShouldNotBe(primeira.FichaId, "o QR leva o identificador opaco, nunca a chave interna");
        primeira.EmitidaEm.Offset.ShouldBe(TimeSpan.FromHours(-3), "o instante sai em America/Sao_Paulo");
        (DateTimeOffset.UtcNow - primeira.EmitidaEm).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
        preferencial.Senha.ShouldBe("AP1", "cada prioridade tem o próprio contador");
        segunda.Senha.ShouldBe("A2");

        var atendimento = await _op.AtendimentoAsync(c, primeira.AtendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        atendimento.FichaId.ShouldBe(primeira.FichaId);
        atendimento.Versao.ShouldBe(0);
    }

    [Fact]
    public async Task Emitir_EmParalelo_NumerosDistintosEConsecutivos()
    {
        const int totens = 8;
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);

        var emissoes = await Task.WhenAll(Enumerable.Range(0, totens)
            .Select(_ => Task.Run(() => PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal))));

        emissoes.Select(e => e.Numero).Order().ShouldBe(Enumerable.Range(1, totens));
        emissoes.Select(e => e.FichaId).Distinct().Count().ShouldBe(totens);
    }

    [Fact]
    public async Task Emitir_MesmaChave_DevolveAMesmaFicha_PeloKitEPeloBanco()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);

        // Kit layer: the second request with the same key is replayed.
        var chaveHttp = Guid.NewGuid().ToString("N");
        using var primeira = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, chaveHttp);
        using var repetida = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, chaveHttp);
        primeira.StatusCode.ShouldBe(HttpStatusCode.OK, await primeira.Content.ReadAsStringAsync());
        repetida.StatusCode.ShouldBe(HttpStatusCode.OK, await repetida.Content.ReadAsStringAsync());
        var original = (await primeira.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
        (await repetida.Content.ReadFromJsonAsync<FichaEmitidaDto>())!.FichaId.ShouldBe(original.FichaId);

        // Database layer, as with the cache down: the handler twice with one key, no kit filter in front.
        var chave = Guid.NewGuid().ToString("N");
        var noBanco = await EmitirPeloHandlerAsync(c, chave);
        var reenvio = await EmitirPeloHandlerAsync(c, chave);
        reenvio.FichaId.ShouldBe(noBanco.FichaId, "a violação de UX_Fichas_ChaveIdempotencia devolve a ficha gravada");
        reenvio.Senha.ShouldBe(noBanco.Senha);
        reenvio.AtendimentoId.ShouldBe(noBanco.AtendimentoId);

        var seguinte = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        seguinte.Numero.ShouldBe(noBanco.Numero + 1, "o reenvio não consumiu número");
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(3);
        (await _op.ContarAsync(c, db => db.Atendimentos)).ShouldBe(3);
    }

    [Fact]
    public async Task Emitir_CredencialDePainelOuPessoa_Recusa403()
    {
        var c = await _op.CriarAsync();
        using var painel = _tenant.Cliente(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);

        using var peloPainel = await PafOperacao.EmitirAsync(painel, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
        using var pelaPessoa = await PafOperacao.EmitirAsync(atendente, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));

        peloPainel.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        pelaPessoa.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(0);
    }

    [Fact]
    public async Task Emitir_ForaDaEstacaoInativaForaDoHorarioOuSemChave_Recusa()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);

        using var foraDoTotem = await PafOperacao.EmitirAsync(totem, c.Retaguarda, c.Normal, Guid.NewGuid().ToString("N"));
        foraDoTotem.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Detalhe(await foraDoTotem.Content.ReadAsStringAsync()).ShouldBe(EmitirFichaCommandHandler.MensagemFilaForaDaEstacao);

        using var semAssociacao = await PafOperacao.EmitirAsync(totem, c.Protocolo, c.Preferencial, Guid.NewGuid().ToString("N"));
        semAssociacao.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Detalhe(await semAssociacao.Content.ReadAsStringAsync()).ShouldBe(EmitirFichaCommandHandler.MensagemPrioridadeForaDaFila);

        using var semChave = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, chave: null);
        semChave.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // A window that cannot contain the current Sao Paulo hour, then one that covers the whole day.
        var horaLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).Hour;
        var (inicio, fim) = horaLocal < 12 ? (new TimeOnly(14, 0), new TimeOnly(15, 0)) : (new TimeOnly(2, 0), new TimeOnly(3, 0));
        await _tenant.ExecutarAsync($"""UPDATE paf."Filas" SET "HoraInicial" = {inicio}, "HoraFinal" = {fim} WHERE "Id" = {c.Caixa}""");
        using var foraDoHorario = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
        foraDoHorario.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Detalhe(await foraDoHorario.Content.ReadAsStringAsync()).ShouldBe(EmitirFichaCommandHandler.MensagemForaDoHorario);

        await _tenant.ExecutarAsync($"""UPDATE paf."Filas" SET "HoraInicial" = {new TimeOnly(0, 0)}, "HoraFinal" = {new TimeOnly(23, 59, 59)} WHERE "Id" = {c.Caixa}""");
        (await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal)).Senha.ShouldBe("A1", "a recusa anterior não consumiu número");

        await _tenant.ExecutarAsync($"""UPDATE paf."Prioridades" SET "Ativa" = false WHERE "Id" = {c.Normal}""");
        using var inativa = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
        inativa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Detalhe(await inativa.Content.ReadAsStringAsync()).ShouldBe(EmitirFichaCommandHandler.MensagemIndisponivel);
    }

    // ─── chamada ─────────────────────────────────────────────────────

    [Fact]
    public async Task Chamar_FichaRecenteOuDeOutroDia_NaoEChamada()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        await ShouldBeSemFichasAsync(atendente, "a guarda de cinco segundos protege a ficha recém-emitida");

        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "Id" = {ficha.AtendimentoId}""");
        await ShouldBeSemFichasAsync(atendente, "a data de elegibilidade de ontem fica fora da chamada");

        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = now() WHERE "Id" = {ficha.AtendimentoId}""");
        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(ficha.AtendimentoId);
    }

    [Fact]
    public async Task Chamar_DoisGuichesAoMesmoTempo_NuncaAMesmaFicha()
    {
        const int fichas = 10;
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        for (var i = 0; i < fichas; i++)
        {
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        }

        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        var chamados = new List<Guid>();
        for (var rodada = 1; rodada <= fichas && chamados.Count < fichas; rodada++)
        {
            var restantes = fichas - chamados.Count;
            var largada = new Largada(2);
            var respostas = await Task.WhenAll(
                Task.Run(async () => { await largada.ChegarAsync(); return await PafOperacao.ChamarAsync(guicheA); }),
                Task.Run(async () => { await largada.ChegarAsync(); return await PafOperacao.ChamarAsync(guicheB); }));

            var clientes = new[] { guicheA, guicheB };
            for (var i = 0; i < 2; i++)
            {
                using var resposta = respostas[i];
                if (restantes >= 2)
                {
                    resposta.StatusCode.ShouldBe(HttpStatusCode.OK, "com duas fichas na fila, os dois guichês chamam sem esperar um ao outro");
                }

                if (resposta.StatusCode == HttpStatusCode.OK)
                {
                    var chamada = (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;
                    chamados.Add(chamada.AtendimentoId);
                    await PafOperacao.AgirOkAsync(clientes[i], chamada.AtendimentoId, "iniciar");
                    await PafOperacao.AgirOkAsync(clientes[i], chamada.AtendimentoId, "encerrar");
                }
                else
                {
                    resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
                }
            }
        }

        chamados.Count.ShouldBe(fichas, "cada rodada chama ao menos uma ficha");
        chamados.Distinct().Count().ShouldBe(fichas, "nenhuma ficha chamada por dois guichês");
        await ShouldBeSemFichasAsync(guicheA, "todas as fichas foram atendidas");
    }

    [Fact]
    public async Task Chamar_CliqueDuplo_UmaChamadaSo()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        var largada = new Largada(2);
        var respostas = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await largada.ChegarAsync();
            return await PafOperacao.ChamarAsync(atendente);
        })));

        respostas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var recusada = respostas.Single(r => r.StatusCode != HttpStatusCode.OK);
        recusada.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Detalhe(await recusada.Content.ReadAsStringAsync()).ShouldBe(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.EstacaoId == c.GuicheA))).ShouldBe(1);
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.Estado == EstadoAtendimento.Aguardando))).ShouldBe(1);
        foreach (var resposta in respostas)
        {
            resposta.Dispose();
        }
    }

    [Fact]
    public async Task Chamar_PrioridadeMaiorPrimeiro_EOrdemDasFilasQuandoOGuicheRespeita()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        var preferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        // Without the option, the higher prioridade value wins over the station's fila order.
        var primeira = await PafOperacao.ChamarOkAsync(guicheB);
        primeira.AtendimentoId.ShouldBe(preferencial.AtendimentoId);
        await PafOperacao.AgirOkAsync(guicheB, primeira.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(guicheB, primeira.AtendimentoId, "encerrar");

        var outraPreferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var configuracao = $$"""{"{{ChamarProximaFichaCommandHandler.ChaveRespeitarOrdemFilas}}": true}""";
        await _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Configuracao" = {configuracao}::jsonb WHERE "Id" = {c.GuicheB}""");

        // With it, Protocolo (order 0 on guichê B) comes before Caixa (order 1), whatever the prioridade.
        var segunda = await PafOperacao.ChamarOkAsync(guicheB);
        segunda.Senha.ShouldBe("B1");
        segunda.AtendimentoId.ShouldNotBe(outraPreferencial.AtendimentoId);
    }

    [Fact]
    public async Task Guiche_TransicoesComVersaoEMarcosDoBanco_ERecusasDoFluxo()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        var chamada = await PafOperacao.ChamarOkAsync(atendente);
        chamada.Senha.ShouldBe("A1");
        chamada.Versao.ShouldBe(1);
        var id = chamada.AtendimentoId;

        await ShouldBeConflitoAsync(PafOperacao.AgirAsync(atendente, id, "encerrar"), MensagensGuiche.MensagemNaoAtendendo);
        await ShouldBeConflitoAsync(PafOperacao.ChamarAsync(atendente), ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);

        var chamadoEm = (await _op.AtendimentoAsync(c, id)).ChamadoEm;
        (await PafOperacao.AgirOkAsync(atendente, id, "rechamar")).Versao.ShouldBe(2);
        var rechamado = await _op.AtendimentoAsync(c, id);
        rechamado.ChamadoEm.ShouldBe(chamadoEm, "a rechamada nunca reescreve o marco da primeira chamada");
        rechamado.UltimaRechamadaEm.ShouldNotBeNull();
        (await _op.ContarAsync(c, db => db.Rechamadas.Where(r => r.AtendimentoId == id))).ShouldBe(1);

        (await PafOperacao.AgirOkAsync(atendente, id, "iniciar")).ShouldBe(new AtendimentoAlteradoDto(id, "EmAtendimento", 3));
        await ShouldBeConflitoAsync(PafOperacao.AgirAsync(atendente, id, "rechamar"), MensagensGuiche.MensagemNaoChamado);
        await ShouldBeConflitoAsync(PafOperacao.AgirAsync(atendente, id, "cancelar"), MensagensGuiche.MensagemNaoChamado);
        (await PafOperacao.AgirOkAsync(atendente, id, "encerrar")).ShouldBe(new AtendimentoAlteradoDto(id, "Encerrado", 4));
        var encerrado = await _op.AtendimentoAsync(c, id);
        encerrado.IniciadoEm.ShouldNotBeNull();
        encerrado.EncerradoEm!.Value.ShouldBeGreaterThanOrEqualTo(encerrado.IniciadoEm.Value);
        encerrado.UsuarioId.ShouldNotBeNull();

        var segunda = await PafOperacao.ChamarOkAsync(atendente);
        segunda.Senha.ShouldBe("A2");
        (await PafOperacao.AgirOkAsync(atendente, segunda.AtendimentoId, "cancelar")).ShouldBe(new AtendimentoAlteradoDto(segunda.AtendimentoId, "Cancelado", 2));
        (await _op.AtendimentoAsync(c, segunda.AtendimentoId)).CanceladoEm.ShouldNotBeNull();
        await ShouldBeConflitoAsync(PafOperacao.AgirAsync(atendente, segunda.AtendimentoId, "rechamar"), MensagensGuiche.MensagemNaoChamado);
        await ShouldBeConflitoAsync(PafOperacao.AgirAsync(atendente, segunda.AtendimentoId, "iniciar"), MensagensGuiche.MensagemNaoChamado);
        await ShouldBeSemFichasAsync(atendente, "a cancelada não volta sozinha para a fila");
    }

    [Fact]
    public async Task Guiche_SemGuicheOuAtendimentoDeOutroGuiche_Recusa()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var semGuiche = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        await ShouldBeConflitoAsync(PafOperacao.ChamarAsync(semGuiche), MensagensGuiche.MensagemSemGuiche);
        var chamada = await PafOperacao.ChamarOkAsync(guicheA);

        using var alheio = await PafOperacao.AgirAsync(guicheB, chamada.AtendimentoId, "cancelar");
        alheio.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Detalhe(await alheio.Content.ReadAsStringAsync()).ShouldBe(MensagensGuiche.MensagemNaoEncontrado);
        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<FichaEmitidaDto> EmitirPeloHandlerAsync(CenarioOperacao c, string chave)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new EmitirFichaCommand(c.Totem, c.Caixa, c.Normal, chave));
    }

    private static async Task ShouldBeSemFichasAsync(HttpClient atendente, string motivo)
    {
        using var resposta = await PafOperacao.ChamarAsync(atendente);
        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, motivo);
        Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(ChamarProximaFichaCommandHandler.MensagemSemFichas);
    }

    private static async Task ShouldBeConflitoAsync(Task<HttpResponseMessage> requisicao, string mensagem)
    {
        using var resposta = await requisicao;
        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, await resposta.Content.ReadAsStringAsync());
        Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(mensagem);
    }

    private static string? Detalhe(string corpo) => PafSessaoLicencaTests.Detalhe(corpo);
}
