using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;
using FSH.Modules.PAF.Parametros;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF12 "reabilitar" (requisitos RF12, seção 4; arquitetura seção 8 item 4): Cancelado → Aguardando on the same
/// row, ending the previous eligibility cycle, only while still callable and never over a sibling in progress.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafReabilitarAtendimentoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-reab");
    private readonly List<HubConnection> _conexoes = [];
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

        await _op.DrenarCaixaDeSaidaAsync();
    }

    private static string Rota(Guid atendimentoId) => $"/api/v1/paf/fichas/atendimentos/{atendimentoId}/reabilitar";

    [Fact]
    public async Task Reabilitar_ChamadoRechamadoECancelado_LimpaOCicloPreservaCriacaoERechamadas_ESaiDoPainel()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var tokenPainel = await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        using var painelHttp = _tenant.Cliente(tokenPainel);
        var painel = await _op.ConectarAsync(tokenPainel);
        _conexoes.Add(painel);
        using var atualizacoes = new Caixa<AtendimentoAtualizado>(painel, PafHubMetodos.AtendimentoAtualizado);

        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "rechamar");
        var cancelado = await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "cancelar");
        var antes = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
        antes.ChamadoEm.ShouldNotBeNull();
        antes.UltimaRechamadaEm.ShouldNotBeNull();
        antes.CanceladoEm.ShouldNotBeNull();
        antes.UsuarioId.ShouldNotBeNull();

        var reabilitado = await ReabilitarOkAsync(supervisor, ficha.AtendimentoId);

        reabilitado.Estado.ShouldBe(nameof(EstadoAtendimento.Aguardando));
        reabilitado.Versao.ShouldBe(cancelado.Versao + 1);
        var depois = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
        depois.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        depois.Versao.ShouldBe(reabilitado.Versao);
        depois.ChamadoEm.ShouldBeNull();
        depois.UltimaRechamadaEm.ShouldBeNull();
        depois.IniciadoEm.ShouldBeNull();
        depois.CanceladoEm.ShouldBeNull();
        depois.EstacaoId.ShouldBeNull();
        depois.UsuarioId.ShouldBeNull();
        depois.CriadoEm.ShouldBe(antes.CriadoEm);
        depois.DataOperacional.ShouldBe(antes.DataOperacional);
        depois.ElegivelDesde.ShouldBeGreaterThan(antes.CanceladoEm.Value, "a espera conta da reabilitação");
        (await _op.ContarAsync(c, db => db.Rechamadas.Where(r => r.AtendimentoId == ficha.AtendimentoId))).ShouldBe(1);

        var aviso = await atualizacoes.EsperarAsync(a => a.AtendimentoId == ficha.AtendimentoId && a.Versao == reabilitado.Versao, Espera);
        aviso.ShouldNotBeNull("o painel recebe o atendimento de volta a Aguardando para tirá-lo da tela");
        aviso.Estado.ShouldBe(nameof(EstadoAtendimento.Aguardando));
        var estado = await painelHttp.GetFromJsonAsync<EstadoPainelDto>(PafOperacao.RotaEstadoPainel);
        estado!.Chamadas.ShouldNotContain(ch => ch.AtendimentoId == ficha.AtendimentoId, "a reconsulta não traz o reabilitado");

        var novaChamada = await PafOperacao.ChamarOkAsync(atendente);
        novaChamada.AtendimentoId.ShouldBe(ficha.AtendimentoId, "o filtro de candidatos volta a enxergá-lo");
        novaChamada.Versao.ShouldBe(reabilitado.Versao + 1);
        var chamadoDeNovo = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
        (chamadoDeNovo.ChamadoEm!.Value - chamadoDeNovo.ElegivelDesde).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero, "espera nunca negativa");
        chamadoDeNovo.ChamadoEm.Value.ShouldBeGreaterThan(antes.ChamadoEm.Value);
    }

    [Fact]
    public async Task Reabilitar_VoltaAFrenteDeQuemChegouDepois()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var primeira = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(primeira.AtendimentoId);
        await PafOperacao.AgirOkAsync(atendente, primeira.AtendimentoId, "cancelar");
        var segunda = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        await ReabilitarOkAsync(supervisor, primeira.AtendimentoId);

        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(primeira.AtendimentoId, "a posição vem da criação");
        (await _op.AtendimentoAsync(c, segunda.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
    }

    [Fact]
    public async Task Reabilitar_ElegivelOntemComParametroPadrao_Recusa409SemEscrever()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var atendimentoId = await CanceladoAsync(c);
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "Id" = {atendimentoId}""");
        var antes = await _op.AtendimentoAsync(c, atendimentoId);

        using var resposta = await ReabilitarAsync(supervisor, atendimentoId);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync())
            .ShouldBe(ReabilitarAtendimentoDaFichaCommandHandler.MensagemForaDoDia);
        await NadaMudouAsync(c, antes);
    }

    [Fact]
    public async Task Reabilitar_ElegivelOntemComParametroDesligado_Reabilita_DataOperacionalNaoMuda()
    {
        var c = await _op.CriarAsync();
        await GravarParametroEmpresaAsync(c.UnidadeId, "false");
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var atendimentoId = await CanceladoAsync(c);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "CriadoEm" = "CriadoEm" - interval '1 day', "ElegivelDesde" = "ElegivelDesde" - interval '1 day'
             WHERE "Id" = {atendimentoId}
            """);
        var antes = await _op.AtendimentoAsync(c, atendimentoId);

        (await ReabilitarOkAsync(supervisor, atendimentoId)).Estado.ShouldBe(nameof(EstadoAtendimento.Aguardando));

        var depois = await _op.AtendimentoAsync(c, atendimentoId);
        depois.DataOperacional.ShouldBe(antes.DataOperacional, "a data da estatística nunca se move");
        depois.CriadoEm.ShouldBe(antes.CriadoEm);
    }

    [Fact]
    public async Task Reabilitar_LiberadoDepoisDaViradaECanceladoHoje_Reabilita()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var atendimentoId = await CanceladoAsync(c);
        // Born yesterday (DataOperacional), eligible today: the shape of an atendimento released after midnight.
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "CriadoEm" = "CriadoEm" - interval '1 day' WHERE "Id" = {atendimentoId}""");
        var antes = await _op.AtendimentoAsync(c, atendimentoId);
        antes.DataOperacional.ShouldBe(antes.DataElegibilidade.AddDays(-1));

        await ReabilitarOkAsync(supervisor, atendimentoId);

        var depois = await _op.AtendimentoAsync(c, atendimentoId);
        depois.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        depois.DataOperacional.ShouldBe(antes.DataOperacional);
        depois.DataElegibilidade.ShouldBe(antes.DataElegibilidade);
    }

    [Fact]
    public async Task Reabilitar_OutroAtendimentoDaFichaEmCurso_Recusa409ComONomeDoGuiche()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        var origemId = await CanceladoAsync(c);
        var origem = await _op.AtendimentoAsync(c, origemId);
        // A second passage of the same ficha, in Protocolo, called by guichê B (which serves Protocolo first).
        Guid destinoId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            var destino = Atendimento.Criar(c.UnidadeId, origem.FichaId, c.Protocolo, c.Normal, origemId);
            db.Atendimentos.Add(destino);
            await db.SaveChangesAsync();
            destinoId = destino.Id;
        }

        (await PafOperacao.ChamarOkAsync(guicheB)).AtendimentoId.ShouldBe(destinoId);
        var antes = await _op.AtendimentoAsync(c, origemId);

        using var resposta = await ReabilitarAsync(supervisor, origemId);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync())
            .ShouldBe(ReabilitarAtendimentoDaFichaCommandHandler.MensagemEmCurso("Guichê 2"));
        await NadaMudouAsync(c, antes);
    }

    [Fact]
    public async Task Reabilitar_AguardandoChamadoOuEncerrado_Recusa409SemEscrever()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var encerrada = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, encerrada.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(atendente, encerrada.AtendimentoId, "encerrar");
        var chamada = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(chamada.AtendimentoId);
        var aguardando = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        foreach (var id in new[] { aguardando.AtendimentoId, chamada.AtendimentoId, encerrada.AtendimentoId })
        {
            var antes = await _op.AtendimentoAsync(c, id);
            using var resposta = await ReabilitarAsync(supervisor, id);
            resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, $"estado {antes.Estado}");
            PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync())
                .ShouldBe(ReabilitarAtendimentoDaFichaCommandHandler.MensagemNaoCancelado);
            await NadaMudouAsync(c, antes);
        }
    }

    [Fact]
    public async Task Reabilitar_AtendimentoDeOutraUnidade_404SemEscrever()
    {
        var a = await _op.CriarAsync();
        var b = await _op.CriarAsync();
        using var supervisorDeB = await SupervisorAsync(b.UnidadeId);
        var atendimentoId = await CanceladoAsync(a);
        var antes = await _op.AtendimentoAsync(a, atendimentoId);

        using var resposta = await ReabilitarAsync(supervisorDeB, atendimentoId);

        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await NadaMudouAsync(a, antes);
    }

    [Fact]
    public async Task Reabilitar_SemPermissao_403_EIdInexistente_404()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheB);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var atendimentoId = await CanceladoAsync(c);
        var antes = await _op.AtendimentoAsync(c, atendimentoId);

        using var semPermissao = await ReabilitarAsync(atendente, atendimentoId);
        semPermissao.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Atendimentos.Operar não inclui reabilitar");
        await NadaMudouAsync(c, antes);

        using var inexistente = await ReabilitarAsync(supervisor, Guid.NewGuid());
        inexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Emitted, called on guichê A and cancelled there (the no-show exit); guichê A is left free.</summary>
    private async Task<Guid> CanceladoAsync(CenarioOperacao c)
    {
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(ficha.AtendimentoId);
        await PafOperacao.AgirOkAsync(atendente, ficha.AtendimentoId, "cancelar");
        return ficha.AtendimentoId;
    }

    /// <summary>A person of the unidade with only the RF12 reabilitar permission, logged in, no guichê.</summary>
    private async Task<HttpClient> SupervisorAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.FichasDoDia.Reabilitar);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static Task<HttpResponseMessage> ReabilitarAsync(HttpClient cliente, Guid atendimentoId) =>
        cliente.PostAsync(Rota(atendimentoId), content: null);

    private static async Task<AtendimentoAlteradoDto> ReabilitarOkAsync(HttpClient cliente, Guid atendimentoId)
    {
        using var resposta = await ReabilitarAsync(cliente, atendimentoId);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!;
    }

    private async Task NadaMudouAsync(CenarioOperacao c, Atendimento antes)
    {
        var agora = await _op.AtendimentoAsync(c, antes.Id);
        agora.Estado.ShouldBe(antes.Estado);
        agora.Versao.ShouldBe(antes.Versao);
        agora.ElegivelDesde.ShouldBe(antes.ElegivelDesde);
        agora.ChamadoEm.ShouldBe(antes.ChamadoEm);
        agora.CanceladoEm.ShouldBe(antes.CanceladoEm);
        agora.EstacaoId.ShouldBe(antes.EstacaoId);
    }

    private async Task GravarParametroEmpresaAsync(Guid unidadeId, string valor)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeId);
        db.Parametros.Add(Parametro.CriarDeEmpresa(CatalogoParametros.SomenteFichasDaDataAtual.Chave, valor));
        await db.SaveChangesAsync();
    }
}
