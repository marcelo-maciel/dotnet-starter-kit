using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// S11 (RF22, RNF15, ARQ seção 9): a impersonação da Codefi opera o guichê por completo com vínculo próprio da concessão
/// (<c>SessaoEstacao.ConcessaoId</c> = <c>jti</c>, vencimento = <c>exp</c>), o atendimento gravado em nome do impersonado
/// (<c>sub</c>), e cada escrita grava um <c>AtoImpersonacao</c> com operador, concessão e atendimento na própria transação.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafImpersonacaoGuicheTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Impersonacao = TestConstants.IdentityBasePath + "/impersonation";
    private const string RotaSelecao = "/api/v1/paf/sessao/guiche";
    private const string RotaPosto = "/api/v1/paf/guiche/posto";

    private readonly PafTenantCliente _tenant = new(factory, "paf-imp-guiche");
    private PafOperacao _op = default!;
    private readonly List<HubConnection> _conexoes = [];
    private readonly List<IDisposable> _caixas = [];

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public async Task DisposeAsync()
    {
        foreach (var caixa in _caixas)
        {
            caixa.Dispose();
        }

        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        await _op.DrenarCaixaDeSaidaAsync();
    }

    [Fact]
    public async Task Suporte_SelecionaChamaRechamaIniciaEncerraECancela_UmAtoPorOperacaoComOperadorConcessaoEAtendimento()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var imp = await SuporteAsync(alvo);
        var (suporte, token, concessao) = (imp.Cliente, imp.Token, imp.Concessao);
        var operador = await new PafApi(factory).RootAdminIdAsync();
        await using var hub = await _op.ConectarAsync(token);
        using var chamadas = new Caixa<SenhaChamada>(hub, PafHubMetodos.SenhaChamada);

        await SelecionarAsync(suporte, c.GuicheA, HttpStatusCode.NoContent);
        var vinculo = await VinculoAtivoAsync(c, c.GuicheA);
        vinculo.ConcessaoId.ShouldBe(concessao);
        vinculo.SessaoId.ShouldBeNull("o vínculo do suporte é da concessão, nunca da sessão do usuário real");
        vinculo.ConcessaoExpiraEm.ShouldBe(new DateTimeOffset(DateTime.SpecifyKind(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo, DateTimeKind.Utc)));
        (await AtosAsync(c, concessao)).ShouldHaveSingleItem().AtendimentoId.ShouldBeNull();

        // The same guichê again writes nothing, so it records nothing either.
        await SelecionarAsync(suporte, c.GuicheA, HttpStatusCode.NoContent);
        (await AtosAsync(c, concessao)).Count.ShouldBe(1, "seleção repetida não grava ato");
        (await suporte.GetFromJsonAsync<PostoGuicheDto>(RotaPosto))!.EstacaoId.ShouldBe(c.GuicheA);

        var primeira = await PafOperacao.ChamarOkAsync(suporte);
        (await chamadas.EsperarAsync(s => s.AtendimentoId == primeira.AtendimentoId, TimeSpan.FromSeconds(10)))
            .ShouldNotBeNull("a conexão do suporte entrou no grupo do guichê da concessão");
        (await hub.InvokeAsync<Guid?>(PafHubChamadas.EntrarNoGuiche)).ShouldBe(c.GuicheA);
        (await _op.AtendimentoAsync(c, primeira.AtendimentoId)).UsuarioId.ShouldBe(Guid.Parse(alvo.UserId), "o atendimento é gravado em nome do impersonado");
        await PafOperacao.AgirOkAsync(suporte, primeira.AtendimentoId, "rechamar");
        await PafOperacao.AgirOkAsync(suporte, primeira.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(suporte, primeira.AtendimentoId, "encerrar");
        var segunda = await PafOperacao.ChamarOkAsync(suporte);
        await PafOperacao.AgirOkAsync(suporte, segunda.AtendimentoId, "cancelar");

        var atos = await AtosAsync(c, concessao);
        atos.Count.ShouldBe(7, "seleção, chamar, rechamar, iniciar, encerrar, chamar e cancelar: um ato cada");
        atos.ShouldAllBe(a => a.OperadorId == operador && a.UnidadeId == c.UnidadeId && a.ConcessaoId == concessao);
        atos.Count(a => a.AtendimentoId == primeira.AtendimentoId).ShouldBe(4);
        atos.Count(a => a.AtendimentoId == segunda.AtendimentoId).ShouldBe(2);
        (await _op.AtendimentoAsync(c, primeira.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Encerrado);
        (await _op.AtendimentoAsync(c, segunda.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Cancelado);
    }

    [Fact]
    public async Task UsuarioRealNoGuiche_SelecaoDoSuporte409_EUsuarioSegueVinculadoEOperando()
    {
        var c = await _op.CriarAsync();
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var real = _tenant.Cliente((await _tenant.EntrarAsync(alvo)).AccessToken);
        await SelecionarAsync(real, c.GuicheA, HttpStatusCode.NoContent);
        var vinculoReal = await VinculoAtivoAsync(c, c.GuicheA);
        using var imp = await SuporteAsync(alvo);
        var (suporte, concessao) = (imp.Cliente, imp.Concessao);

        var recusa = await SelecionarAsync(suporte, c.GuicheA, HttpStatusCode.Conflict);
        recusa.ShouldContain("está em uso por");

        (await VinculoAtivoAsync(c, c.GuicheA)).Id.ShouldBe(vinculoReal.Id, "a sessão do usuário real não cai");
        (await AtosAsync(c, concessao)).ShouldBeEmpty("seleção recusada não grava ato");
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(real)).Senha.ShouldNotBeNullOrEmpty("o usuário real segue operando o guichê");

        // The other way round: the real user cannot take the guichê the support holds.
        await SelecionarAsync(suporte, c.GuicheB, HttpStatusCode.NoContent);
        using var colega = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);
        (await SelecionarAsync(colega, c.GuicheB, HttpStatusCode.Conflict)).ShouldContain(SetGuicheCommandHandler.MensagemEmUsoPeloSuporte);
        (await VinculoAtivoAsync(c, c.GuicheB)).ConcessaoId.ShouldBe(concessao);
    }

    [Fact]
    public async Task PendenciaDoProprioImpersonado_SuporteRetomaSemLiberarEOpera()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        ChamadaDto chamada;
        using (var real = _tenant.Cliente((await _tenant.EntrarAsync(alvo)).AccessToken))
        {
            await SelecionarAsync(real, c.GuicheA, HttpStatusCode.NoContent);
            chamada = await PafOperacao.ChamarOkAsync(real);
        }

        // A new login releases the old session's link (single session): the call stays on guichê A as a pendência.
        await _tenant.EntrarAsync(alvo);
        using var imp = await SuporteAsync(alvo);
        var (suporte, concessao) = (imp.Cliente, imp.Concessao);

        await SelecionarAsync(suporte, c.GuicheA, HttpStatusCode.NoContent);
        (await suporte.GetFromJsonAsync<PostoGuicheDto>(RotaPosto))!
            .Atendimento.ShouldNotBeNull().AtendimentoId.ShouldBe(chamada.AtendimentoId);
        await PafOperacao.AgirOkAsync(suporte, chamada.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(suporte, chamada.AtendimentoId, "encerrar");

        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Encerrado);
        var atos = await AtosAsync(c, concessao);
        atos.Count.ShouldBe(3);
        atos.Count(a => a.AtendimentoId == null).ShouldBe(1, "a retomada não cancela nada");
    }

    [Fact]
    public async Task CorridaPeloMesmoGuiche_SuporteEUsuario_UmVinculo()
    {
        var c = await _op.CriarAsync();
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var imp = await SuporteAsync(alvo);
        var suporte = imp.Cliente;
        using var real = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);

        for (var rodada = 0; rodada < 5; rodada++)
        {
            var respostas = await Task.WhenAll(
                suporte.PostAsJsonAsync(RotaSelecao, new { estacaoId = c.GuicheB, liberarPendencia = false }),
                real.PostAsJsonAsync(RotaSelecao, new { estacaoId = c.GuicheB, liberarPendencia = false }));
            var codigos = respostas.Select(r => r.StatusCode).OrderBy(s => s).ToList();
            foreach (var r in respostas)
            {
                r.Dispose();
            }

            codigos.ShouldBe([HttpStatusCode.NoContent, HttpStatusCode.Conflict], $"rodada {rodada}");
            (await _op.ContarAsync(c, db => db.SessoesEstacao.Where(v => v.EstacaoId == c.GuicheB && v.LiberadoEm == null)))
                .ShouldBe(1, $"rodada {rodada}: um vínculo por guichê");
            await _tenant.ExecutarAsync($"""UPDATE paf."SessoesEstacao" SET "LiberadoEm" = now() WHERE "EstacaoId" = {c.GuicheB} AND "LiberadoEm" IS NULL""");
        }
    }

    [Fact]
    public async Task FalhaForcadaDepoisDoAto_DesfazAtoEAtendimentoJuntos()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var imp = await SuporteAsync(alvo);
        var (suporte, concessao) = (imp.Cliente, imp.Concessao);
        await SelecionarAsync(suporte, c.GuicheA, HttpStatusCode.NoContent);
        var chamada = await PafOperacao.ChamarOkAsync(suporte);
        var antes = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
        (await AtosAsync(c, concessao)).Count.ShouldBe(2);

        // A deferred constraint trigger fails at COMMIT, after the act and the state UPDATE were both written; only
        // this atendimento entering EmAtendimento trips it.
        var sufixo = Guid.NewGuid().ToString("N")[..12];
        await _tenant.ExecutarAsync(FormattableStringFactory.Create(
            $"CREATE FUNCTION paf.s11_falha_{sufixo}() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN " +
            $"IF NEW.\"Id\" = '{chamada.AtendimentoId}' AND NEW.\"Estado\" = 'EmAtendimento' THEN RAISE EXCEPTION 'falha forcada'; END IF; RETURN NULL; END $f$"));
        try
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create(
                $"CREATE CONSTRAINT TRIGGER s11_falha_{sufixo} AFTER UPDATE ON paf.\"Atendimentos\" " +
                $"DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION paf.s11_falha_{sufixo}()"));

            using var resposta = await PafOperacao.AgirAsync(suporte, chamada.AtendimentoId, "iniciar");

            resposta.IsSuccessStatusCode.ShouldBeFalse(await resposta.Content.ReadAsStringAsync());
            (await AtosAsync(c, concessao)).Count.ShouldBe(2, "o ato sai junto com o atendimento desfeito");
            var depois = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
            depois.Estado.ShouldBe(EstadoAtendimento.Chamado, "o atendimento sai junto com o ato");
            depois.Versao.ShouldBe(antes.Versao);
        }
        finally
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP TRIGGER IF EXISTS s11_falha_{sufixo} ON paf.\"Atendimentos\""));
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP FUNCTION IF EXISTS paf.s11_falha_{sufixo}()"));
        }

        // Without the trigger the same write goes through and records its act: the failure above was the trigger's.
        await PafOperacao.AgirOkAsync(suporte, chamada.AtendimentoId, "iniciar");
        var atos = await AtosAsync(c, concessao);
        atos.Count.ShouldBe(3);
        atos.Count(a => a.AtendimentoId == chamada.AtendimentoId).ShouldBe(2);
    }

    [Fact]
    public async Task SemActSub_UsuarioOperaOGuiche_NenhumAto()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        using var real = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);

        await SelecionarAsync(real, c.GuicheA, HttpStatusCode.NoContent);
        var chamada = await PafOperacao.ChamarOkAsync(real);
        await PafOperacao.AgirOkAsync(real, chamada.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(real, chamada.AtendimentoId, "encerrar");

        (await _op.ContarAsync(c, db => db.AtosImpersonacao)).ShouldBe(0, "sem act_sub nenhum ato é gravado");
    }

    // ─── revisão da onda 3: R1 (emissão repetida) e R2 (grupo e vínculo da concessão) ───────────

    [Fact]
    public async Task EmissaoDoSuporte_NumeracaoColideUmaVez_RepeteEGravaUmAtoDoAtendimentoEmitido()
    {
        var c = await _op.CriarAsync();
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var imp = await SuporteAsync(alvo);
        var (suporte, concessao) = (imp.Cliente, imp.Concessao);
        await SelecionarAsync(suporte, c.GuicheA, HttpStatusCode.NoContent);

        // The first insert of a ficha in this unidade fails as a totem winning the number would make it fail
        // (UX_Fichas_Numeracao); the counter is a sequence, so the rollback of that attempt does not reset it.
        var sufixo = Guid.NewGuid().ToString("N")[..12];
        await _tenant.ExecutarAsync(FormattableStringFactory.Create($"CREATE SEQUENCE paf.r1_vez_{sufixo}"));
        await _tenant.ExecutarAsync(FormattableStringFactory.Create(
            $"CREATE FUNCTION paf.r1_colide_{sufixo}() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN " +
            $"IF NEW.\"UnidadeId\" = '{c.UnidadeId}' AND nextval('paf.r1_vez_{sufixo}') = 1 THEN " +
            $"RAISE EXCEPTION 'colisao forcada' USING ERRCODE = 'unique_violation', CONSTRAINT = '{EmissaoDeFicha.RestricaoNumeracao}'; END IF; " +
            $"RETURN NEW; END $f$"));
        try
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create(
                $"CREATE TRIGGER r1_colide_{sufixo} BEFORE INSERT ON paf.\"Fichas\" FOR EACH ROW EXECUTE FUNCTION paf.r1_colide_{sufixo}()"));

            using var resposta = await EmitirNoGuicheAsync(suporte, c.Caixa, c.Normal);

            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            var emitida = (await resposta.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
            (await _op.AtendimentoAsync(c, emitida.AtendimentoId)).FichaId.ShouldBe(emitida.FichaId);
            var atos = await AtosAsync(c, concessao);
            atos.Count.ShouldBe(2, "seleção e uma emissão: a tentativa desfeita não deixa ato");
            atos.Count(a => a.AtendimentoId == emitida.AtendimentoId).ShouldBe(1, "o ato nomeia o atendimento da tentativa que gravou");
        }
        finally
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP TRIGGER IF EXISTS r1_colide_{sufixo} ON paf.\"Fichas\""));
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP FUNCTION IF EXISTS paf.r1_colide_{sufixo}()"));
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP SEQUENCE IF EXISTS paf.r1_vez_{sufixo}"));
        }
    }

    [Fact]
    public async Task GerenteLiberaOGuicheDoSuporte_AConexaoDoSuporteSaiDoGrupoDoGuiche()
    {
        var (c, _, chamadas) = await SuporteNoGuicheComUmaChamadaAsync();
        using var gerente = await GerenteAsync(c.UnidadeId);

        using (var liberacao = await gerente.PostAsync($"/api/v1/paf/guiches/{c.GuicheA}/liberar", content: null))
        {
            liberacao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await liberacao.Content.ReadAsStringAsync());
        }

        await ColegaChamaNoGuicheEOSuporteNaoRecebeAsync(c, chamadas);
    }

    [Fact]
    public async Task DesativacaoDoGuicheDoSuporte_AConexaoDoSuporteSaiDoGrupoDoGuiche()
    {
        var (c, _, chamadas) = await SuporteNoGuicheComUmaChamadaAsync();
        using var gerente = await GerenteAsync(c.UnidadeId);

        using (var desativacao = await gerente.PostAsync($"/api/v1/paf/estacoes/{c.GuicheA}/disable", content: null))
        {
            desativacao.StatusCode.ShouldBe(HttpStatusCode.OK, await desativacao.Content.ReadAsStringAsync());
        }

        using (var reativacao = await gerente.PostAsync($"/api/v1/paf/estacoes/{c.GuicheA}/enable", content: null))
        {
            reativacao.IsSuccessStatusCode.ShouldBeTrue(await reativacao.Content.ReadAsStringAsync());
        }

        await ColegaChamaNoGuicheEOSuporteNaoRecebeAsync(c, chamadas);
    }

    [Fact]
    public async Task VinculoDeConcessaoVencidaOuEncerrada_UsuarioTomaOGuiche_ConcessaoVivaSegueRecusando()
    {
        var c = await _op.CriarAsync();
        using var colega = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);

        // Expired by the exp copied into the link: no event marks it, only the 5-minute sweep would release it.
        var vencida = await VincularConcessaoAsync(c.GuicheA, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1));
        await SelecionarAsync(colega, c.GuicheA, HttpStatusCode.NoContent);
        (await VinculoAtivoAsync(c, c.GuicheA)).ConcessaoId.ShouldBeNull("o vínculo da concessão vencida foi solto na seleção");
        (await _op.ContarAsync(c, db => db.SessoesEstacao.Where(v => v.Id == vencida && v.LiberadoEm != null))).ShouldBe(1);

        // Not expired but unknown to the kit, as a grant whose end event was lost: the kit counts it as ended.
        var encerrada = await VincularConcessaoAsync(c.GuicheB, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30));
        await SelecionarAsync(colega, c.GuicheB, HttpStatusCode.NoContent);
        (await _op.ContarAsync(c, db => db.SessoesEstacao.Where(v => v.Id == encerrada && v.LiberadoEm != null))).ShouldBe(1);

        // The colleague moved to B, so A is free; a live grant on it still holds it against a real user.
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var imp = await SuporteAsync(alvo);
        await SelecionarAsync(imp.Cliente, c.GuicheA, HttpStatusCode.NoContent);
        using var outro = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);
        (await SelecionarAsync(outro, c.GuicheA, HttpStatusCode.Conflict)).ShouldContain(SetGuicheCommandHandler.MensagemEmUsoPeloSuporte);
        (await VinculoAtivoAsync(c, c.GuicheA)).ConcessaoId.ShouldBe(imp.Concessao);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The support on guichê A with a hub connection, proved to be in the guichê's group by its own call, which is then
    /// ended so nothing in progress holds the guichê.
    /// </summary>
    private async Task<(CenarioOperacao C, Suporte Suporte, Caixa<SenhaChamada> Chamadas)> SuporteNoGuicheComUmaChamadaAsync()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        var imp = await SuporteAsync(alvo);
        _caixas.Add(imp);
        var hub = await _op.ConectarAsync(imp.Token);
        _conexoes.Add(hub);
        var chamadas = new Caixa<SenhaChamada>(hub, PafHubMetodos.SenhaChamada);
        _caixas.Add(chamadas);

        await SelecionarAsync(imp.Cliente, c.GuicheA, HttpStatusCode.NoContent);
        var propria = await PafOperacao.ChamarOkAsync(imp.Cliente);
        (await chamadas.EsperarAsync(s => s.AtendimentoId == propria.AtendimentoId, Espera))
            .ShouldNotBeNull("antes da soltura a conexão do suporte está no grupo do guichê A");
        await PafOperacao.AgirOkAsync(imp.Cliente, propria.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(imp.Cliente, propria.AtendimentoId, "encerrar");
        return (c, imp, chamadas);
    }

    /// <summary>A colleague takes guichê A and calls: his connection gets the call, the support's must not.</summary>
    private async Task ColegaChamaNoGuicheEOSuporteNaoRecebeAsync(CenarioOperacao c, Caixa<SenhaChamada> chamadasDoSuporte)
    {
        (await _op.ContarAsync(c, db => db.SessoesEstacao.Where(v => v.EstacaoId == c.GuicheA && v.LiberadoEm == null))).ShouldBe(0);
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
        chamadasDoSuporte.Recebidos.ShouldNotContain(s => s.AtendimentoId == chamada.AtendimentoId, "a soltura tirou a conexão do suporte do grupo do guichê A");
    }

    private async Task<HttpClient> GerenteAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Guiches.Liberar, PafPermissions.Estacoes.Update);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static async Task<HttpResponseMessage> EmitirNoGuicheAsync(HttpClient guiche, Guid fila, Guid prioridade)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/paf/guiche/fichas");
        request.Content = JsonContent.Create(new EmitirFichaNoGuicheRequest(fila, prioridade));
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await guiche.SendAsync(request);
    }

    private async Task<Guid> VincularConcessaoAsync(Guid estacaoId, Guid concessaoId, DateTimeOffset expiraEm)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var vinculo = SessaoEstacao.CriarParaConcessao(estacaoId, concessaoId, expiraEm);
        db.SessoesEstacao.Add(vinculo);
        await db.SaveChangesAsync();
        return vinculo.Id;
    }

    private async Task<Suporte> SuporteAsync(SeededUser alvo)
    {
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        using var resposta = await root.PostAsJsonAsync($"{Impersonacao}/start", new
        {
            targetUserId = alvo.UserId,
            targetTenantId = _tenant.TenantId,
            reason = "teste do guichê sob impersonação",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("accessToken").GetString()!;
        return new Suporte(_tenant.Cliente(token), token, Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id));
    }

    private static async Task<string> SelecionarAsync(HttpClient cliente, Guid estacaoId, HttpStatusCode esperado)
    {
        using var resposta = await cliente.PostAsJsonAsync(RotaSelecao, new { estacaoId, liberarPendencia = false });
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(esperado, corpo);
        return corpo;
    }

    private async Task<SessaoEstacao> VinculoAtivoAsync(CenarioOperacao c, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, c.UnidadeId).SessoesEstacao.AsNoTracking()
            .SingleAsync(v => v.EstacaoId == estacaoId && v.LiberadoEm == null);
    }

    private async Task<List<AtoImpersonacao>> AtosAsync(CenarioOperacao c, Guid concessao)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, c.UnidadeId).AtosImpersonacao.AsNoTracking().Where(a => a.ConcessaoId == concessao).ToListAsync();
    }

    private sealed record Suporte(HttpClient Cliente, string Token, Guid Concessao) : IDisposable
    {
        public void Dispose() => Cliente.Dispose();
    }
}
