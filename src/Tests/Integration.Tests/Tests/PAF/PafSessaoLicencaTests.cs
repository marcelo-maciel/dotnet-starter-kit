using System.Text.Json;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Licença na primeira fatia (RF22, ADR 0008): sem cascata, paga a unidade principal. Roda contra o host real,
/// num tenant de cliente provisionado pela API, sem Valkey no harness: a contagem é a do PostgreSQL.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafSessaoLicencaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private readonly PafTenantCliente _tenant = new(factory, "paf-lic");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // First test of ADR 0008: if the closed behavior were not applied by Mediator, this login would succeed.
    [Fact]
    public async Task Login_UnidadeSemContrato_RecusadoSemSessaoNemToken()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);

        var resposta = await _tenant.LoginAsync(usuario.Email, usuario.Password);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Detalhe(corpo).ShouldBe(Licencas.MensagemSemContrato);
        corpo.ShouldNotContain("accessToken", Case.Insensitive);
        await SessoesDoKitAsync(usuario.UserId).ShouldBeAsync(0);
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_LimiteAtingidoPorOutroUsuario_RecusadoSemSessao()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var primeiro = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var segundo = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await _tenant.EntrarAsync(primeiro);

        var resposta = await _tenant.LoginAsync(segundo.Email, segundo.Password);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(Licencas.MensagemLimite);
        await SessoesDoKitAsync(segundo.UserId).ShouldBeAsync(0);
    }

    [Fact]
    public async Task Login_UnidadeInativa_RecusadoComMensagemPropria()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" = {unidade.UnidadeId}""");

        var resposta = await _tenant.LoginAsync(usuario.Email, usuario.Password);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(Licencas.MensagemSemUnidadeAtiva);
    }

    // RF01: without the password, nobody learns that the user exists or that his unit has no balance.
    [Fact]
    public async Task SenhaErrada_UnidadeSemSaldo_MesmaRespostaDaCredencialInvalida()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);

        var senhaErrada = await _tenant.LoginAsync(usuario.Email, "Senha@Errada1");
        var inexistente = await _tenant.LoginAsync($"ninguem-{Guid.NewGuid():N}@paf.test", "Senha@Errada1");

        senhaErrada.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        inexistente.StatusCode.ShouldBe(senhaErrada.StatusCode);
        var corpoSenha = JsonDocument.Parse(await senhaErrada.Content.ReadAsStringAsync()).RootElement;
        var corpoInexistente = JsonDocument.Parse(await inexistente.Content.ReadAsStringAsync()).RootElement;
        corpoSenha.GetProperty("title").GetString().ShouldBe(corpoInexistente.GetProperty("title").GetString());
        corpoSenha.GetProperty("detail").GetString().ShouldBe(corpoInexistente.GetProperty("detail").GetString());
        corpoSenha.GetProperty("detail").GetString().ShouldNotBe(Licencas.MensagemSemContrato);
    }

    // Also proves the user's own earlier session counts as available: limit 1, second login accepted.
    [Fact]
    public async Task SegundoLogin_RevogaAPrimeiraSessao()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);

        var primeiro = await _tenant.EntrarAsync(usuario);
        using (var clientePrimeiro = _tenant.Cliente(primeiro.AccessToken))
        {
            (await clientePrimeiro.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var segundo = await _tenant.EntrarAsync(usuario);

        using var antigo = _tenant.Cliente(primeiro.AccessToken);
        using var novo = _tenant.Cliente(segundo.AccessToken);
        (await antigo.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await novo.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SessoesDoKitAsync(usuario.UserId).ShouldBeAsync(1);
        var atividades = await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId));
        atividades.Select(a => a.SessaoId).ShouldBe([PafTenantCliente.SessaoDoToken(segundo.AccessToken)]);
        atividades.Single().UnidadeId.ShouldBe(unidade.UnidadeId);
    }

    [Fact]
    public async Task OperadorRaiz_LoginSemOcuparLicenca()
    {
        var token = await new AuthHelper(factory).GetRootAdminTokenAsync();

        var sessaoId = PafTenantCliente.SessaoDoToken(token.AccessToken);
        var banco = new PafBanco(factory);
        await banco.InicializarAsync();
        using var scope = factory.Services.CreateScope();
        var linhas = await banco.Contexto(scope, unidadeCorrente: null).Database
            .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM paf."AtividadesSessao" WHERE "SessaoId" = {sessaoId}""")
            .SingleAsync();
        linhas.ShouldBe(0);
        using var raiz = await new AuthHelper(factory).CreateRootAdminClientAsync();
        (await raiz.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Renovacao_SessaoViva_MantemClaimESessao()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);

        var resposta = await _tenant.RenovarAsync(token);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var renovado = (await resposta.Content.ReadFromJsonAsync<TokenRefreshResult>())!;
        PafTenantCliente.SessaoDoToken(renovado.Token).ShouldBe(PafTenantCliente.SessaoDoToken(token.AccessToken));
        using var cliente = _tenant.Cliente(renovado.Token);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarcaVencidaVagaTomada_RequisicaoERenovacaoRecusadasSemRevogar()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var dono = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var outro = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var tokenDono = await _tenant.EntrarAsync(dono);
        await EnvelhecerMarcaAsync(tokenDono);

        // The expired mark freed the seat, so another user takes it; reviving the first one is now refused, by the
        // request (S06: re-occupation through the cascade, 401 with the cause) and by the refresh.
        await _tenant.EntrarAsync(outro);
        using (var cliente = _tenant.Cliente(tokenDono.AccessToken))
        {
            using var requisicao = await cliente.GetAsync(Perfil);
            requisicao.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            Detalhe(await requisicao.Content.ReadAsStringAsync()).ShouldBe(Licencas.MensagemLimite);
        }

        var renovacao = await _tenant.RenovarAsync(tokenDono);

        renovacao.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Detalhe(await renovacao.Content.ReadAsStringAsync()).ShouldBe(Licencas.MensagemLimite);
        // S05: the refusal comes before the token is issued, so the session stays for a later refresh with a seat.
        await SessoesDoKitAsync(dono.UserId).ShouldBeAsync(1);
    }

    [Fact]
    public async Task MarcaVencida_RenovacaoComSaldo_ReocupaALicenca()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        await EnvelhecerMarcaAsync(token);

        var resposta = await _tenant.RenovarAsync(token);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var renovado = (await resposta.Content.ReadFromJsonAsync<TokenRefreshResult>())!;
        using var cliente = _tenant.Cliente(renovado.Token);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TrocaDeUnidade_ForaDoEscopo_Recusada()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var alheia = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        var resposta = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/unidade-corrente", new { unidadeId = alheia.UnidadeId });

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(SetUnidadeCorrenteCommandHandler.MensagemForaDoEscopo);
    }

    // No Valkey in the harness: the switch is refused, as seção 9 decides for the degraded mode.
    [Fact]
    public async Task TrocaDeUnidade_SemValkey_RecusadaComoIndisponivel()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var segunda = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId, segunda.UnidadeId);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        var resposta = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/unidade-corrente", new { unidadeId = segunda.UnidadeId });

        resposta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(SetUnidadeCorrenteCommandHandler.MensagemTrocaIndisponivel);
    }

    [Fact]
    public async Task UsuarioDeTenantClienteSemUnidade_LoginRecusadoComMensagemPropria()
    {
        var usuario = await _tenant.CriarUsuarioAsync();

        var resposta = await _tenant.LoginAsync(usuario.Email, usuario.Password);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var corpo = await resposta.Content.ReadAsStringAsync();
        Detalhe(corpo).ShouldBe(GenerateTokenLicenseBehavior.MensagemSemUnidade);
        corpo.ShouldNotContain("accessToken", Case.Insensitive);
        await SessoesDoKitAsync(usuario.UserId).ShouldBeAsync(0);
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
        // The refusal comes after the password check (RF01): a wrong password still gets the generic 401.
        (await _tenant.LoginAsync(usuario.Email, "Senha@Errada1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Records which runs first, the kit's ValidationBehavior or the PAF login behavior (ADR 0008 asked to confirm).
    [Fact]
    public async Task Login_EmailVazio_ValidacaoDoKitRodaAntesDaLicenca()
    {
        var resposta = await _tenant.LoginAsync(string.Empty, "qualquer");

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task EnvelhecerMarcaAsync(TokenResult token)
    {
        var sessaoId = PafTenantCliente.SessaoDoToken(token.AccessToken);
        var linhas = await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '11 minutes' WHERE "SessaoId" = {sessaoId}
            """);
        linhas.ShouldBe(1);
    }

    private async Task<int> SessoesDoKitAsync(string usuarioId)
    {
        var resposta = await _tenant.Admin.GetAsync($"{TestConstants.IdentityBasePath}/users/{usuarioId}/sessions");
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.GetArrayLength();
    }

    internal static string? Detalhe(string corpo) =>
        JsonDocument.Parse(corpo).RootElement.GetProperty("detail").GetString();
}

internal static class TarefaInteiraExtensions
{
    public static async Task ShouldBeAsync(this Task<int> tarefa, int esperado) => (await tarefa).ShouldBe(esperado);
}
