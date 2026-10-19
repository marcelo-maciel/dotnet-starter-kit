using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Usuarios;
using FSH.Modules.PAF.Features.v1.Usuarios.Permissoes;
using FSH.Modules.PAF.Provisionamento;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Permissões por usuário nos quatro grupos do RF08 (C15): papel pessoal por cima do papel padrão, concessão limitada
/// ao que quem concede tem e aos usuários que cabem no escopo dele, cópia de perfil com o conjunto exato.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafPermissoesUsuarioTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Senha = "Test@1234!";
    private const string FichasDoDia = "/fichas/atendimentos";
    private readonly PafTenantCliente _tenant = new(factory, "paf-perm");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MarcarSoParaX_XPassaEYComOMesmoPapelPadraoLeva403_DesmarcarVoltaA403NaProximaRequisicao()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (x, emailX) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        var (_, emailY) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);

        await DefinirAsync(_tenant.Admin, x, PafPermissions.FichasDoDia.View);

        using var clienteX = await ClienteAsync(emailX);
        using var clienteY = await ClienteAsync(emailY);
        (await StatusAsync(clienteX, HttpMethod.Get, FichasDoDia, null)).ShouldBe(HttpStatusCode.OK);
        (await StatusAsync(clienteY, HttpMethod.Get, FichasDoDia, null)).ShouldBe(HttpStatusCode.Forbidden);

        // Same token: the kit checks the permission per request from its cache, which the role write invalidates.
        await DefinirAsync(_tenant.Admin, x);
        (await StatusAsync(clienteX, HttpMethod.Get, FichasDoDia, null)).ShouldBe(HttpStatusCode.Forbidden);
        (await LerAsync(_tenant.Admin, x)).Individuais.ShouldBeEmpty();
    }

    [Fact]
    public async Task Leitura_MostraGruposPapelPadraoIndividuaisEOQueOChamadorAltera()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (x, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        await DefinirAsync(_tenant.Admin, x, PafPermissions.Atendimentos.Vip, PafPermissions.Estatisticas.View);

        var lido = await LerAsync(_tenant.Admin, x);

        lido.Grupos.Select(g => g.Nome).ShouldBe(GruposPermissoesPaf.Todos.Select(g => g.Nome));
        lido.DosPapeis.ShouldBe(PapeisPadrao.PermissoesAtendente.Order(StringComparer.Ordinal), ignoreOrder: false, customMessage: "o papel padrão e as básicas do kit, nada além");
        lido.Individuais.ShouldBe([PafPermissions.Atendimentos.Vip, PafPermissions.Estatisticas.View], ignoreOrder: true);
        // The initial admin holds the kit Admin role, which carries every client-side permission.
        lido.AlteraveisPeloChamador.ShouldBe(GruposPermissoesPaf.Nomes, ignoreOrder: true);
        (await PapeisPessoaisAsync(x)).ShouldBe(1);
    }

    [Fact]
    public async Task AdministradorSemEstatisticas_NaoConcedeNemRetira_EConcedeOQueTem()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (x, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        (await StatusAsync(filial, HttpMethod.Put, $"/usuarios/{x}/permissoes", Corpo(PafPermissions.Estatisticas.View)))
            .ShouldBe(HttpStatusCode.Forbidden);
        (await LerAsync(_tenant.Admin, x)).Individuais.ShouldBeEmpty("recusado sem efeito");
        (await PapeisPessoaisAsync(x)).ShouldBe(0, "a recusa vem antes de criar o papel pessoal");

        // What he holds he grants; what he lacks, given by the tenant admin, he can neither take away nor overwrite.
        await DefinirAsync(filial, x, PafPermissions.Usuarios.View);
        await DefinirAsync(_tenant.Admin, x, PafPermissions.Usuarios.View, PafPermissions.Estatisticas.View);
        (await StatusAsync(filial, HttpMethod.Put, $"/usuarios/{x}/permissoes", Corpo(PafPermissions.Usuarios.View)))
            .ShouldBe(HttpStatusCode.Forbidden);
        await DefinirAsync(filial, x, PafPermissions.Estatisticas.View);
        (await LerAsync(_tenant.Admin, x)).Individuais.ShouldBe([PafPermissions.Estatisticas.View]);
        (await LerAsync(filial, x)).AlteraveisPeloChamador.ShouldNotContain(PafPermissions.Estatisticas.View);
    }

    [Fact]
    public async Task AdminDaFilial_EditandoORegional_403SemEfeito()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (regional, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId, u2.UnidadeId);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        (await StatusAsync(filial, HttpMethod.Put, $"/usuarios/{regional}/permissoes", Corpo(PafPermissions.Usuarios.View)))
            .ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(filial, HttpMethod.Get, $"/usuarios/{regional}/permissoes", null)).ShouldBe(HttpStatusCode.Forbidden);
        (await LerAsync(_tenant.Admin, regional)).Individuais.ShouldBeEmpty();
    }

    [Fact]
    public async Task PermissaoForaDosGruposOuUsuarioInexistente_Recusa()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (x, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);

        foreach (var invalida in new[] { PafPermissions.Contratos.Manage, PafPermissions.Unidades.Operar, "Permissions.Users.Update", "", "qualquer" })
        {
            (await StatusAsync(_tenant.Admin, HttpMethod.Put, $"/usuarios/{x}/permissoes", Corpo(invalida)))
                .ShouldBe(HttpStatusCode.BadRequest, invalida);
        }

        (await StatusAsync(_tenant.Admin, HttpMethod.Put, $"/usuarios/{Guid.NewGuid()}/permissoes", Corpo()))
            .ShouldBe(HttpStatusCode.NotFound);
        (await PapeisPessoaisAsync(x)).ShouldBe(0);
    }

    [Fact]
    public async Task PrimeiraGravacaoConcorrente_UmPapelPessoalSo()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (x, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);

        var status = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => StatusAsync(_tenant.Admin, HttpMethod.Put, $"/usuarios/{x}/permissoes",
            Corpo(i % 2 == 0 ? PafPermissions.Atendimentos.Vip : PafPermissions.FichasDoDia.View))));

        status.ShouldAllBe(s => s == HttpStatusCode.OK);
        (await PapeisPessoaisAsync(x)).ShouldBe(1);
        (await LerAsync(_tenant.Admin, x)).Individuais.Count.ShouldBe(1, "uma gravação inteira, a última, nunca a mistura");
    }

    [Fact]
    public async Task CopiarPerfil_CopiaOConjuntoExato_EOrigemSemIndividuaisLimpaODestino()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (origem, _) = await CriarAsync(PapeisPadrao.Gerente, u1.UnidadeId);
        var (destino, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        var (semIndividuais, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        await DefinirAsync(_tenant.Admin, origem, PafPermissions.Midias.Create, PafPermissions.Usuarios.View);
        await DefinirAsync(_tenant.Admin, destino, PafPermissions.Parametros.UpdateUnidade);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{destino}/copiar-perfil", null,
            new CopiarPerfilUsuarioPafRequest(origem, false));

        (await EfetivasAsync(destino)).ShouldBe(await EfetivasAsync(origem), ignoreOrder: true);
        (await LerAsync(_tenant.Admin, destino)).Individuais.ShouldBe([PafPermissions.Midias.Create, PafPermissions.Usuarios.View], ignoreOrder: true);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{destino}/copiar-perfil", null,
            new CopiarPerfilUsuarioPafRequest(semIndividuais, false));
        (await EfetivasAsync(destino)).ShouldBe(await EfetivasAsync(semIndividuais), ignoreOrder: true);
    }

    [Fact]
    public async Task CopiarPerfil_IndividualQueOChamadorNaoTem_403SemEfeito()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (origem, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        var (destino, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        await DefinirAsync(_tenant.Admin, origem, PafPermissions.Estatisticas.View);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        (await StatusAsync(filial, HttpMethod.Post, $"/usuarios/{destino}/copiar-perfil", new CopiarPerfilUsuarioPafRequest(origem, false)))
            .ShouldBe(HttpStatusCode.Forbidden);
        (await LerAsync(_tenant.Admin, destino)).Individuais.ShouldBeEmpty();
        (await EfetivasAsync(destino)).ShouldNotContain(PafPermissions.Estatisticas.View);
    }

    [Fact]
    public async Task Codefi_Impersonando_DefinePermissoes_GravaUmAto()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var (x, _) = await CriarAsync(PapeisPadrao.Atendente, u1.UnidadeId);
        var alvo = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId, PafPermissions.Usuarios.View, PafPermissions.Usuarios.Manage, PafPermissions.Atendimentos.Vip);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await ImpersonarAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        using var suporte = _tenant.Cliente(token);

        await DefinirAsync(suporte, x, PafPermissions.Atendimentos.Vip);

        using var scope = factory.Services.CreateScope();
        var atos = await _tenant.Contexto(scope, u1.UnidadeId).AtosImpersonacao.AsNoTracking().Where(a => a.ConcessaoId == concessao).ToListAsync();
        atos.ShouldHaveSingleItem().AtendimentoId.ShouldBeNull();
        (await LerAsync(_tenant.Admin, x)).Individuais.ShouldBe([PafPermissions.Atendimentos.Vip]);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static DefinirPermissoesUsuarioPafRequest Corpo(params string[] permissoes) => new(permissoes);

    private static Task<Guid> DefinirAsync(HttpClient cliente, Guid usuario, params string[] permissoes) =>
        PafApi.OkAsync<Guid>(cliente, HttpMethod.Put, $"/usuarios/{usuario}/permissoes", null, Corpo(permissoes));

    private static Task<PermissoesUsuarioPafDto> LerAsync(HttpClient cliente, Guid usuario) =>
        PafApi.OkAsync<PermissoesUsuarioPafDto>(cliente, HttpMethod.Get, $"/usuarios/{usuario}/permissoes", null, null);

    private static async Task<HttpStatusCode> StatusAsync(HttpClient cliente, HttpMethod metodo, string rota, object? corpo)
    {
        using var resposta = await PafApi.EnviarAsync(cliente, metodo, rota, null, corpo);
        return resposta.StatusCode;
    }

    private async Task<(Guid Id, string Email)> CriarAsync(string papel, params Guid[] unidades)
    {
        var email = $"c15-{Guid.NewGuid().ToString("N")[..10]}@cliente.test";
        var id = await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, "/usuarios", null,
            new CreateUsuarioPafCommand("Pessoa", "Teste", email, Senha, Senha, null, null, papel, unidades, false));
        return (id, email);
    }

    private async Task<HttpClient> ClienteAsync(string email)
    {
        var login = await _tenant.LoginAsync(email, Senha);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        return _tenant.Cliente((await login.Content.ReadFromJsonAsync<TokenResult>())!.AccessToken);
    }

    // A filial admin as RF08 describes him: scope [unidade], the user permissions and the basic ones, no statistics.
    private async Task<HttpClient> AdminDaFilialAsync(Guid unidade)
    {
        var admin = await _tenant.CriarUsuarioAsync(unidade);
        await _tenant.DarPapelAsync(admin.UserId, PafPermissions.Usuarios.View, PafPermissions.Usuarios.Manage,
            PafPermissions.UsuariosUnidade.View, PafPermissions.UsuariosUnidade.Update);
        return _tenant.Cliente((await _tenant.EntrarAsync(admin)).AccessToken);
    }

    private async Task<string> ImpersonarAsync(HttpClient ator, string alvo)
    {
        using var resposta = await ator.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/impersonation/start", new
        {
            targetUserId = alvo,
            targetTenantId = _tenant.TenantId,
            reason = "teste das permissões por usuário",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<int> PapeisPessoaisAsync(Guid usuario)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        var pagina = await scope.ServiceProvider.GetRequiredService<IRoleService>()
            .GetRolesAsync(1, 200, PermissoesIndividuais.NomeDoPapel(usuario), CancellationToken.None);
        return pagina.Items.Count(r => r.Name == PermissoesIndividuais.NomeDoPapel(usuario));
    }

    private async Task<List<string>> EfetivasAsync(Guid usuario)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        return await scope.ServiceProvider.GetRequiredService<IUserPermissionService>()
            .GetPermissionsAsync(usuario.ToString(), CancellationToken.None) ?? [];
    }
}
