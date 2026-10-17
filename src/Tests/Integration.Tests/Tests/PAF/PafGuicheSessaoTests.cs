using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>Seleção de guichê depois do login e logout pelo servidor (ADR 0006, seção 5), pelo host real.</summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafGuicheSessaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaGuiche = "/api/v1/paf/sessao/guiche";
    private const string RotaUnidade = "/api/v1/paf/sessao/unidade-corrente";
    private const string RotaLogout = "/api/v1/paf/sessao/logout";
    private readonly PafTenantCliente _tenant = new(factory, "paf-gui");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Guiche_SegundaSessaoNoMesmoGuiche_RecusadaInformandoQuemEsta()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var (ana, clienteAna) = await EntrarAsync(unidade);
        var (_, clienteBia) = await EntrarAsync(unidade);

        (await Selecionar(clienteAna, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var segunda = await Selecionar(clienteBia, unidade.GuicheA);

        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var detalhe = PafSessaoLicencaTests.Detalhe(await segunda.Content.ReadAsStringAsync());
        // The seeder registers userName as the e-mail's local part.
        detalhe.ShouldBe(SetGuicheCommandHandler.MensagemEmUso("Guichê 1", ana.Email.Split('@')[0]));
    }

    [Fact]
    public async Task Guiche_TrocarDeGuiche_SoltaOAnteriorNoMesmoAto()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var (_, clienteAna) = await EntrarAsync(unidade);
        var (_, clienteBia) = await EntrarAsync(unidade);
        (await Selecionar(clienteAna, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Selecionar(clienteAna, unidade.GuicheB)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Selecionar(clienteBia, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Choosing the guichê it already holds changes nothing.
        (await Selecionar(clienteAna, unidade.GuicheB)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await VinculosAtivosAsync(unidade.GuicheA)).ShouldBe(1);
        (await VinculosAtivosAsync(unidade.GuicheB)).ShouldBe(1);
    }

    [Fact]
    public async Task Guiche_ComAtendimentoEmCurso_TrocaDeGuicheEDeUnidadeRecusadas()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var outra = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId, outra.UnidadeId);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        (await Selecionar(cliente, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _tenant.ChamarNoGuicheAsync(unidade);

        var trocaGuiche = await Selecionar(cliente, unidade.GuicheB);
        var trocaUnidade = await cliente.PostAsJsonAsync(RotaUnidade, new { unidadeId = outra.UnidadeId });

        trocaGuiche.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        PafSessaoLicencaTests.Detalhe(await trocaGuiche.Content.ReadAsStringAsync()).ShouldBe(SetGuicheCommandHandler.MensagemAtendimentoEmCurso);
        trocaUnidade.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await VinculosAtivosAsync(unidade.GuicheA)).ShouldBe(1);
    }

    [Fact]
    public async Task Guiche_DeSessaoComMarcaVencida_SoltoEEntregueNaEscolha()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 2);
        var ana = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenAna = await _tenant.EntrarAsync(ana);
        using (var clienteAna = _tenant.Cliente(tokenAna.AccessToken))
        {
            (await Selecionar(clienteAna, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var sessaoAna = PafTenantCliente.SessaoDoToken(tokenAna.AccessToken);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '11 minutes' WHERE "SessaoId" = {sessaoAna}
            """);
        var (_, clienteBia) = await EntrarAsync(unidade);

        (await Selecionar(clienteBia, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await VinculosAtivosAsync(unidade.GuicheA)).ShouldBe(1);
    }

    [Fact]
    public async Task Guiche_DeOutraUnidade_NaoEncontrado()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var outra = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var (_, cliente) = await EntrarAsync(unidade);

        var resposta = await Selecionar(cliente, outra.GuicheA);

        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(SetGuicheCommandHandler.MensagemNaoEncontrado);
    }

    [Fact]
    public async Task Logout_RevogaSessaoESoltaLicencaEGuiche()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var ana = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var bia = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenAna = await _tenant.EntrarAsync(ana);
        using var clienteAna = _tenant.Cliente(tokenAna.AccessToken);
        (await Selecionar(clienteAna, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await clienteAna.PostAsync(RotaLogout, content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await clienteAna.GetAsync($"{TestConstants.IdentityBasePath}/profile")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await VinculosAtivosAsync(unidade.GuicheA)).ShouldBe(0);
        (await _tenant.AtividadesAsync(Guid.Parse(ana.UserId))).ShouldBeEmpty();
        // Limit 1: the seat Ana held is free again.
        using var clienteBia = _tenant.Cliente((await _tenant.EntrarAsync(bia)).AccessToken);
        (await Selecionar(clienteBia, unidade.GuicheA)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Logout_ComMarcaVencida_AindaRevogaASessaoNoServidor()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var ana = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenAna = await _tenant.EntrarAsync(ana);
        var sessaoAna = PafTenantCliente.SessaoDoToken(tokenAna.AccessToken);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '11 minutes' WHERE "SessaoId" = {sessaoAna}
            """);
        using var clienteAna = _tenant.Cliente(tokenAna.AccessToken);

        (await clienteAna.PostAsync(RotaLogout, content: null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Revoked in the kit: the refresh token can no longer revive the session.
        (await _tenant.RenovarAsync(tokenAna)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _tenant.AtividadesAsync(Guid.Parse(ana.UserId))).ShouldBeEmpty();
    }

    private async Task<(Integration.Tests.Tests.Sessions.SeededUser Usuario, HttpClient Cliente)> EntrarAsync(UnidadeTeste unidade)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        return (usuario, _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken));
    }

    private static Task<HttpResponseMessage> Selecionar(HttpClient cliente, Guid estacaoId) =>
        cliente.PostAsJsonAsync(RotaGuiche, new { estacaoId });

    private async Task<int> VinculosAtivosAsync(Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.AsNoTracking().CountAsync(v => v.EstacaoId == estacaoId && v.LiberadoEm == null);
    }
}
