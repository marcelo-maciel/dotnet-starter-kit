using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Domain;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Features.v1.Usuarios;
using FSH.Modules.PAF.Features.v1.Usuarios.DefinirSenha;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Senha definida pelo administrador (RF08 l.302 e l.317, C16): a senha nova entra e a antiga é recusada, todas as
/// sessões do usuário caem com as conexões, e o pedido segue a regra de alcance, a política e o histórico de senha
/// do kit sem mexer no hash nem nas sessões quando recusado.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafSenhaUsuarioTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private const string SenhaNova = "Nova@5678!";
    private readonly PafTenantCliente _tenant = new(factory, "paf-senha");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AdminDoTenant_DefineSenha_NovaEntraAntigaRecusadaESessoesEConexoesCaem()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var token = await _tenant.EntrarAsync(usuario);
        using var cliente = _tenant.Cliente(token.AccessToken);
        var conexao = await new PafOperacao(factory, _tenant).ConectarAsync(token.AccessToken);
        var fechou = Fechamento(conexao);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{usuarioId}/senha", null, Corpo(SenhaNova));

        (await Task.WhenAny(fechou, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBe(fechou, "a conexão do PafHub cai no mesmo ato");
        await conexao.DisposeAsync();
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _tenant.RenovarAsync(token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "o refresh da sessão revogada não revive");
        (await SessoesAtivasAsync(usuarioId)).ShouldBe(0);
        (await _tenant.AtividadesAsync(usuarioId)).ShouldBeEmpty();
        (await _tenant.LoginAsync(usuario.Email, usuario.Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _tenant.LoginAsync(usuario.Email, SenhaNova)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminDaFilial_DefineSenhaDoColegaDaUnidade()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var colega = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);

        await PafApi.OkAsync<Guid>(filial, HttpMethod.Post, $"/usuarios/{colega.UserId}/senha", null, Corpo(SenhaNova));

        (await _tenant.LoginAsync(colega.Email, SenhaNova)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminDaFilial_NoRegional_403SemMudarHashNemSessao()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var regional = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        var token = await _tenant.EntrarAsync(regional);
        using var cliente = _tenant.Cliente(token.AccessToken);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);
        var hashAntes = await HashAsync(regional.UserId);

        (await StatusAsync(filial, regional.UserId, SenhaNova)).ShouldBe(HttpStatusCode.Forbidden);

        (await HashAsync(regional.UserId)).ShouldBe(hashAntes);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK, "a sessão do regional continua viva");
    }

    [Fact]
    public async Task UsuarioComPermissaoQueOChamadorNaoTem_403SemMudarHash()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var colega = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(colega.UserId, PafPermissions.Unidades.Create);
        using var filial = await AdminDaFilialAsync(u1.UnidadeId);
        var hashAntes = await HashAsync(colega.UserId);

        using var resposta = await PafApi.EnviarAsync(filial, HttpMethod.Post, $"/usuarios/{colega.UserId}/senha", null, Corpo(SenhaNova));

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await resposta.Content.ReadAsStringAsync()).ShouldContain(DefinirSenhaUsuarioPafCommandHandler.MensagemAlvoComPermissaoAMais);
        (await HashAsync(colega.UserId)).ShouldBe(hashAntes);
    }

    [Fact]
    public async Task SemUsuariosManage_403SemMudarHash()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var colega = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        var leitor = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(leitor.UserId, PafPermissions.Usuarios.View);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(leitor)).AccessToken);
        var hashAntes = await HashAsync(colega.UserId);

        (await StatusAsync(cliente, colega.UserId, SenhaNova)).ShouldBe(HttpStatusCode.Forbidden);

        (await HashAsync(colega.UserId)).ShouldBe(hashAntes);
    }

    [Fact]
    public async Task SenhaFracaAtualOuDoHistorico_400SemMudarHashNemDerrubarSessao()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{usuario.UserId}/senha", null, Corpo(SenhaNova));
        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Post, $"/usuarios/{usuario.UserId}/senha", null, Corpo("Terceira@9012!"));
        var token = await _tenant.EntrarAsync(usuario with { Password = "Terceira@9012!" });
        using var cliente = _tenant.Cliente(token.AccessToken);
        var hashAntes = await HashAsync(usuario.UserId);

        (await StatusAsync(_tenant.Admin, usuario.UserId, "fraca")).ShouldBe(HttpStatusCode.BadRequest, "política de senha do kit");
        (await StatusAsync(_tenant.Admin, usuario.UserId, "semdigitoMaiuscula")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusAsync(_tenant.Admin, usuario.UserId, "Terceira@9012!")).ShouldBe(HttpStatusCode.BadRequest, "a senha atual");
        (await StatusAsync(_tenant.Admin, usuario.UserId, SenhaNova)).ShouldBe(HttpStatusCode.BadRequest, "a senha do histórico");
        (await HashAsync(usuario.UserId)).ShouldBe(hashAntes);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK, "recusa não derruba a sessão");

        // The kit's history only records changes, so the password the user was registered with is accepted again.
        (await StatusAsync(_tenant.Admin, usuario.UserId, usuario.Password)).ShouldBe(HttpStatusCode.OK);
        (await _tenant.LoginAsync(usuario.Email, usuario.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "o pedido aceito derruba a sessão");
    }

    [Fact]
    public async Task Recusas_400NaoMexemNoHashNemNaSessao()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        using var cliente = _tenant.Cliente(token.AccessToken);
        var hashAntes = await HashAsync(usuario.UserId);

        (await StatusAsync(_tenant.Admin, usuario.UserId, "fraca")).ShouldBe(HttpStatusCode.BadRequest);
        (await StatusAsync(_tenant.Admin, usuario.UserId, usuario.Password)).ShouldBe(HttpStatusCode.BadRequest, "a senha atual");
        using (var diferente = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Post, $"/usuarios/{usuario.UserId}/senha", null,
            new DefinirSenhaUsuarioPafRequest(SenhaNova, "Outra@5678!")))
        {
            diferente.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "confirmação diferente");
            (await diferente.Content.ReadAsStringAsync()).ShouldNotContain(SenhaNova, Case.Sensitive, "a resposta não ecoa a senha");
        }

        (await HashAsync(usuario.UserId)).ShouldBe(hashAntes);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SessoesAtivasAsync(Guid.Parse(usuario.UserId))).ShouldBe(1);
    }

    [Fact]
    public async Task Codefi_Impersonando_FalhaNaRevogacao_SenhaTrocadaJaTemOAto()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await _tenant.EntrarAsync(usuario);
        var alvo = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId, PafPermissions.Usuarios.View, PafPermissions.Usuarios.Manage);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await ImpersonarAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        using var suporte = _tenant.Cliente(token);
        var hashAntes = await HashAsync(usuario.UserId);

        // The revocation releases the licence row in its own transaction; failing that DELETE fails the request after
        // the kit already wrote the password, which is exactly the window R3 closes. Only this user trips it.
        var sufixo = Guid.NewGuid().ToString("N")[..12];
        await _tenant.ExecutarAsync(FormattableStringFactory.Create(
            $"CREATE FUNCTION paf.bx3_revoga_{sufixo}() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN " +
            $"IF OLD.\"UsuarioId\" = '{usuario.UserId}' THEN RAISE EXCEPTION 'falha forcada'; END IF; RETURN OLD; END $f$"));
        try
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create(
                $"CREATE TRIGGER bx3_revoga_{sufixo} BEFORE DELETE ON paf.\"AtividadesSessao\" " +
                $"FOR EACH ROW EXECUTE FUNCTION paf.bx3_revoga_{sufixo}()"));

            using var resposta = await PafApi.EnviarAsync(suporte, HttpMethod.Post, $"/usuarios/{usuario.UserId}/senha", null, Corpo(SenhaNova));

            resposta.IsSuccessStatusCode.ShouldBeFalse(await resposta.Content.ReadAsStringAsync());
        }
        finally
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP TRIGGER IF EXISTS bx3_revoga_{sufixo} ON paf.\"AtividadesSessao\""));
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP FUNCTION IF EXISTS paf.bx3_revoga_{sufixo}()"));
        }

        (await HashAsync(usuario.UserId)).ShouldNotBe(hashAntes, "a senha do kit já foi gravada quando a revogação falhou");
        using var scope = factory.Services.CreateScope();
        var atos = await _tenant.Contexto(scope, u1.UnidadeId).AtosImpersonacao.AsNoTracking().Where(a => a.ConcessaoId == concessao).ToListAsync();
        atos.ShouldHaveSingleItem("a senha trocada sob impersonação nunca fica sem o ato (D28)").AtendimentoId.ShouldBeNull();
    }

    [Fact]
    public async Task PropriaSenha_400()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var admin = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(admin.UserId, PafPermissions.Usuarios.View, PafPermissions.Usuarios.Manage);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(admin)).AccessToken);
        var hashAntes = await HashAsync(admin.UserId);

        using var resposta = await PafApi.EnviarAsync(cliente, HttpMethod.Post, $"/usuarios/{admin.UserId}/senha", null, Corpo(SenhaNova));

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await resposta.Content.ReadAsStringAsync()).ShouldContain(DefinirSenhaUsuarioPafCommandHandler.MensagemPropriaSenha);
        (await HashAsync(admin.UserId)).ShouldBe(hashAntes);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static DefinirSenhaUsuarioPafRequest Corpo(string senha) => new(senha, senha);

    // A filial admin as RF08 describes him: scope [unidade] and the user permissions only, no kit Admin role.
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
            reason = "teste da senha definida pelo administrador",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient cliente, string usuarioId, string senha)
    {
        using var resposta = await PafApi.EnviarAsync(cliente, HttpMethod.Post, $"/usuarios/{usuarioId}/senha", null, Corpo(senha));
        return resposta.StatusCode;
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

    private async Task<string?> HashAsync(string usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        return (await scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>().FindByIdAsync(usuarioId))!.PasswordHash;
    }

    private async Task<int> SessoesAtivasAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        return (await scope.ServiceProvider.GetRequiredService<ISessionService>().GetUserSessionsForAdminAsync(usuarioId.ToString(), CancellationToken.None))
            .Count(s => s.IsActive);
    }
}
