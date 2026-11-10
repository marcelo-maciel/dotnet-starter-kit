using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Unidade of a client tenant set up for the operational flow. Caixa has Normal (A) and Preferencial (AP);
/// Protocolo has Normal (B); Retaguarda (R) is on no totem. Guichê A serves Caixa; guichê B serves Protocolo
/// (order 0) then Caixa (order 1). The painel shows Caixa; the other painel shows Protocolo.
/// </summary>
internal sealed record CenarioOperacao(
    Guid UnidadeId,
    Guid Caixa,
    Guid Protocolo,
    Guid Retaguarda,
    Guid Normal,
    Guid Preferencial,
    Guid GuicheA,
    Guid GuicheB,
    Guid Totem,
    Guid Painel,
    Guid PainelProtocolo);

/// <summary>Drives the PAF operation through the real host: station credentials from a redeemed link, people logged in with a guichê.</summary>
internal sealed class PafOperacao(FshWebApplicationFactory factory, PafTenantCliente tenant)
{
    public const string RotaEmissao = "/api/v1/paf/estacao/emissao/fichas";
    public const string RotaEstadoPainel = "/api/v1/paf/estacao/painel/estado";
    public const string RotaChamar = "/api/v1/paf/guiche/chamar-proximo";

    public static string Rota(Guid atendimentoId, string acao) => $"/api/v1/paf/guiche/atendimentos/{atendimentoId}/{acao}";

    public async Task<CenarioOperacao> CriarAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, unidadeCorrente: null);
        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var u = unidade.Id;
        var caixa = Fila.Criar(u, "Caixa", "Caixa", setorId: null);
        var protocolo = Fila.Criar(u, "Protocolo", "Protocolo", setorId: null);
        var retaguarda = Fila.Criar(u, "Retaguarda", "Retaguarda", setorId: null);
        var normal = Prioridade.Criar(u, "Normal", valor: 0);
        var preferencial = Prioridade.Criar(u, "Preferencial", valor: 10);
        var guicheA = Estacao.Criar(u, "Guichê 1", TipoEstacao.Guiche);
        var guicheB = Estacao.Criar(u, "Guichê 2", TipoEstacao.Guiche);
        var totem = Estacao.Criar(u, "Totem", TipoEstacao.Emissao);
        var painel = Estacao.Criar(u, "Painel Caixa", TipoEstacao.Painel);
        var painelProtocolo = Estacao.Criar(u, "Painel Protocolo", TipoEstacao.Painel);

        db.Unidades.Add(unidade);
        db.Filas.AddRange(caixa, protocolo, retaguarda);
        db.Prioridades.AddRange(normal, preferencial);
        db.Estacoes.AddRange(guicheA, guicheB, totem, painel, painelProtocolo);
        db.FilaPrioridades.AddRange(
            FilaPrioridade.Criar(u, caixa.Id, normal.Id, "A", numeroInicial: 1, ordem: 0),
            FilaPrioridade.Criar(u, caixa.Id, preferencial.Id, "AP", numeroInicial: 1, ordem: 1),
            FilaPrioridade.Criar(u, protocolo.Id, normal.Id, "B", numeroInicial: 1, ordem: 0),
            FilaPrioridade.Criar(u, retaguarda.Id, normal.Id, "R", numeroInicial: 1, ordem: 0));
        db.EstacaoFilas.AddRange(
            EstacaoFila.Criar(u, guicheA.Id, PapelEstacao.Guiche, caixa.Id, 0),
            EstacaoFila.Criar(u, guicheB.Id, PapelEstacao.Guiche, protocolo.Id, 0),
            EstacaoFila.Criar(u, guicheB.Id, PapelEstacao.Guiche, caixa.Id, 1),
            EstacaoFila.Criar(u, totem.Id, PapelEstacao.Emissao, caixa.Id, 0),
            EstacaoFila.Criar(u, totem.Id, PapelEstacao.Emissao, protocolo.Id, 1),
            EstacaoFila.Criar(u, painel.Id, PapelEstacao.Painel, caixa.Id, 0),
            EstacaoFila.Criar(u, painelProtocolo.Id, PapelEstacao.Painel, protocolo.Id, 0));
        db.EstacaoPrioridades.AddRange(
            EstacaoPrioridade.Criar(u, guicheA.Id, normal.Id, 0),
            EstacaoPrioridade.Criar(u, guicheA.Id, preferencial.Id, 1),
            EstacaoPrioridade.Criar(u, guicheB.Id, normal.Id, 0),
            EstacaoPrioridade.Criar(u, guicheB.Id, preferencial.Id, 1));
        // Started two days ago so the America/Sao_Paulo calendar day is inside the validity at any UTC hour.
        db.ContratosUnidade.Add(ContratoUnidade.Criar(u, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), null, "Teste", 5, PoliticaLimiteLicenca.Recusar));
        await db.SaveChangesAsync();

        return new CenarioOperacao(u, caixa.Id, protocolo.Id, retaguarda.Id, normal.Id, preferencial.Id,
            guicheA.Id, guicheB.Id, totem.Id, painel.Id, painelProtocolo.Id);
    }

    /// <summary>Real station credential: link generated for the station, redeemed through the anonymous endpoint.</summary>
    public async Task<string> CredencialAsync(Guid unidadeId, Guid estacaoId, string papel)
    {
        VinculoEstacaoGeradoDto link;
        using (var scope = factory.Services.CreateScope())
        {
            tenant.Contexto(scope, unidadeId);
            link = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GerarVinculoEstacaoCommand(estacaoId));
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", tenant.TenantId);
        using var resgate = await client.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token = link.Token });
        resgate.StatusCode.ShouldBe(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
        var credenciais = await resgate.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>();
        return credenciais!.Credenciais.Single(c => c.Papel == papel).AccessToken;
    }

    public async Task<HttpClient> TotemAsync(CenarioOperacao c) =>
        tenant.Cliente(await CredencialAsync(c.UnidadeId, c.Totem, PafPapeisEstacao.Emissao));

    /// <summary>A person of the unidade, logged in, with the guichê selected.</summary>
    public async Task<HttpClient> AtendenteAsync(CenarioOperacao c, Guid guiche)
    {
        var usuario = await tenant.CriarUsuarioAsync(c.UnidadeId);
        var cliente = tenant.Cliente((await tenant.EntrarAsync(usuario)).AccessToken);
        using var selecao = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = guiche });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        return cliente;
    }

    public static async Task<HttpResponseMessage> EmitirAsync(HttpClient totem, Guid fila, Guid prioridade, string? chave)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RotaEmissao);
        request.Content = JsonContent.Create(new EmitirFichaRequest(fila, prioridade));
        if (chave is not null)
        {
            request.Headers.Add("Idempotency-Key", chave);
        }

        return await totem.SendAsync(request);
    }

    public static async Task<FichaEmitidaDto> EmitirOkAsync(HttpClient totem, Guid fila, Guid prioridade)
    {
        using var resposta = await EmitirAsync(totem, fila, prioridade, Guid.NewGuid().ToString("N"));
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
    }

    public static Task<HttpResponseMessage> ChamarAsync(HttpClient atendente) => atendente.PostAsync(RotaChamar, content: null);

    public static async Task<ChamadaDto> ChamarOkAsync(HttpClient atendente)
    {
        using var resposta = await ChamarAsync(atendente);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;
    }

    public static Task<HttpResponseMessage> AgirAsync(HttpClient atendente, Guid atendimentoId, string acao) =>
        atendente.PostAsync(Rota(atendimentoId, acao), content: null);

    public static async Task<AtendimentoAlteradoDto> AgirOkAsync(HttpClient atendente, Guid atendimentoId, string acao)
    {
        using var resposta = await AgirAsync(atendente, atendimentoId, acao);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!;
    }

    /// <summary>Moves the unidade's fichas past the five-second guard (seção 4) without sleeping in the test.</summary>
    public Task<int> EnvelhecerFichasAsync(Guid unidadeId) =>
        tenant.ExecutarAsync($"""UPDATE paf."Fichas" SET "CriadoEm" = "CriadoEm" - interval '6 seconds' WHERE "UnidadeId" = {unidadeId}""");

    public async Task<Atendimento> AtendimentoAsync(CenarioOperacao c, Guid atendimentoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, c.UnidadeId);
        return await db.Atendimentos.AsNoTracking().SingleAsync(a => a.Id == atendimentoId);
    }

    public async Task<int> ContarAsync<T>(CenarioOperacao c, Func<FSH.Modules.PAF.Data.PafDbContext, IQueryable<T>> consulta)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, c.UnidadeId);
        return await consulta(db).CountAsync();
    }

    /// <summary>
    /// The host runs no outbox dispatcher, and OutboxDrain gives up after ten batches: the hundreds of notices
    /// this flow writes would sit ahead of later tests' rows. Drained here until a pass clears nothing.
    /// </summary>
    public async Task DrenarCaixaDeSaidaAsync()
    {
        for (var anterior = int.MaxValue; ;)
        {
            await OutboxDrain.DrainAsync(factory.Services);
            using var scope = factory.Services.CreateScope();
            var pendentes = await scope.ServiceProvider.GetRequiredService<FSH.Framework.Eventing.Persistence.EventingDbContext>()
                .OutboxMessages.CountAsync(m => m.ProcessedOnUtc == null && !m.IsDead);
            if (pendentes == 0 || pendentes >= anterior)
            {
                return;
            }

            anterior = pendentes;
        }
    }

    public async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = new HubConnectionBuilder()
            .WithUrl(
                $"http://localhost{PafHubRota.Caminho}?tenant={Uri.EscapeDataString(tenant.TenantId)}&access_token={Uri.EscapeDataString(token)}",
                options =>
                {
                    // Long polling: WebSocket through TestServer closes before the SignalR handshake (see PafHubTests).
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                })
            .Build();
        await conexao.StartAsync();
        await ProntaAsync(conexao);
        return conexao;
    }

    /// <summary>
    /// StartAsync returns at the handshake, before PafHubFilter and PafHub.OnConnectedAsync have joined the groups;
    /// the server reads invocations only after that, so one round trip means the groups are in place. A station's
    /// EntrarNoGuiche answers with a HubException, which is the round trip too.
    /// </summary>
    public static async Task ProntaAsync(HubConnection conexao)
    {
        var erro = await Record.ExceptionAsync(() => conexao.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche));
        if (erro is not null and not HubException)
        {
            throw erro;
        }
    }
}
