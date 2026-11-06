using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Atendimentos.EncaminharAtendimento;
using FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;
using FSH.Modules.PAF.TempoReal;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// F16, ADR 0005 consequences for the pairs that only exist since wave 1 (ARQ l.271, l.275, l.292): the RF12 cancel
/// outside the guichê, rehabilitation and forwarding, against the call and against each other. Same barrier as the
/// guichê pairs: the test's own transaction decides which operation reaches the ficha lock first.
/// </summary>
public sealed partial class PafConcorrenciaGuicheTests
{
    private static string RotaCancelarRf12(Guid atendimentoId) => $"/api/v1/paf/fichas/atendimentos/{atendimentoId}/cancelar";

    private static string RotaReabilitar(Guid atendimentoId) => $"/api/v1/paf/fichas/atendimentos/{atendimentoId}/reabilitar";

    // ─── cancelar do RF12 x chamar ───────────────────────────────────

    /// <summary>
    /// The outbox write is the last statement before each commit, so with the outbox table held the RF12 cancel stops
    /// there with the ficha rows locked: the call that arrives next must skip that ficha, never wait for it.
    /// </summary>
    [Fact]
    public async Task CancelarRf12XChamar_CancelarSeguraATrava_ChamarPulaAFichaEChamaASeguinte()
    {
        var p = await PrepararAsync();
        var seguinte = await PafOperacao.EmitirOkAsync(p.Totem, p.Cenario.Caixa, p.Cenario.Normal);
        await _op.EnvelhecerFichasAsync(p.Cenario.UnidadeId);
        var supervisor = await SupervisorRf12Async(p.Cenario.UnidadeId);
        using var chamadas = new Caixa<SenhaChamada>(p.Painel, PafHubMetodos.SenhaChamada);

        Task<HttpResponseMessage> cancelar;
        Task<HttpResponseMessage> chamar;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.ExecutarAsync($"""LOCK TABLE framework."OutboxMessages" IN EXCLUSIVE MODE""");
            cancelar = Task.Run(() => supervisor.PostAsync(RotaCancelarRf12(p.Ficha.AtendimentoId), content: null));
            await EsperarNaTravaAsync("OutboxMessages", esperando: 1, () => cancelar.IsCompleted);
            cancelar.IsCompleted.ShouldBeFalse("o cancelamento precisa estar parado com a trava da ficha");

            chamar = Task.Run(() => PafOperacao.ChamarAsync(p.Atendente));
            await EsperarNaTravaAsync("OutboxMessages", esperando: 2, () => cancelar.IsCompleted || chamar.IsCompleted);
            chamar.IsCompleted.ShouldBeFalse("a chamada já escolheu a ficha e só espera a caixa de saída, não a trava da ficha");
            cancelar.IsCompleted.ShouldBeFalse();
        }

        using (var cancelado = await cancelar.WaitAsync(Prazo))
        {
            cancelado.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelado.Content.ReadAsStringAsync());
            (await cancelado.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!
                .ShouldBe(new AtendimentoAlteradoDto(p.Ficha.AtendimentoId, nameof(EstadoAtendimento.Cancelado), 1));
        }

        using (var chamada = await chamar.WaitAsync(Prazo))
        {
            chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
            (await chamada.Content.ReadFromJsonAsync<ChamadaDto>())!.AtendimentoId
                .ShouldBe(seguinte.AtendimentoId, "a ficha mais antiga estava travada pelo cancelamento e foi pulada");
        }

        var alvo = await _op.AtendimentoAsync(p.Cenario, p.Ficha.AtendimentoId);
        alvo.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        alvo.ChamadoEm.ShouldBeNull();
        alvo.EstacaoId.ShouldBeNull();
        (await _op.AtendimentoAsync(p.Cenario, seguinte.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);
        (await AvisosNaCaixaDeSaidaAsync(nameof(SenhaChamadaPaf), p.Ficha.AtendimentoId)).ShouldBe(0);
        (await chamadas.EsperarAsync(s => s.AtendimentoId == seguinte.AtendimentoId, Prazo)).ShouldNotBeNull();
        await Task.Delay(SilencioEsperado);
        chamadas.Recebidos.ShouldNotContain(s => s.AtendimentoId == p.Ficha.AtendimentoId, "o painel nunca anuncia a senha cancelada");
    }

    /// <summary>
    /// The call holds the ficha (stopped on the Filas row the test holds, after marking the atendimento Chamado); the RF12
    /// cancel, unlike the guichê's, does not look for the guichê first, so it queues on the ficha lock and cancels the
    /// confirmed call, releasing the ficha's waiting sibling.
    /// </summary>
    [Fact]
    public async Task ChamarXCancelarRf12_ChamarSeguraATrava_CancelarEsperaECancelaNaVersao2()
    {
        var p = await PrepararAsync();
        var irmao = await AcrescentarNoProtocoloAsync(p.Cenario, p.Ficha.FichaId);
        var irmaoAntes = await _op.AtendimentoAsync(p.Cenario, irmao);
        var supervisor = await SupervisorRf12Async(p.Cenario.UnidadeId);
        using var chamadas = new Caixa<SenhaChamada>(p.Painel, PafHubMetodos.SenhaChamada);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(p.Painel, PafHubMetodos.AtendimentoAtualizado);

        Task<HttpResponseMessage> chamar;
        Task<HttpResponseMessage> cancelar;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.TravarAsync($"""SELECT "Id" AS "Value" FROM paf."Filas" WHERE "Id" = {p.Cenario.Caixa} FOR UPDATE""");
            chamar = Task.Run(() => PafOperacao.ChamarAsync(p.Atendente));
            await EsperarNaTravaAsync("UPDATE paf.\"Filas\"", esperando: 1, () => chamar.IsCompleted);

            cancelar = Task.Run(() => supervisor.PostAsync(RotaCancelarRf12(p.Ficha.AtendimentoId), content: null));
            await EsperarNaTravaAsync("FOR UPDATE", esperando: 1, () => chamar.IsCompleted || cancelar.IsCompleted);
            cancelar.IsCompleted.ShouldBeFalse("o cancelamento do RF12 espera a trava que a chamada segura");
            chamar.IsCompleted.ShouldBeFalse();
            await Task.Delay(SilencioEsperado);
            chamadas.Recebidos.ShouldBeEmpty("nada sai antes da confirmação da chamada");
        }

        using (var chamada = await chamar.WaitAsync(Prazo))
        {
            chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
            var dto = (await chamada.Content.ReadFromJsonAsync<ChamadaDto>())!;
            dto.AtendimentoId.ShouldBe(p.Ficha.AtendimentoId);
            dto.Versao.ShouldBe(1);
        }

        using (var cancelado = await cancelar.WaitAsync(Prazo))
        {
            cancelado.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelado.Content.ReadAsStringAsync());
            (await cancelado.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!
                .ShouldBe(new AtendimentoAlteradoDto(p.Ficha.AtendimentoId, nameof(EstadoAtendimento.Cancelado), 2),
                    "o cancelamento que esperou encontra a chamada confirmada, versão 1, e a incrementa");
        }

        var depois = await _op.AtendimentoAsync(p.Cenario, irmao);
        depois.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        depois.ElegivelDesde.ShouldBeGreaterThan(irmaoAntes.ElegivelDesde, "cancelar um Chamado libera o irmão");
        depois.DataOperacional.ShouldBe(irmaoAntes.DataOperacional);
        depois.CriadoEm.ShouldBe(irmaoAntes.CriadoEm);

        (await chamadas.EsperarAsync(s => s.AtendimentoId == p.Ficha.AtendimentoId && s.Versao == 1, Prazo)).ShouldNotBeNull();
        var aviso = await atualizacoes.EsperarAsync(a => a.AtendimentoId == p.Ficha.AtendimentoId, Prazo);
        aviso.ShouldNotBeNull();
        aviso.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
        aviso.Versao.ShouldBe(2);
        await ShouldBeForaDoPainelAsync(p.PainelHttp, p.Ficha.AtendimentoId);
    }

    // ─── reabilitar x chamar ─────────────────────────────────────────

    /// <summary>Guichê 2 calls the ficha's Protocolo atendimento while the Caixa one, cancelled, is being rehabilitated.</summary>
    [Fact]
    public async Task ReabilitarXChamarOutroDaFicha_ChamarConfirmadoPrimeiro_ReabilitarRecusaCitandoOGuiche()
    {
        var r = await PrepararReabilitacaoAsync();
        var (c, supervisor, _, cancelado, protocolo) = r;
        var antes = await _op.AtendimentoAsync(c, cancelado);

        Task<HttpResponseMessage> chamar;
        Task<HttpResponseMessage> reabilitar;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.TravarAsync($"""SELECT "Id" AS "Value" FROM paf."Filas" WHERE "Id" = {c.Protocolo} FOR UPDATE""");
            chamar = Task.Run(() => PafOperacao.ChamarAsync(r.GuicheB));
            await EsperarNaTravaAsync("UPDATE paf.\"Filas\"", esperando: 1, () => chamar.IsCompleted);

            reabilitar = Task.Run(() => supervisor.PostAsync(RotaReabilitar(cancelado), content: null));
            await EsperarNaTravaAsync("FOR UPDATE", esperando: 1, () => chamar.IsCompleted || reabilitar.IsCompleted);
            reabilitar.IsCompleted.ShouldBeFalse("reabilitar espera a trava da ficha que a chamada segura");
        }

        using (var chamada = await chamar.WaitAsync(Prazo))
        {
            chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
            (await chamada.Content.ReadFromJsonAsync<ChamadaDto>())!.AtendimentoId.ShouldBe(protocolo);
        }

        using (var recusa = await reabilitar.WaitAsync(Prazo))
        {
            recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict, "reabilitar perdeu e revalida sob a trava");
            PafSessaoLicencaTests.Detalhe(await recusa.Content.ReadAsStringAsync())
                .ShouldBe(ReabilitarAtendimentoDaFichaCommandHandler.MensagemEmCurso("Guichê 2"));
        }

        var agora = await _op.AtendimentoAsync(c, cancelado);
        agora.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        agora.Versao.ShouldBe(antes.Versao);
        agora.CanceladoEm.ShouldBe(antes.CanceladoEm);
        agora.ElegivelDesde.ShouldBe(antes.ElegivelDesde);
        (await AvisosNaCaixaDeSaidaAsync(nameof(AtendimentoAtualizadoPaf), cancelado)).ShouldBe(1, "só o aviso do cancelamento, nenhum de reabilitação");
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a =>
            a.EstacaoId == c.GuicheB && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)))).ShouldBe(1);
    }

    /// <summary>Rehabilitation stopped before its commit holds the ficha: the call skips it for the next ficha of Protocolo.</summary>
    [Fact]
    public async Task ReabilitarXChamarOutroDaFicha_ReabilitarSeguraATrava_ChamarPulaAFicha()
    {
        var r = await PrepararReabilitacaoAsync();
        var (c, supervisor, _, cancelado, protocolo) = r;
        var outraFicha = await FichaComAtendimentosAsync(c, numero: 2, [c.Protocolo]);
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "CriadoEm" = "CriadoEm" + interval '1 second' WHERE "Id" = {outraFicha[0]}""");

        Task<HttpResponseMessage> reabilitar;
        Task<HttpResponseMessage> chamar;
        await using (var barreira = await BarreiraAsync())
        {
            await barreira.ExecutarAsync($"""LOCK TABLE framework."OutboxMessages" IN EXCLUSIVE MODE""");
            reabilitar = Task.Run(() => supervisor.PostAsync(RotaReabilitar(cancelado), content: null));
            await EsperarNaTravaAsync("OutboxMessages", esperando: 1, () => reabilitar.IsCompleted);
            reabilitar.IsCompleted.ShouldBeFalse("reabilitar precisa estar parado com a trava da ficha");

            chamar = Task.Run(() => PafOperacao.ChamarAsync(r.GuicheB));
            await EsperarNaTravaAsync("OutboxMessages", esperando: 2, () => reabilitar.IsCompleted || chamar.IsCompleted);
            chamar.IsCompleted.ShouldBeFalse();
        }

        using (var reabilitado = await reabilitar.WaitAsync(Prazo))
        {
            reabilitado.StatusCode.ShouldBe(HttpStatusCode.OK, await reabilitado.Content.ReadAsStringAsync());
            (await reabilitado.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!
                .ShouldBe(new AtendimentoAlteradoDto(cancelado, nameof(EstadoAtendimento.Aguardando), 2));
        }

        using (var chamada = await chamar.WaitAsync(Prazo))
        {
            chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
            (await chamada.Content.ReadFromJsonAsync<ChamadaDto>())!.AtendimentoId
                .ShouldBe(outraFicha[0], "a ficha travada pela reabilitação foi pulada, mesmo tendo o atendimento mais antigo");
        }

        (await _op.AtendimentoAsync(c, cancelado)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
        (await _op.AtendimentoAsync(c, protocolo)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
    }

    // ─── cancelar do RF12 x encaminhar ───────────────────────────────

    [Fact]
    public async Task CancelarRf12XEncaminhar_CancelarConfirmadoPrimeiro_EncaminharRecebe409SemDestino()
    {
        var p = await PrepararAsync();
        var origem = await IniciarAsync(p);
        var supervisor = await SupervisorRf12Async(p.Cenario.UnidadeId);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(p.Painel, PafHubMetodos.AtendimentoAtualizado);

        var (cancelar, encaminhar) = await EnfileirarAtrasDaFichaAsync(
            p.Ficha.FichaId,
            () => supervisor.PostAsync(RotaCancelarRf12(origem), content: null),
            () => EncaminharAsync(p.Atendente, origem, [p.Cenario.Protocolo], p.Cenario.Normal));

        using (cancelar)
        using (encaminhar)
        {
            cancelar.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelar.Content.ReadAsStringAsync());
            (await cancelar.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!
                .ShouldBe(new AtendimentoAlteradoDto(origem, nameof(EstadoAtendimento.Cancelado), 3));
            encaminhar.StatusCode.ShouldBe(HttpStatusCode.Conflict, "o encaminhamento que esperou revalida sob a trava");
            PafSessaoLicencaTests.Detalhe(await encaminhar.Content.ReadAsStringAsync())
                .ShouldBe(EncaminharAtendimentoCommandHandler.MensagemSemAtendimentoIniciado);
        }

        var atendimento = await _op.AtendimentoAsync(p.Cenario, origem);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        atendimento.Versao.ShouldBe(3);
        atendimento.EncerradoEm.ShouldBeNull();
        (await _op.ContarAsync(p.Cenario, db => db.Atendimentos.Where(a => a.FichaId == p.Ficha.FichaId))).ShouldBe(1, "nenhum destino criado");
        var aviso = await atualizacoes.EsperarAsync(a => a.AtendimentoId == origem, Prazo);
        aviso.ShouldNotBeNull();
        aviso.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
        await Task.Delay(SilencioEsperado);
        atualizacoes.Recebidos.Where(a => a.AtendimentoId == origem).ShouldAllBe(a => a.Estado == nameof(EstadoAtendimento.Cancelado));
    }

    [Fact]
    public async Task CancelarRf12XEncaminhar_EncaminharConfirmadoPrimeiro_CancelarRecebe409EDestinoSegueAguardando()
    {
        var p = await PrepararAsync();
        var origem = await IniciarAsync(p);
        var supervisor = await SupervisorRf12Async(p.Cenario.UnidadeId);

        var (encaminhar, cancelar) = await EnfileirarAtrasDaFichaAsync(
            p.Ficha.FichaId,
            () => EncaminharAsync(p.Atendente, origem, [p.Cenario.Protocolo], p.Cenario.Normal),
            () => supervisor.PostAsync(RotaCancelarRf12(origem), content: null));

        AtendimentoEncaminhadoDto encaminhado;
        using (encaminhar)
        using (cancelar)
        {
            encaminhar.StatusCode.ShouldBe(HttpStatusCode.OK, await encaminhar.Content.ReadAsStringAsync());
            encaminhado = (await encaminhar.Content.ReadFromJsonAsync<AtendimentoEncaminhadoDto>())!;
            encaminhado.Versao.ShouldBe(3);
            cancelar.StatusCode.ShouldBe(HttpStatusCode.Conflict, "o cancelamento que esperou encontra a origem encerrada");
            PafSessaoLicencaTests.Detalhe(await cancelar.Content.ReadAsStringAsync()).ShouldBe(OperacaoGuiche.MensagemNaoCancelavel);
        }

        var atendimento = await _op.AtendimentoAsync(p.Cenario, origem);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Encerrado);
        atendimento.Versao.ShouldBe(3);
        atendimento.CanceladoEm.ShouldBeNull();
        var destino = await _op.AtendimentoAsync(p.Cenario, encaminhado.Destinos.ShouldHaveSingleItem().AtendimentoId);
        destino.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        destino.FilaId.ShouldBe(p.Cenario.Protocolo);
        destino.AtendimentoOrigemId.ShouldBe(origem);
        destino.Versao.ShouldBe(0);
        (await _op.ContarAsync(p.Cenario, db => db.Atendimentos.Where(a => a.FichaId == p.Ficha.FichaId && a.Estado == EstadoAtendimento.Cancelado)))
            .ShouldBe(0);
    }

    // ─── liberação depois do cancelamento do encaminhado ─────────────

    /// <summary>
    /// A ficha forwarded to Protocolo and Retaguarda, the Retaguarda copy aged into yesterday as if the lock had
    /// crossed midnight; cancelling the called Protocolo copy by RF12 releases it into today's eligibility, while
    /// DataOperacional, the statistics day, stays yesterday (seção 8 item 4).
    /// </summary>
    [Fact]
    public async Task CancelarRf12DoEncaminhadoChamado_LiberaOIrmao_DataOperacionalDeOntemIntacta()
    {
        var p = await PrepararAsync();
        var origem = await IniciarAsync(p);
        await CriarGuicheRetaguardaAsync(p.Cenario);
        var supervisor = await SupervisorRf12Async(p.Cenario.UnidadeId);
        var guicheB = await _op.AtendenteAsync(p.Cenario, p.Cenario.GuicheB);
        _clientes.Add(guicheB);

        using (var resposta = await EncaminharAsync(p.Atendente, origem, [p.Cenario.Protocolo, p.Cenario.Retaguarda], p.Cenario.Normal))
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        }

        var protocolo = (await PafOperacao.ChamarOkAsync(guicheB)).AtendimentoId;
        var retaguarda = await _op.AtendimentoAsync(p.Cenario, (await DestinoNaFilaAsync(p.Cenario, origem, p.Cenario.Retaguarda)));
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos"
               SET "CriadoEm" = "CriadoEm" - interval '1 day', "ElegivelDesde" = "ElegivelDesde" - interval '1 day'
             WHERE "Id" = {retaguarda.Id}
            """);
        var antes = await _op.AtendimentoAsync(p.Cenario, retaguarda.Id);
        antes.DataOperacional.ShouldBe(retaguarda.DataOperacional.AddDays(-1));

        using (var cancelado = await supervisor.PostAsync(RotaCancelarRf12(protocolo), content: null).WaitAsync(Prazo))
        {
            cancelado.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelado.Content.ReadAsStringAsync());
        }

        var depois = await _op.AtendimentoAsync(p.Cenario, retaguarda.Id);
        depois.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        depois.ElegivelDesde.ShouldBeGreaterThan(antes.ElegivelDesde);
        depois.DataElegibilidade.ShouldBe(await HojeNoBancoAsync(), "a liberação devolve o atendimento à elegibilidade de hoje");
        depois.DataOperacional.ShouldBe(antes.DataOperacional, "a data operacional é imutável");
        depois.CriadoEm.ShouldBe(antes.CriadoEm);
        depois.Versao.ShouldBe(antes.Versao, "liberar não é mudança de estado");
    }

    // ─── apoio do F16 ────────────────────────────────────────────────

    /// <summary>A person of the unidade with the RF12 cancel and rehabilitation permissions, logged in, no guichê.</summary>
    private async Task<HttpClient> SupervisorRf12Async(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.FichasDoDia.Cancelar, PafPermissions.FichasDoDia.Reabilitar);
        var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        _clientes.Add(cliente);
        return cliente;
    }

    /// <summary>Guichê A calls and starts the prepared ficha: Chamado is version 1, EmAtendimento version 2.</summary>
    private static async Task<Guid> IniciarAsync(Preparo p)
    {
        var chamada = await PafOperacao.ChamarOkAsync(p.Atendente);
        chamada.AtendimentoId.ShouldBe(p.Ficha.AtendimentoId);
        (await PafOperacao.AgirOkAsync(p.Atendente, chamada.AtendimentoId, "iniciar")).Versao.ShouldBe(2);
        return chamada.AtendimentoId;
    }

    private static Task<HttpResponseMessage> EncaminharAsync(HttpClient atendente, Guid atendimentoId, Guid[] filaIds, Guid prioridadeId) =>
        atendente.PostAsJsonAsync(PafOperacao.Rota(atendimentoId, "encaminhar"), new EncaminharAtendimentoRequest(filaIds, prioridadeId));

    /// <summary>A second atendimento of the ficha, waiting in Protocolo (the shape an encaminhamento leaves).</summary>
    private async Task<Guid> AcrescentarNoProtocoloAsync(CenarioOperacao c, Guid fichaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var atendimento = Atendimento.Criar(c.UnidadeId, fichaId, c.Protocolo, c.Normal, atendimentoOrigemId: null);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        return atendimento.Id;
    }

    /// <summary>One ficha with one waiting atendimento per fila given, in that order, aged past the five-second guard.</summary>
    private async Task<List<Guid>> FichaComAtendimentosAsync(CenarioOperacao c, int numero, Guid[] filas)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, filas[0], c.Normal, "A", numero, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
        var atendimentos = filas.Select(fila => Atendimento.Criar(c.UnidadeId, ficha.Id, fila, c.Normal, atendimentoOrigemId: null)).ToList();
        db.Fichas.Add(ficha);
        db.Atendimentos.AddRange(atendimentos);
        await db.SaveChangesAsync();
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        return atendimentos.Select(a => a.Id).ToList();
    }

    private sealed record PreparoReabilitacao(CenarioOperacao Cenario, HttpClient Supervisor, HttpClient GuicheB, Guid Cancelado, Guid Protocolo);

    /// <summary>Guichê 2 selected; one ficha in Caixa and Protocolo, the Caixa one cancelled by RF12 while waiting (version 1).</summary>
    private async Task<PreparoReabilitacao> PrepararReabilitacaoAsync()
    {
        var c = await _op.CriarAsync();
        var supervisor = await SupervisorRf12Async(c.UnidadeId);
        var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        _clientes.Add(guicheB);
        var ids = await FichaComAtendimentosAsync(c, 1, [c.Caixa, c.Protocolo]);
        using var resposta = await supervisor.PostAsync(RotaCancelarRf12(ids[0]), content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return new PreparoReabilitacao(c, supervisor, guicheB, ids[0], ids[1]);
    }

    /// <summary>Guichê 3 serving only Retaguarda with Normal, which gives Retaguarda/Normal coverage for the forwarding.</summary>
    private async Task CriarGuicheRetaguardaAsync(CenarioOperacao c)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var guiche = Estacao.Criar(c.UnidadeId, "Guichê 3", TipoEstacao.Guiche);
        db.Estacoes.Add(guiche);
        db.EstacaoFilas.Add(EstacaoFila.Criar(c.UnidadeId, guiche.Id, PapelEstacao.Guiche, c.Retaguarda, 0));
        db.EstacaoPrioridades.Add(EstacaoPrioridade.Criar(c.UnidadeId, guiche.Id, c.Normal, 0));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> DestinoNaFilaAsync(CenarioOperacao c, Guid origemId, Guid filaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        return await db.Atendimentos.AsNoTracking()
            .Where(a => a.AtendimentoOrigemId == origemId && a.FilaId == filaId)
            .Select(a => a.Id)
            .SingleAsync();
    }

    private async Task<DateOnly> HojeNoBancoAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Database.SqlQuery<DateOnly>($"""
            SELECT (now() AT TIME ZONE 'America/Sao_Paulo')::date AS "Value"
            """).SingleAsync();
    }
}
