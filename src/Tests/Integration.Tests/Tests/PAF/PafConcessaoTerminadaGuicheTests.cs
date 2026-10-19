using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.Identity.Contracts.Events;
using FSH.Modules.Identity.Contracts.v1.Impersonation;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// S12 (RF22, REQ l.599, ARQ seção 9 "o vínculo de impersonação segue a vida da concessão"): o consumidor de
/// <see cref="ImpersonationGrantTerminatedIntegrationEvent"/> solta o vínculo do guichê da concessão encerrada ou
/// revogada, tira as conexões dela dos grupos e as encerra; o atendimento em curso fica no guichê. O host do harness
/// sobe sem Valkey: a chave da unidade da concessão é apagada no teste com Valkey real de
/// <see cref="PafImpersonacaoUnidadeTests"/>. O vencimento sem evento, solto na seleção de outro usuário, está em
/// <see cref="PafImpersonacaoGuicheTests"/> (VinculoDeConcessaoVencidaOuEncerrada_UsuarioTomaOGuiche_ConcessaoVivaSegueRecusando).
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafConcessaoTerminadaGuicheTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Impersonacao = TestConstants.IdentityBasePath + "/impersonation";
    private const string RotaSelecao = "/api/v1/paf/sessao/guiche";
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-s12");
    private readonly List<HubConnection> _conexoes = [];
    private readonly List<IDisposable> _descartaveis = [];
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public async Task DisposeAsync()
    {
        foreach (var descartavel in _descartaveis)
        {
            descartavel.Dispose();
        }

        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        await _op.DrenarCaixaDeSaidaAsync();
    }

    [Fact]
    public async Task FimDaImpersonacao_SoltaOVinculo_EAConexaoSaiDoGuicheENaoRecebeAProximaChamada()
    {
        var (c, suporte, hub, chamadasDoSuporte) = await SuporteNoGuicheLivreAsync();
        var fechou = Fechamento(hub);

        using (var fim = await suporte.Cliente.PostAsync($"{Impersonacao}/end", content: null))
        {
            fim.StatusCode.ShouldBe(HttpStatusCode.OK, await fim.Content.ReadAsStringAsync());
        }

        // The kit only records the event: before it is delivered the PAF has not moved yet.
        (await VinculosAtivosDaConcessaoAsync(c, suporte.Concessao)).ShouldBe(1, "sem o evento entregue o PAF ainda não soltou");

        await _op.DrenarCaixaDeSaidaAsync();

        (await VinculosAtivosDaConcessaoAsync(c, suporte.Concessao)).ShouldBe(0, "o consumidor soltou o vínculo da concessão");
        (await Task.WhenAny(fechou, Task.Delay(Espera))).ShouldBe(fechou, "a conexão da concessão terminada fecha (aqui também pelo kit; a prova do consumidor é SoOConsumidor)");
        await ColegaChamaNoGuicheEOSuporteNaoRecebeAsync(c, chamadasDoSuporte);
    }

    [Fact]
    public async Task Revogacao_SoltaOVinculo_AtendimentoChamadoFicaNoGuicheComEstadoEVersaoIntactos()
    {
        var (c, suporte, hub, _, _) = await SuporteNoGuicheComChamadaPendenteAsync();
        var fechou = Fechamento(hub);
        var antes = await AtendimentoChamadoNoGuicheAsync(c);

        using (var root = await new AuthHelper(factory).CreateRootAdminClientAsync())
        {
            var grantId = await ConcessaoNoKitAsync(root, suporte.Concessao);
            using var revogacao = await root.PostAsJsonAsync($"{Impersonacao}/grants/{grantId}/revoke", new { reason = "teste S12" });
            revogacao.StatusCode.ShouldBe(HttpStatusCode.OK, await revogacao.Content.ReadAsStringAsync());
        }

        await _op.DrenarCaixaDeSaidaAsync();

        (await VinculosAtivosDaConcessaoAsync(c, suporte.Concessao)).ShouldBe(0, "a revogação solta como o fim");
        (await Task.WhenAny(fechou, Task.Delay(Espera))).ShouldBe(fechou, "a conexão da concessão revogada fecha (aqui também pelo kit; a prova do consumidor é SoOConsumidor)");
        var depois = await _op.AtendimentoAsync(c, antes.Id);
        depois.Estado.ShouldBe(EstadoAtendimento.Chamado, "o atendimento em curso fica no guichê (REQ l.599)");
        depois.Versao.ShouldBe(antes.Versao, "a soltura do vínculo não escreve no atendimento");
        depois.EstacaoId.ShouldBe(c.GuicheA);

        // The guichê is free of the grant, and the pending call is offered for release as any other pendência (RF12).
        using var colega = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);
        var recusa = await SelecionarAsync(colega, c.GuicheA, HttpStatusCode.Conflict);
        recusa.ShouldNotContain(SetGuicheCommandHandler.MensagemEmUsoPeloSuporte);
        recusa.ShouldContain("pendente de outro atendente");
    }

    // Over long polling the kit's jti check refuses the next poll of an ended grant's token and the client closes by
    // itself, so the tests above cannot tell who closed it. Here the token stays valid for the kit and only the
    // consumer can close the connection and take it out of the guichê's group (over WebSockets nothing else would).
    [Fact]
    public async Task SoOConsumidor_EncerraAConexaoDaConcessao_ComOTokenAindaAceitoPeloKit()
    {
        var (c, suporte, hub, chamadasDoSuporte) = await SuporteNoGuicheLivreAsync();
        var fechou = Fechamento(hub);

        await PublicarAsync(Evento(suporte, ImpersonationGrantStatus.Ended));

        (await Task.WhenAny(fechou, Task.Delay(Espera))).ShouldBe(fechou, "o consumidor aborta a conexão da concessão");
        await ColegaChamaNoGuicheEOSuporteNaoRecebeAsync(c, chamadasDoSuporte);
    }

    [Fact]
    public async Task EventoRepetido_DepoisQueOColegaTomouOGuiche_NaoAlteraNada()
    {
        var (c, suporte, _, _) = await SuporteNoGuicheLivreAsync();
        var evento = Evento(suporte, ImpersonationGrantStatus.Ended);
        await PublicarAsync(evento);
        var solto = await VinculoDaConcessaoAsync(c, suporte.Concessao);
        solto.LiberadoEm.ShouldNotBeNull();
        using var colega = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);
        await SelecionarAsync(colega, c.GuicheA, HttpStatusCode.NoContent);
        var doColega = await VinculoAtivoAsync(c, c.GuicheA);

        // A fresh event id passes the kit inbox, as a redelivery from another outbox row would; then the revocation
        // of the same grant after its end, which the kit itself never raises.
        await PublicarAsync(evento with { Id = Guid.NewGuid() });
        await PublicarAsync(Evento(suporte, ImpersonationGrantStatus.Revoked));

        (await VinculoDaConcessaoAsync(c, suporte.Concessao)).LiberadoEm.ShouldBe(solto.LiberadoEm, "o vínculo já solto não é regravado");
        (await VinculoAtivoAsync(c, c.GuicheA)).Id.ShouldBe(doColega.Id, "o vínculo do colega no mesmo guichê não é tocado");
    }

    [Fact]
    public async Task EventoDuranteAChamadaDoSuporte_NenhumVinculoSobra_EOAtendimentoFicaCoerente()
    {
        var (c, suporte, _, _) = await SuporteNoGuicheLivreAsync();
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        // The token is still valid for the kit (only the event is published): the call and the release race on the link row.
        var chamada = PafOperacao.ChamarAsync(suporte.Cliente);
        var evento = PublicarAsync(Evento(suporte, ImpersonationGrantStatus.Revoked));
        await Task.WhenAll(chamada, evento);
        using var resposta = await chamada;

        (await VinculosAtivosDaConcessaoAsync(c, suporte.Concessao)).ShouldBe(0, "o evento solta o vínculo vença quem vencer");
        var chamados = await ChamadosNoGuicheAsync(c);
        if (resposta.IsSuccessStatusCode)
        {
            // The call committed first: its atendimento stays on the guichê, untouched by the release.
            var dto = await resposta.Content.ReadFromJsonAsync<FSH.Modules.PAF.Contracts.v1.Atendimentos.ChamadaDto>();
            chamados.ShouldHaveSingleItem().Id.ShouldBe(dto!.AtendimentoId);
        }
        else
        {
            // The release committed first: the call found no guichê and wrote nothing.
            chamados.ShouldBeEmpty(await resposta.Content.ReadAsStringAsync());
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>The support on guichê A with a hub connection, proved in the guichê's group by its own call, left <c>Chamado</c>.</summary>
    private async Task<(CenarioOperacao C, Suporte Suporte, HubConnection Hub, Caixa<SenhaChamada> Chamadas, Guid Propria)> SuporteNoGuicheComChamadaPendenteAsync()
    {
        var c = await _op.CriarAsync();
        using (var totem = await _op.TotemAsync(c))
        {
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        }

        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var suporte = await SuporteAsync(alvo);
        var hub = await _op.ConectarAsync(suporte.Token);
        _conexoes.Add(hub);
        var chamadas = new Caixa<SenhaChamada>(hub, PafHubMetodos.SenhaChamada);
        _descartaveis.Add(chamadas);
        await SelecionarAsync(suporte.Cliente, c.GuicheA, HttpStatusCode.NoContent);
        var propria = await PafOperacao.ChamarOkAsync(suporte.Cliente);
        (await chamadas.EsperarAsync(s => s.AtendimentoId == propria.AtendimentoId, Espera))
            .ShouldNotBeNull("antes do fim a conexão do suporte está no grupo do guichê A");
        return (c, suporte, hub, chamadas, propria.AtendimentoId);
    }

    /// <summary>As above, with the support's own call ended: nothing in progress holds the guichê.</summary>
    private async Task<(CenarioOperacao C, Suporte Suporte, HubConnection Hub, Caixa<SenhaChamada> Chamadas)> SuporteNoGuicheLivreAsync()
    {
        var (c, suporte, hub, chamadas, propria) = await SuporteNoGuicheComChamadaPendenteAsync();
        await PafOperacao.AgirOkAsync(suporte.Cliente, propria, "iniciar");
        await PafOperacao.AgirOkAsync(suporte.Cliente, propria, "encerrar");
        return (c, suporte, hub, chamadas);
    }

    /// <summary>A colleague takes guichê A and calls: his connection gets the call, the support's must not.</summary>
    private async Task ColegaChamaNoGuicheEOSuporteNaoRecebeAsync(CenarioOperacao c, Caixa<SenhaChamada> chamadasDoSuporte)
    {
        var token = (await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken;
        using var colega = _tenant.Cliente(token);
        var hub = await _op.ConectarAsync(token);
        _conexoes.Add(hub);
        using var doColega = new Caixa<SenhaChamada>(hub, PafHubMetodos.SenhaChamada);
        await SelecionarAsync(colega, c.GuicheA, HttpStatusCode.NoContent);

        var chamada = await PafOperacao.ChamarOkAsync(colega);

        (await doColega.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId, Espera))
            .ShouldNotBeNull("o grupo do guichê A recebe a chamada do colega");
        await Task.Delay(SilencioEsperado);
        chamadasDoSuporte.Recebidos.ShouldNotContain(s => s.AtendimentoId == chamada.AtendimentoId, "a conexão da concessão terminada saiu do grupo do guichê A");
    }

    private async Task<Suporte> SuporteAsync(SeededUser alvo)
    {
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        using var resposta = await root.PostAsJsonAsync($"{Impersonacao}/start", new
        {
            targetUserId = alvo.UserId,
            targetTenantId = _tenant.TenantId,
            reason = "teste da concessão terminada",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("accessToken").GetString()!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var cliente = _tenant.Cliente(token);
        _descartaveis.Add(cliente);
        return new Suporte(cliente, token, jwt.Id, Guid.Parse(jwt.Id), alvo.UserId);
    }

    private async Task<Guid> ConcessaoNoKitAsync(HttpClient root, Guid concessao)
    {
        var concessoes = await root.GetFromJsonAsync<List<ImpersonationGrantDto>>(
            $"{Impersonacao}/grants?ImpersonatedTenantId={Uri.EscapeDataString(_tenant.TenantId)}");
        return concessoes!.Single(g => Guid.Parse(g.Jti) == concessao).Id;
    }

    private ImpersonationGrantTerminatedIntegrationEvent Evento(Suporte suporte, ImpersonationGrantStatus motivo) => new(
        Id: Guid.NewGuid(),
        OccurredOnUtc: DateTime.UtcNow,
        TenantId: _tenant.TenantId,
        CorrelationId: Guid.NewGuid().ToString(),
        Source: "Identity",
        GrantId: Guid.NewGuid(),
        Jti: suporte.Jti,
        ImpersonatedUserId: suporte.UsuarioId,
        Motivo: motivo);

    // The kit bus installs the event's tenant and runs the inbox, as the outbox dispatcher does.
    private async Task PublicarAsync(IIntegrationEvent evento)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(evento);
    }

    private static async Task<string> SelecionarAsync(HttpClient cliente, Guid estacaoId, HttpStatusCode esperado)
    {
        using var resposta = await cliente.PostAsJsonAsync(RotaSelecao, new { estacaoId, liberarPendencia = false });
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(esperado, corpo);
        return corpo;
    }

    private Task<int> VinculosAtivosDaConcessaoAsync(CenarioOperacao c, Guid concessao) =>
        _op.ContarAsync(c, db => db.SessoesEstacao.Where(v => v.ConcessaoId == concessao && v.LiberadoEm == null));

    private async Task<SessaoEstacao> VinculoDaConcessaoAsync(CenarioOperacao c, Guid concessao)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, c.UnidadeId).SessoesEstacao.AsNoTracking().SingleAsync(v => v.ConcessaoId == concessao);
    }

    private async Task<SessaoEstacao> VinculoAtivoAsync(CenarioOperacao c, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, c.UnidadeId).SessoesEstacao.AsNoTracking()
            .SingleAsync(v => v.EstacaoId == estacaoId && v.LiberadoEm == null);
    }

    private async Task<Atendimento> AtendimentoChamadoNoGuicheAsync(CenarioOperacao c) =>
        (await ChamadosNoGuicheAsync(c)).ShouldHaveSingleItem();

    private async Task<List<Atendimento>> ChamadosNoGuicheAsync(CenarioOperacao c)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, c.UnidadeId).Atendimentos.AsNoTracking()
            .Where(a => a.EstacaoId == c.GuicheA && a.Estado == EstadoAtendimento.Chamado).ToListAsync();
    }

    private static Task Fechamento(HubConnection conexao)
    {
        var fechou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Closed += _ =>
        {
            fechou.TrySetResult();
            return Task.CompletedTask;
        };
        return fechou.Task;
    }

    private sealed record Suporte(HttpClient Cliente, string Token, string Jti, Guid Concessao, string UsuarioId);
}
