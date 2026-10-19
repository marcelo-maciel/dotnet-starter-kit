using FSH.Framework.Eventing.Persistence;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF12 cancel outside the guichê (F04, requisitos seção 4, ARQ l.290-294): any non-terminal state, the ficha
/// lock, the release of the waiting siblings, the deactivated station's link and the notices after the commit.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafCancelamentoFichaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-f04");
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

    private static string Rota(Guid atendimentoId) => $"/api/v1/paf/fichas/atendimentos/{atendimentoId}/cancelar";

    [Fact]
    public async Task Aguardando_VaiParaCancelado_SemTocarOIrmao()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var (alvo, irmao) = await FichaComDoisAtendimentosAsync(c, 1);
        var irmaoAntes = await _op.AtendimentoAsync(c, irmao);

        var resposta = await CancelarOkAsync(supervisor, alvo);

        resposta.ShouldBe(new AtendimentoAlteradoDto(alvo, nameof(EstadoAtendimento.Cancelado), 1));
        var cancelado = await _op.AtendimentoAsync(c, alvo);
        cancelado.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        cancelado.CanceladoEm.ShouldNotBeNull();
        (await _op.AtendimentoAsync(c, irmao)).ShouldBeEquivalentTo(irmaoAntes, "Aguardando nunca segurou a exclusividade");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChamadoOuEmAtendimento_LiberaOIrmao_ComORelogioDoBanco_EDataOperacionalIntacta(bool iniciado)
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        var (alvo, irmao) = await FichaComDoisAtendimentosAsync(c, 1);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(guiche)).AtendimentoId.ShouldBe(alvo);
        if (iniciado)
        {
            await PafOperacao.AgirOkAsync(guiche, alvo, "iniciar");
        }

        var antes = await _op.AtendimentoAsync(c, alvo);
        var irmaoAntes = await _op.AtendimentoAsync(c, irmao);

        (await CancelarOkAsync(supervisor, alvo)).Versao.ShouldBe(antes.Versao + 1);

        var cancelado = await _op.AtendimentoAsync(c, alvo);
        cancelado.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        var liberado = await _op.AtendimentoAsync(c, irmao);
        liberado.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        liberado.ElegivelDesde.ShouldBe(cancelado.CanceladoEm!.Value, "now() da transação do cancelamento");
        liberado.ElegivelDesde.ShouldBeGreaterThan(irmaoAntes.ElegivelDesde);
        liberado.DataOperacional.ShouldBe(irmaoAntes.DataOperacional, "a data operacional é imutável");
        liberado.CriadoEm.ShouldBe(irmaoAntes.CriadoEm);
        liberado.Versao.ShouldBe(irmaoAntes.Versao, "a liberação não é mudança de estado");
    }

    [Theory]
    [InlineData("encerrar")]
    [InlineData("cancelar")]
    public async Task Terminal_Recusa409_SemEscrita(string desfecho)
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        var (alvo, irmao) = await FichaComDoisAtendimentosAsync(c, 1);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await PafOperacao.ChamarOkAsync(guiche);
        if (desfecho == "encerrar")
        {
            await PafOperacao.AgirOkAsync(guiche, alvo, "iniciar");
        }

        await PafOperacao.AgirOkAsync(guiche, alvo, desfecho);
        var antes = await _op.AtendimentoAsync(c, alvo);
        var irmaoAntes = await _op.AtendimentoAsync(c, irmao);

        using var resposta = await supervisor.PostAsync(Rota(alvo), content: null);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(OperacaoGuiche.MensagemNaoCancelavel);
        (await _op.AtendimentoAsync(c, alvo)).ShouldBeEquivalentTo(antes);
        (await _op.AtendimentoAsync(c, irmao)).ShouldBeEquivalentTo(irmaoAntes);
    }

    [Fact]
    public async Task OutraUnidade_404_SemEscrita()
    {
        var c = await _op.CriarAsync();
        var outra = await _op.CriarAsync();
        using var supervisorDaOutra = await SupervisorAsync(outra.UnidadeId);
        var (alvo, _) = await FichaComDoisAtendimentosAsync(c, 1);

        using var resposta = await supervisorDaOutra.PostAsync(Rota(alvo), content: null);

        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _op.AtendimentoAsync(c, alvo)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
    }

    [Fact]
    public async Task SemPermissao_403_MasOCancelamentoDoGuicheComOperarContinua()
    {
        var c = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(guiche);

        using (var recusa = await guiche.PostAsync(Rota(chamada.AtendimentoId), content: null))
        {
            recusa.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Atendimentos.Operar não dá FichasDoDia.Cancelar");
        }

        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);
        (await PafOperacao.AgirOkAsync(guiche, chamada.AtendimentoId, "cancelar")).Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
    }

    [Fact]
    public async Task GuicheDonoEPainel_RecebemAVersaoMaior_EAFilaAtualizada()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var atendente = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var tokenGuiche = (await _tenant.EntrarAsync(atendente)).AccessToken;
        using var guiche = _tenant.Cliente(tokenGuiche);
        using (var selecao = await guiche.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheA }))
        {
            selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        }

        var (alvo, irmao) = await FichaComDoisAtendimentosAsync(c, 1);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(guiche);

        var telaGuiche = await ConectarAsync(tokenGuiche);
        var painel = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));
        using var noGuiche = new Caixa<AtendimentoAtualizado>(telaGuiche, PafHubMetodos.AtendimentoAtualizado);
        using var noPainel = new Caixa<AtendimentoAtualizado>(painel, PafHubMetodos.AtendimentoAtualizado);
        using var filas = new Caixa<FilaAtualizada>(telaGuiche, PafHubMetodos.FilaAtualizada);

        await CancelarOkAsync(supervisor, alvo);

        foreach (var caixa in new[] { noGuiche, noPainel })
        {
            var aviso = await caixa.EsperarAsync(a => a.AtendimentoId == alvo, Espera);
            aviso.ShouldNotBeNull("o grupo da unidade alcança a tela do guichê dono e o painel");
            aviso.Versao.ShouldBeGreaterThan(chamada.Versao);
            aviso.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
            aviso.EstacaoId.ShouldBe(c.GuicheA);
        }

        (await filas.EsperarAsync(f => f.FilaId == c.Caixa, Espera)).ShouldNotBeNull("a fila do cancelado mudou");
        var filaDoIrmao = (await _op.AtendimentoAsync(c, irmao)).FilaId;
        (await filas.EsperarAsync(f => f.FilaId == filaDoIrmao, Espera)).ShouldNotBeNull("a fila do irmão liberado mudou");
    }

    [Fact]
    public async Task FalhaDepoisDoUpdate_DesfazTudo_SemCaixaDeSaida_EHubCalado()
    {
        var c = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        var (alvo, irmao) = await FichaComDoisAtendimentosAsync(c, 1);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await PafOperacao.ChamarOkAsync(guiche);
        await _op.DrenarCaixaDeSaidaAsync();
        var painel = await ConectarAsync(await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel));
        using var noPainel = new Caixa<AtendimentoAtualizado>(painel, PafHubMetodos.AtendimentoAtualizado);
        var antes = await _op.AtendimentoAsync(c, alvo);
        var irmaoAntes = await _op.AtendimentoAsync(c, irmao);
        var caixaAntes = await CaixaDeSaidaDoTenantAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            var avisos = scope.ServiceProvider.GetRequiredService<AvisosTempoReal>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var cancelado = await OperacaoGuiche.CancelarAsync(db, _tenant.TenantId, alvo, CancellationToken.None);
            foreach (var aviso in cancelado.Avisos)
            {
                await avisos.GravarAsync(aviso, CancellationToken.None);
            }

            (await CaixaDeSaidaDoTenantAsync()).ShouldBe(caixaAntes, "a gravação ainda não confirmada não aparece fora da transação");

            // The failure after the UPDATE and the outbox write: the commit never happens.
            await tx.RollbackAsync();
        }

        (await _op.AtendimentoAsync(c, alvo)).ShouldBeEquivalentTo(antes);
        (await _op.AtendimentoAsync(c, irmao)).ShouldBeEquivalentTo(irmaoAntes);
        (await CaixaDeSaidaDoTenantAsync()).ShouldBe(caixaAntes, "os avisos desfazem junto com a escrita");
        await _op.DrenarCaixaDeSaidaAsync();
        await Task.Delay(SilencioEsperado);
        noPainel.Recebidos.ShouldBeEmpty("nada anuncia um cancelamento que não aconteceu");
    }

    [Fact]
    public async Task SemTransacao_Lanca()
    {
        var c = await _op.CriarAsync();
        var (alvo, _) = await FichaComDoisAtendimentosAsync(c, 1);

        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        await Should.ThrowAsync<InvalidOperationException>(
            () => OperacaoGuiche.CancelarAsync(db, _tenant.TenantId, alvo, CancellationToken.None));
    }

    [Fact]
    public async Task EstacaoDesativada_ComVinculoAtivo_SoltaOVinculoNaMesmaTransacao()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        var (alvo, _) = await FichaComDoisAtendimentosAsync(c, 1);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await PafOperacao.ChamarOkAsync(guiche);
        await _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Ativa" = false WHERE "Id" = {c.GuicheA}""");

        await CancelarOkAsync(supervisor, alvo);

        var cancelado = await _op.AtendimentoAsync(c, alvo);
        var vinculo = await VinculoAsync(c.GuicheA);
        vinculo.LiberadoEm.ShouldNotBeNull("o atendimento saiu da estação desativada");
        vinculo.LiberadoEm.ShouldBe(cancelado.CanceladoEm, "mesma transação do cancelamento");
    }

    [Fact]
    public async Task EstacaoAtiva_MantemOVinculo()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        var (alvo, _) = await FichaComDoisAtendimentosAsync(c, 1);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await PafOperacao.ChamarOkAsync(guiche);

        await CancelarOkAsync(supervisor, alvo);

        (await VinculoAsync(c.GuicheA)).LiberadoEm.ShouldBeNull();
    }

    [Fact]
    public async Task DoisCancelamentosSimultaneos_UmVence_OutroRecebe409_VersaoSobeUmaVez()
    {
        var c = await _op.CriarAsync();
        using var supervisor1 = await SupervisorAsync(c.UnidadeId);
        using var supervisor2 = await SupervisorAsync(c.UnidadeId);
        var (alvo, _) = await FichaComDoisAtendimentosAsync(c, 1);

        var respostas = await Task.WhenAll(supervisor1.PostAsync(Rota(alvo), content: null), supervisor2.PostAsync(Rota(alvo), content: null));
        try
        {
            respostas.Select(r => r.StatusCode).OrderBy(s => s).ToArray()
                .ShouldBe(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, "a trava da ficha serializa e a segunda revalida");
        }
        finally
        {
            foreach (var resposta in respostas)
            {
                resposta.Dispose();
            }
        }

        (await _op.AtendimentoAsync(c, alvo)).Versao.ShouldBe(1);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>A person of the unidade holding only <c>FichasDoDia.Cancelar</c> beyond the basic set, with no guichê.</summary>
    private async Task<HttpClient> SupervisorAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.FichasDoDia.Cancelar);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static async Task<AtendimentoAlteradoDto> CancelarOkAsync(HttpClient cliente, Guid atendimentoId)
    {
        using var resposta = await cliente.PostAsync(Rota(atendimentoId), content: null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!;
    }

    /// <summary>
    /// One ficha routed to Caixa and to Protocolo (an encaminhamento's shape), both waiting: guichê A serves only
    /// Caixa, so its call takes the first and the second stays behind the ficha's exclusivity.
    /// </summary>
    private async Task<(Guid Caixa, Guid Protocolo)> FichaComDoisAtendimentosAsync(CenarioOperacao c, int numero)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, c.Caixa, c.Normal, "A", numero, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
        var caixa = Atendimento.Criar(c.UnidadeId, ficha.Id, c.Caixa, c.Normal, atendimentoOrigemId: null);
        var protocolo = Atendimento.Criar(c.UnidadeId, ficha.Id, c.Protocolo, c.Normal, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.AddRange(caixa, protocolo);
        await db.SaveChangesAsync();
        return (caixa.Id, protocolo.Id);
    }

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = await _op.ConectarAsync(token);
        _conexoes.Add(conexao);
        return conexao;
    }

    private async Task<int> CaixaDeSaidaDoTenantAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EventingDbContext>().OutboxMessages
            .CountAsync(m => m.TenantId == _tenant.TenantId);
    }

    private async Task<SessaoEstacao> VinculoAsync(Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.AsNoTracking().SingleAsync(v => v.EstacaoId == estacaoId);
    }
}
