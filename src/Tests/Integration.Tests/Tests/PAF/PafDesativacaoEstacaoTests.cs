using System.Diagnostics;
using System.Security.Claims;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Mediator;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Station deactivation and stuck-guichê release with real effect (RF04, ARQ seção 5 and seção 7, C05): the guichê
/// link is released at once or when the atendimento in progress ends, the dialog may cancel it by RF12, device links
/// die with the station, and the manager's release refuses an atendimento in progress.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafDesativacaoEstacaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const int Rodadas = 50;
    private static readonly TimeSpan Fechamento = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-c05");
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

    [Fact]
    public async Task GuicheSemAtendimento_DesativarSoltaOVinculo_EAChamadaFicaSemGuiche()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);

        var desativada = await DesativarAsync(c, new DisableEstacaoCommand(c.GuicheA));

        desativada.GuicheLiberado.ShouldBeTrue();
        desativada.EmCurso.ShouldBeEmpty();
        (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(0);
        await ChamarRecusadoAsync(atendente, MensagensGuiche.MensagemSemGuiche);
    }

    [Fact]
    public async Task ComChamado_VinculoFica_ChamarERecusado_EEncerrarSolta()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var chamada = await ChamarUmaAsync(c, totem, atendente);

        var impacto = await PreverAsync(c, c.GuicheA);
        impacto.GuicheOcupado.ShouldBeTrue();
        impacto.EmCurso.ShouldHaveSingleItem().AtendimentoId.ShouldBe(chamada.AtendimentoId);

        var desativada = await DesativarAsync(c, new DisableEstacaoCommand(c.GuicheA));

        desativada.GuicheLiberado.ShouldBeFalse("o atendimento chamado segura o guichê até terminar");
        desativada.EmCurso.ShouldHaveSingleItem().Estado.ShouldBe(nameof(EstadoAtendimento.Chamado));
        (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(1);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await ChamarRecusadoAsync(atendente, ChamarProximaFichaCommandHandler.MensagemEstacaoInativa);

        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(1, "iniciar não termina o atendimento");
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "encerrar");
        (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(0, "encerrar na estação inativa solta o guichê na mesma transação");
    }

    [Fact]
    public async Task ComCancelar_PeloEndpoint_CanceladoESolto()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var gerente = await GerenteAsync(c.UnidadeId);
        var chamada = await ChamarUmaAsync(c, totem, atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");

        using var resposta = await gerente.PostAsJsonAsync($"/api/v1/paf/estacoes/{c.GuicheA}/disable", new { cancelarAtendimentoId = chamada.AtendimentoId });

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var desativada = (await resposta.Content.ReadFromJsonAsync<EstacaoDesativadaDto>())!;
        desativada.GuicheLiberado.ShouldBeTrue();
        desativada.EmCurso.ShouldBeEmpty();
        var atendimento = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        atendimento.Versao.ShouldBe(chamada.Versao + 2, "iniciar e cancelar, cada um com a sua versão");
        (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(0);
    }

    [Fact]
    public async Task CancelarPedidoJaEncerrado_NaoFalha_ESoltaOGuiche()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var chamada = await ChamarUmaAsync(c, totem, atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        // The dialog showed it in progress; the attendant ended it before the confirmation arrived.
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "encerrar");

        var desativada = await DesativarAsync(c, new DisableEstacaoCommand(c.GuicheA) { CancelarAtendimentoId = chamada.AtendimentoId });

        desativada.GuicheLiberado.ShouldBeTrue();
        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Encerrado, "o atendimento encerrado não é cancelado");
        (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(0);
    }

    [Fact]
    public async Task Previa_MostraCombinacaoQuePerdeOUltimoGuiche_ESemEscrever()
    {
        var c = await _op.CriarAsync();

        // Guichê B is the only one serving Protocolo; Caixa is also on guichê A.
        var impacto = await PreverAsync(c, c.GuicheB);

        impacto.GuicheOcupado.ShouldBeFalse();
        impacto.EmCurso.ShouldBeEmpty();
        impacto.CombinacoesSemCobertura.Select(x => x.Letra).ShouldBe(["B"]);
        (await PreverAsync(c, c.GuicheA)).CombinacoesSemCobertura.ShouldBeEmpty();
        (await _op.ContarAsync(c, db => db.Estacoes.Where(e => e.Id == c.GuicheB && e.Ativa))).ShouldBe(1, "a prévia não desativa");

        var desativada = await DesativarAsync(c, new DisableEstacaoCommand(c.GuicheB));
        desativada.CombinacoesSemCobertura.Select(x => x.Letra).ShouldBe(["B"], "a prévia e a desativação concordam");
    }

    [Fact]
    public async Task PainelConectado_DesativarFechaAConexaoEmMenosDeDoisSegundos_ERevogaOVinculo()
    {
        var c = await _op.CriarAsync();
        var token = await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var painel = await ConectarAsync(token);
        var fechou = QuandoFechar(painel);

        var relogio = Stopwatch.StartNew();
        await DesativarAsync(c, new DisableEstacaoCommand(c.Painel));

        (await Task.WhenAny(fechou, Task.Delay(Fechamento))).ShouldBe(fechou, "a instância que segura a conexão a encerra");
        relogio.Elapsed.ShouldBeLessThan(Fechamento);
        (await _op.ContarAsync(c, db => db.VinculosEstacao.Where(v => v.EstacaoId == c.Painel && v.RevogadoEm == null))).ShouldBe(0);
        await Should.ThrowAsync<HttpRequestException>(() => ConectarAsync(token));
    }

    /// <summary>
    /// Another instance holds the painel's connection: the deactivation reaches it only through the Valkey registry
    /// (S03). The harness host runs without Valkey, so the handler runs with a ConexoesPaf of a second instance over
    /// a real Valkey, and the first instance registered the real connection there. Only the holding instance can
    /// abort (seção 7): here the connection leaves every group at once, is refused on its next call and cannot
    /// reconnect.
    /// </summary>
    [Fact]
    public async Task PainelEmOutraInstancia_DesativarTiraDosGruposPeloRegistro_EReconexaoRecusada()
    {
        await using var valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
        await valkey.StartAsync();
        var opcoesValkey = ConfigurationOptions.Parse(valkey.GetConnectionString());
        opcoesValkey.AbortOnConnectFail = false;
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(opcoesValkey);

        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var token = await _op.CredencialAsync(c.UnidadeId, c.Painel, PafPapeisEstacao.Painel);
        var painel = await ConectarAsync(token);
        using var filas = new Caixa<FilaAtualizada>(painel, PafHubMetodos.FilaAtualizada);

        var primeira = NovaInstancia(multiplexer);
        var segunda = NovaInstancia(multiplexer);
        var principal = Principal(token);
        var contexto = new ContextoDeTeste(painel.ConnectionId!, principal);
        await primeira.RegistrarAsync(contexto, ConexaoPaf.Ler(principal)!, CancellationToken.None);
        foreach (var grupo in new[] { PafGrupos.Unidade(_tenant.TenantId, c.UnidadeId), PafGrupos.Painel(_tenant.TenantId, c.Painel) })
        {
            await primeira.EntrarNoGrupoAsync(contexto.ConnectionId, _tenant.TenantId, grupo, CancellationToken.None);
        }

        // Setup proof: the painel receives the unit's movement, so the silence below is the deactivation's doing.
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        (await filas.EsperarAsync(f => f.UnidadeId == c.UnidadeId, Fechamento)).ShouldNotBeNull();

        var relogio = Stopwatch.StartNew();
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            var servicos = scope.ServiceProvider;
            var handler = new DisableEstacaoCommandHandler(
                db, servicos.GetRequiredService<IUnidadeCorrente>(), segunda, servicos.GetRequiredService<AvisosTempoReal>(),
                servicos.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>());
            await handler.Handle(new DisableEstacaoCommand(c.Painel), CancellationToken.None);
        }

        relogio.Elapsed.ShouldBeLessThan(Fechamento);
        var recebidas = filas.Recebidos.Count;
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await Task.Delay(SilencioEsperado);
        filas.Recebidos.Count.ShouldBe(recebidas, "fora de todos os grupos pelo registro, o painel não recebe mais nada");
        contexto.Abortada.ShouldBeFalse("só a instância que segura a conexão aborta; a outra a alcança pelos grupos");

        var fechou = QuandoFechar(painel);
        (await Record.ExceptionAsync(() => painel.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche))).ShouldNotBeNull();
        (await Task.WhenAny(fechou, Task.Delay(Fechamento))).ShouldBe(fechou, "a próxima chamada da conexão é recusada e a fecha");
        await Should.ThrowAsync<HttpRequestException>(() => ConectarAsync(token));
    }

    /// <summary>
    /// Deactivation and the call of the same guichê, side by side, 50 times. Whatever the order, the guichê is never
    /// released with a Chamado on it, and never kept with nothing in progress: the link row lock serializes them.
    /// </summary>
    [Fact]
    public async Task DesativarEChamarProximo_EmCinquentaRodadas_NuncaQuebramAInvariante()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamadas = 0;

        for (var rodada = 1; rodada <= Rodadas; rodada++)
        {
            var (desativada, resposta) = await ChamarEDesativarAsync(c, atendente, TimeSpan.FromMilliseconds(rodada % 10 * 5));
            using var _ = resposta;

            var emCurso = await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.EstacaoId == c.GuicheA
                && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)));
            var vinculos = await VinculosAtivosAsync(c.GuicheA);
            var detalhe = $"rodada {rodada}: chamar={resposta.StatusCode} emCurso={emCurso} vinculos={vinculos}";
            vinculos.ShouldBe(emCurso, detalhe);
            desativada.GuicheLiberado.ShouldBe(emCurso == 0, detalhe);

            if (resposta.StatusCode == HttpStatusCode.OK)
            {
                chamadas++;
                var chamada = (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;
                await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "cancelar");
                await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
                await _op.EnvelhecerFichasAsync(c.UnidadeId);
            }
            else
            {
                resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, detalhe);
            }

            (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(0, detalhe);
            await EnviarAsync(c, new EnableEstacaoCommand(c.GuicheA));
            using var selecao = await atendente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = c.GuicheA });
            selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{detalhe} {await selecao.Content.ReadAsStringAsync()}");
        }

        // Both orders are legal; the count only shows which ones the runs exercised.
        Console.WriteLine($"[C05] desativar x chamar: {chamadas} chamadas antes da desativação em {Rodadas} rodadas");
    }

    [Fact]
    public async Task Liberar_SemPermissao403_ComEmCurso409_LivreSolta_ESemVinculoNaoFaz()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var colega = await _op.AtendenteAsync(c, c.GuicheB);
        using var gerente = await GerenteAsync(c.UnidadeId);

        using (var recusa = await atendente.PostAsync(Rota(c.GuicheB), content: null))
        {
            recusa.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Guiches.Liberar não é básica");
        }

        var chamada = await ChamarUmaAsync(c, totem, atendente);
        using (var emCurso = await gerente.PostAsync(Rota(c.GuicheA), content: null))
        {
            emCurso.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            PafSessaoLicencaTests.Detalhe(await emCurso.Content.ReadAsStringAsync()).ShouldBe(VinculosGuiche.MensagemAtendimentoEmCurso);
        }

        (await VinculosAtivosAsync(c.GuicheA)).ShouldBe(1);
        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);

        using (var livre = await gerente.PostAsync(Rota(c.GuicheB), content: null))
        {
            livre.StatusCode.ShouldBe(HttpStatusCode.NoContent, await livre.Content.ReadAsStringAsync());
        }

        (await VinculosAtivosAsync(c.GuicheB)).ShouldBe(0);
        await ChamarRecusadoAsync(colega, MensagensGuiche.MensagemSemGuiche);

        using (var semVinculo = await gerente.PostAsync(Rota(c.GuicheB), content: null))
        {
            semVinculo.StatusCode.ShouldBe(HttpStatusCode.NoContent, "liberar um guichê já livre não é erro");
        }

        var outraUnidade = await _op.CriarAsync();
        using var deOutra = await gerente.PostAsync(Rota(outraUnidade.GuicheA), content: null);
        deOutra.StatusCode.ShouldBe(HttpStatusCode.NotFound, "o guichê de outra unidade não é alcançado");

        // The pre-C05 shape, with no body, still deactivates and keeps the atendimento in progress.
        using var semCorpo = await gerente.PostAsync($"/api/v1/paf/estacoes/{c.GuicheA}/disable", content: null);
        semCorpo.StatusCode.ShouldBe(HttpStatusCode.OK, await semCorpo.Content.ReadAsStringAsync());
        (await semCorpo.Content.ReadFromJsonAsync<EstacaoDesativadaDto>())!.EmCurso.ShouldHaveSingleItem().AtendimentoId.ShouldBe(chamada.AtendimentoId);
    }

    /// <summary>
    /// The release reads the link unlocked and only then locks it. A session that lets guichê A go and takes guichê B
    /// in that window leaves the release nothing to release, and its connections must stay in B's group. The test holds
    /// the link's row lock to put the session's move exactly there, done as the guichê selection does it.
    /// </summary>
    [Fact]
    public async Task Liberar_SessaoTrocouDeGuicheEntreLeituraETrava_ConexaoContinuaNoGuicheNovo()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var gerente = await GerenteAsync(c.UnidadeId);
        var token = atendente.DefaultRequestHeaders.Authorization!.Parameter!;
        var sessao = PafTenantCliente.SessaoDoToken(token);
        var hub = await ConectarAsync(token);
        using var chamadas = new Caixa<SenhaChamada>(hub, PafHubMetodos.SenhaChamada);
        var conexoes = factory.Services.GetRequiredService<ConexoesPaf>();

        Task<HttpResponseMessage> liberar;
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            await using var trava = await db.Database.BeginTransactionAsync();
            var vinculo = await VinculosGuiche.TravarAtivoDaEstacaoAsync(db, _tenant.TenantId, c.GuicheA, CancellationToken.None);
            vinculo.ShouldNotBeNull().SessaoId.ShouldBe(sessao);

            liberar = gerente.PostAsync(Rota(c.GuicheA), content: null);
            await EsperarTravaNoVinculoAsync();

            await VinculosGuiche.SoltarAsync(db, _tenant.TenantId, vinculo.Id, CancellationToken.None);
            db.SessoesEstacao.Add(SessaoEstacao.CriarParaSessao(c.GuicheB, sessao));
            await db.SaveChangesAsync();
            await conexoes.MoverSessaoParaGuicheAsync(_tenant.TenantId, sessao, c.GuicheB, CancellationToken.None);
            await trava.CommitAsync();
        }

        using (var resposta = await liberar)
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.NoContent, await resposta.Content.ReadAsStringAsync());
        }

        (await VinculosAtivosAsync(c.GuicheB)).ShouldBe(1, "a liberação do guichê A não toca o vínculo novo");
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);

        (await chamadas.EsperarAsync(s => s.AtendimentoId == chamada.AtendimentoId, TimeSpan.FromSeconds(5)))
            .ShouldNotBeNull("a conexão da sessão continua no grupo do guichê B");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    // The release is parked on the link's FOR UPDATE once a backend of this database waits on a lock with that query.
    private async Task EsperarTravaNoVinculoAsync()
    {
        var relogio = Stopwatch.StartNew();
        while (relogio.Elapsed < TimeSpan.FromSeconds(10))
        {
            using var scope = factory.Services.CreateScope();
            var db = _tenant.Contexto(scope, unidadeCorrente: null);
            var esperando = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                 WHERE datname = current_database() AND wait_event_type = 'Lock'
                   AND query LIKE '%SessoesEstacao%FOR UPDATE%'
                """).SingleAsync();
            if (esperando > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("A liberação não chegou à trava do vínculo em 10 s.");
    }

    private static string Rota(Guid estacaoId) => $"/api/v1/paf/guiches/{estacaoId}/liberar";

    private async Task<ChamadaDto> ChamarUmaAsync(CenarioOperacao c, HttpClient totem, HttpClient atendente)
    {
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        return await PafOperacao.ChamarOkAsync(atendente);
    }

    private static async Task ChamarRecusadoAsync(HttpClient atendente, string mensagem)
    {
        using var resposta = await PafOperacao.ChamarAsync(atendente);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(mensagem);
    }

    /// <summary>A person of the unidade with the release and the station cadastro beyond the basic set.</summary>
    private async Task<HttpClient> GerenteAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Guiches.Liberar, PafPermissions.Estacoes.Update);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    /// <summary>
    /// The HTTP call goes through the whole pipeline before reaching the link lock, the deactivation does not: the call
    /// leaves first and the deactivation follows after <paramref name="atraso"/>, varied by round so both orders occur.
    /// </summary>
    private async Task<(EstacaoDesativadaDto Desativada, HttpResponseMessage Chamada)> ChamarEDesativarAsync(
        CenarioOperacao c, HttpClient atendente, TimeSpan atraso)
    {
        var chamar = PafOperacao.ChamarAsync(atendente);
        await Task.Delay(atraso);
        var desativada = await DesativarAsync(c, new DisableEstacaoCommand(c.GuicheA));
        return (desativada, await chamar);
    }

    private Task<EstacaoDesativadaDto> DesativarAsync(CenarioOperacao c, DisableEstacaoCommand command) => EnviarAsync(c, command);

    private async Task<T> EnviarAsync<T>(CenarioOperacao c, ICommand<T> command)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
    }

    private async Task<ImpactoDesativacaoEstacaoDto> PreverAsync(CenarioOperacao c, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ObterImpactoDesativacaoEstacaoQuery(estacaoId));
    }

    private async Task<int> VinculosAtivosAsync(Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.CountAsync(v => v.EstacaoId == estacaoId && v.LiberadoEm == null);
    }

    private async Task<HubConnection> ConectarAsync(string token)
    {
        var conexao = await _op.ConectarAsync(token);
        _conexoes.Add(conexao);
        return conexao;
    }

    private static Task QuandoFechar(HubConnection conexao)
    {
        var fechou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conexao.Closed += _ =>
        {
            fechou.TrySetResult();
            return Task.CompletedTask;
        };
        return fechou.Task;
    }

    private ConexoesPaf NovaInstancia(ConnectionMultiplexer multiplexer)
    {
        var registro = new RegistroConexoes([multiplexer], Options.Create(new OpcoesSessaoPaf()), TimeProvider.System, NullLogger<RegistroConexoes>.Instance);
        return new ConexoesPaf(factory.Services.GetRequiredService<IHubContext<PafHub>>(), registro, NullLogger<ConexoesPaf>.Instance);
    }

    private static ClaimsPrincipal Principal(string token) =>
        new(new ClaimsIdentity(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Claims,
            authenticationType: "Bearer"));

    private sealed class ContextoDeTeste(string connectionId, ClaimsPrincipal principal) : HubCallerContext
    {
        public bool Abortada { get; private set; }
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => principal;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Abortada = true;
    }
}
