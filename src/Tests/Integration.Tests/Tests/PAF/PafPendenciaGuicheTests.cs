using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Atendimento left on a guichê by a released link (seção 5, RF04): its own user resumes it on a new session;
/// another user only selects the guichê by releasing it in the same act (RF12 cancel), and without releasing is
/// refused and leaves the guichê free for the owner.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafPendenciaGuicheTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaSelecao = "/api/v1/paf/sessao/guiche";
    private const string RotaGuiches = "/api/v1/paf/guiche/guiches";
    private const string RotaPosto = "/api/v1/paf/guiche/posto";

    private readonly PafTenantCliente _tenant = new(factory, "paf-pend");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Colega_SemLiberar_Recusado_EDonoRetomaNaSessaoNova()
    {
        var (c, dono, chamada) = await PendenciaNoGuicheAAsync();
        using var colega = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);

        var oferecido = (await LerOkAsync<List<GuicheDaUnidadeDto>>(colega, RotaGuiches)).Single(g => g.Id == c.GuicheA);
        oferecido.Pendencia.ShouldBe(chamada.Senha, "a seleção mostra a pendência de outro usuário");
        oferecido.Ocupante.ShouldBeNull("o vínculo do dono foi solto pelo login novo");

        using (var recusa = await colega.PostAsJsonAsync(RotaSelecao, new { estacaoId = c.GuicheA, liberarPendencia = false }))
        {
            recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            PafSessaoLicencaTests.Detalhe(await recusa.Content.ReadAsStringAsync())
                .ShouldBe(SetGuicheCommandHandler.MensagemPendencia("Guichê 1", chamada.Senha));
        }

        (await LerOkAsync<PostoGuicheDto>(colega, RotaPosto)).EstacaoId.ShouldBeNull("a recusa não liga o colega ao guichê");
        (await _op.AtendimentoAsync(c, chamada.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);

        using var donoDeNovo = _tenant.Cliente((await _tenant.EntrarAsync(dono)).AccessToken);
        (await LerOkAsync<List<GuicheDaUnidadeDto>>(donoDeNovo, RotaGuiches)).Single(g => g.Id == c.GuicheA)
            .Pendencia.ShouldBeNull("a pendência do próprio usuário não é de outro");
        await SelecionarOkAsync(donoDeNovo, c.GuicheA, liberarPendencia: false);

        var posto = await LerOkAsync<PostoGuicheDto>(donoDeNovo, RotaPosto);
        posto.Atendimento.ShouldNotBeNull("o dono retoma o atendimento pendente");
        posto.Atendimento.AtendimentoId.ShouldBe(chamada.AtendimentoId);
        posto.Atendimento.Estado.ShouldBe(nameof(EstadoAtendimento.Chamado));
        (await PafOperacao.AgirOkAsync(donoDeNovo, chamada.AtendimentoId, "iniciar")).Estado.ShouldBe(nameof(EstadoAtendimento.EmAtendimento));
    }

    [Fact]
    public async Task Colega_Liberando_CancelaAPendencia_ESeleciona()
    {
        var (c, dono, chamada) = await PendenciaNoGuicheAAsync();
        using var colega = _tenant.Cliente((await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken);

        await SelecionarOkAsync(colega, c.GuicheA, liberarPendencia: true);

        var atendimento = await _op.AtendimentoAsync(c, chamada.AtendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Cancelado);
        atendimento.CanceladoEm.ShouldNotBeNull();
        atendimento.Versao.ShouldBe(chamada.Versao + 1);

        var posto = await LerOkAsync<PostoGuicheDto>(colega, RotaPosto);
        posto.EstacaoId.ShouldBe(c.GuicheA);
        posto.Atendimento.ShouldBeNull();

        // The guichê is free of atendimentos again: the colleague calls on it.
        using var totem = await _op.TotemAsync(c);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(colega)).AtendimentoId.ShouldNotBe(chamada.AtendimentoId);

        using var donoDeNovo = _tenant.Cliente((await _tenant.EntrarAsync(dono)).AccessToken);
        using var tarde = await donoDeNovo.PostAsJsonAsync(RotaSelecao, new { estacaoId = c.GuicheA, liberarPendencia = false });
        tarde.StatusCode.ShouldBe(HttpStatusCode.Conflict, "o guichê agora é do colega");
    }

    // The posto read never shows another user's atendimento as the caller's, even if a link to that guichê
    // exists without the release (a link written outside the selection).
    [Fact]
    public async Task Posto_NaoMostraAtendimentoDeOutroUsuario()
    {
        var (c, _, _) = await PendenciaNoGuicheAAsync();
        var token = (await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(c.UnidadeId))).AccessToken;
        using var colega = _tenant.Cliente(token);
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            db.SessoesEstacao.Add(SessaoEstacao.CriarParaSessao(c.GuicheA, PafTenantCliente.SessaoDoToken(token)));
            await db.SaveChangesAsync();
        }

        var posto = await LerOkAsync<PostoGuicheDto>(colega, RotaPosto);
        posto.EstacaoId.ShouldBe(c.GuicheA);
        posto.Atendimento.ShouldBeNull();
    }

    // The owner calls a ficha on guichê A, then logs in again: the login releases the old session's link and
    // the Chamado stays on the guichê as a pendência.
    private async Task<(CenarioOperacao Cenario, SeededUser Dono, ChamadaDto Chamada)> PendenciaNoGuicheAAsync()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var dono = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        using var primeira = _tenant.Cliente((await _tenant.EntrarAsync(dono)).AccessToken);
        await SelecionarOkAsync(primeira, c.GuicheA, liberarPendencia: false);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(primeira);

        await _tenant.EntrarAsync(dono);
        return (c, dono, chamada);
    }

    private static async Task SelecionarOkAsync(HttpClient cliente, Guid guiche, bool liberarPendencia)
    {
        using var selecao = await cliente.PostAsJsonAsync(RotaSelecao, new { estacaoId = guiche, liberarPendencia });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
    }

    private static async Task<T> LerOkAsync<T>(HttpClient cliente, string rota)
    {
        using var resposta = await cliente.GetAsync(rota);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<T>())!;
    }
}
