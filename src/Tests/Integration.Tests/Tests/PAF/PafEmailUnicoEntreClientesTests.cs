using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Data;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Identity.Services;
using FSH.Modules.Multitenancy.Features.v1.CreateTenant;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Provisionamento;
using FSH.Modules.PAF.Features.v1.Usuarios.CreateUsuario;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// O1: o e-mail de um usuário de cliente é único entre todas as empresas clientes, porque o login não pede mais a
/// empresa. O root fica fora: o operador pode ter o mesmo endereço de um cliente. Nenhuma recusa diz qual empresa
/// usa o e-mail.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEmailUnicoEntreClientesTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Senha = "Test@1234!";
    private readonly PafTenantCliente _empresaA = new(factory, "paf-mail-a");
    private readonly PafTenantCliente _empresaB = new(factory, "paf-mail-b");

    public async Task InitializeAsync()
    {
        await _empresaA.InicializarAsync();
        await _empresaB.InicializarAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CadastroDoPafEDoKit_EmailDeOutraEmpresa_RecusadosSemNomearAEmpresa()
    {
        var naEmpresaA = await _empresaA.CriarUsuarioAsync(_empresaA.UnidadeInicial);
        var unidadeB = await _empresaB.CriarUnidadeAsync(limiteSessoes: 5);

        using var paf = await PafApi.EnviarAsync(_empresaB.Admin, HttpMethod.Post, "/usuarios", null, new CreateUsuarioPafCommand(
            "Outra", "Pessoa", naEmpresaA.Email, Senha, Senha, null, null, PapeisPadrao.Atendente, [unidadeB.UnidadeId], false));
        var corpoPaf = await paf.Content.ReadAsStringAsync();
        paf.StatusCode.ShouldBe(HttpStatusCode.Conflict, corpoPaf);
        TextosDoJson(corpoPaf).ShouldContain(CreateUsuarioPafCommandHandler.MensagemEmailEmUso);
        corpoPaf.ShouldNotContain(_empresaA.TenantId);

        // Upper case and surrounding spaces still name the same mailbox.
        using var kit = await _empresaB.Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Outra",
            lastName = "Pessoa",
            email = $"  {naEmpresaA.Email.ToUpperInvariant()} ",
            userName = $"dup-{Guid.NewGuid():N}"[..20],
            password = Senha,
            confirmPassword = Senha,
        });
        var corpoKit = await kit.Content.ReadAsStringAsync();
        kit.StatusCode.ShouldBe(HttpStatusCode.BadRequest, corpoKit);
        TextosDoJson(corpoKit).ShouldContain(ClientTenantEmailValidator.Mensagem);
        corpoKit.ShouldNotContain(_empresaA.TenantId);
        (await UsuariosComEmailAsync(naEmpresaA.Email)).ShouldBe(1);
    }

    [Fact]
    public async Task CriacaoDeEmpresa_AdminComEmailDeOutraEmpresa_RecusadaAntesDeCriar()
    {
        var naEmpresaA = await _empresaA.CriarUsuarioAsync(_empresaA.UnidadeInicial);
        var novoTenant = $"paf-mail-c-{Guid.NewGuid().ToString("N")[..8]}";

        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        using var criar = await CriarEmpresaAsync(root, novoTenant, naEmpresaA.Email);

        var corpo = await criar.Content.ReadAsStringAsync();
        criar.StatusCode.ShouldBe(HttpStatusCode.BadRequest, corpo);
        TextosDoJson(corpo).ShouldContain(CreateTenantCommandValidator.MensagemEmailEmUso);
        corpo.ShouldNotContain(_empresaA.TenantId);
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(novoTenant)).ShouldBeNull();
    }

    // The operator creating a tenant with his own e-mail is the usual case: root is outside the index.
    [Fact]
    public async Task Raiz_PodeRepetirOEmailDeUmCliente_EOClientePodeRepetirODaRaiz()
    {
        var naEmpresaA = await _empresaA.CriarUsuarioAsync(_empresaA.UnidadeInicial);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();

        using var naRaiz = await root.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Operador",
            lastName = "Raiz",
            email = naEmpresaA.Email,
            userName = $"op-{Guid.NewGuid():N}"[..20],
            password = Senha,
            confirmPassword = Senha,
        });
        naRaiz.StatusCode.ShouldBe(HttpStatusCode.Created, await naRaiz.Content.ReadAsStringAsync());

        var operador = await IdentityUserSeeder.CreateLoginableUserAsync(factory, root, "paf-op-raiz");
        var novoTenant = $"paf-mail-r-{Guid.NewGuid().ToString("N")[..8]}";
        using var criar = await CriarEmpresaAsync(root, novoTenant, operador.Email);
        criar.StatusCode.ShouldBe(HttpStatusCode.Created, await criar.Content.ReadAsStringAsync());

        // The provisioning job seeds that admin through UserManager, so the user validator ran on it too.
        await TenantProvisioningWait.WaitForProvisioningAsync(root, novoTenant);
        (await UsuariosComEmailAsync(operador.Email)).ShouldBe(2);
    }

    [Fact]
    public async Task Indice_RecusaDuplicataEntreClientes_EIgnoraARaiz()
    {
        var naEmpresaA = await _empresaA.CriarUsuarioAsync(_empresaA.UnidadeInicial);
        var naEmpresaB = await _empresaB.CriarUsuarioAsync(_empresaB.UnidadeInicial);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var naRaiz = await IdentityUserSeeder.CreateLoginableUserAsync(factory, root, "paf-idx-raiz");
        var normalizado = naEmpresaA.Email.ToUpperInvariant();

        // Straight to the table, past every validation: only the index stands.
        var violacao = await Should.ThrowAsync<PostgresException>(() =>
            ExecutarAsync($"""UPDATE identity."Users" SET "NormalizedEmail" = {normalizado} WHERE "Id" = {naEmpresaB.UserId}"""));
        violacao.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        violacao.ConstraintName.ShouldBe(ApplicationUserConfig.ClientEmailIndexName);

        (await ExecutarAsync($"""UPDATE identity."Users" SET "NormalizedEmail" = {normalizado} WHERE "Id" = {naRaiz.UserId}"""))
            .ShouldBe(1);
    }

    // Two tenants creating the same address at once both pass the validator; the index refuses the second. That
    // one must get the validator's 400, not a 500. Here no validator runs at all, which is the race's second request.
    [Fact]
    public async Task Corrida_IndiceRecusa_ComAMesmaRespostaDoValidador()
    {
        var naEmpresaA = await _empresaA.CriarUsuarioAsync(_empresaA.UnidadeInicial);
        using var scope = factory.Services.CreateScope();
        var empresaB = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(_empresaB.TenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(empresaB!);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        userManager.UserValidators.Clear();

        var recusa = await Should.ThrowAsync<CustomException>(() => userManager.CreateAsync(
            new FshUser { Email = naEmpresaA.Email, UserName = $"corrida-{Guid.NewGuid():N}"[..20], IsActive = true },
            Senha));

        recusa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        recusa.ErrorMessages.ShouldBe([ClientTenantEmailValidator.Mensagem]);
        recusa.Message.ShouldNotContain(_empresaA.TenantId);
    }

    // The root operator edits a client user from the root context: the check follows the user's tenant, not root.
    [Fact]
    public async Task Validador_UsaOTenantDoUsuario_MesmoNoContextoDaRaiz()
    {
        var naEmpresaA = await _empresaA.CriarUsuarioAsync(_empresaA.UnidadeInicial);
        var naEmpresaB = await _empresaB.CriarUsuarioAsync(_empresaB.UnidadeInicial);
        using var scope = factory.Services.CreateScope();
        var db = ContextoRaiz(scope);
        var usuarioB = await db.Users.IgnoreQueryFilters([ClientTenantEmailDirectory.TenantFilterKey]).SingleAsync(u => u.Id == naEmpresaB.UserId);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();

        var resultado = await userManager.SetEmailAsync(usuarioB, naEmpresaA.Email);

        resultado.Succeeded.ShouldBeFalse();
        resultado.Errors.Select(e => e.Code).ShouldContain(ClientTenantEmailValidator.ErrorCode);
    }

    // ClientTenantEmailDirectory turns off the tenant filter by its key; if Finbuckle renamed it, the lookup would
    // silently stay inside one tenant and the login by e-mail would find nobody.
    [Fact]
    public void Usuarios_FiltroDeTenantTemAChaveQueABuscaDesliga()
    {
        using var scope = factory.Services.CreateScope();
        var db = ContextoRaiz(scope);

        var filtros = db.Model.FindEntityType(typeof(FshUser))!.GetDeclaredQueryFilters();

        filtros.Select(f => f.Key).ShouldContain(ClientTenantEmailDirectory.TenantFilterKey);
    }

    private static Task<HttpResponseMessage> CriarEmpresaAsync(HttpClient root, string tenantId, string adminEmail) =>
        root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });

    private async Task<int> UsuariosComEmailAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var normalizado = email.ToUpperInvariant();
        return await ContextoRaiz(scope).Database
            .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM identity."Users" WHERE "NormalizedEmail" = {normalizado}""")
            .SingleAsync();
    }

    private async Task<int> ExecutarAsync(FormattableString sql)
    {
        using var scope = factory.Services.CreateScope();
        return await ContextoRaiz(scope).Database.ExecuteSqlAsync(sql);
    }

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    private static IdentityDbContext ContextoRaiz(IServiceScope scope)
    {
        var raiz = new AppTenantInfo(MultitenancyConstants.Root.Id, MultitenancyConstants.Root.Name, string.Empty, MultitenancyConstants.Root.EmailAddress);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(raiz);
        return scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    }

    private static List<string> TextosDoJson(string json)
    {
        var textos = new List<string>();
        Coletar(JsonDocument.Parse(json).RootElement, textos);
        return textos;
    }

    private static void Coletar(JsonElement elemento, List<string> textos)
    {
        switch (elemento.ValueKind)
        {
            case JsonValueKind.String:
                textos.Add(elemento.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in elemento.EnumerateArray())
                {
                    Coletar(item, textos);
                }

                break;
            case JsonValueKind.Object:
                foreach (var propriedade in elemento.EnumerateObject())
                {
                    Coletar(propriedade.Value, textos);
                }

                break;
        }
    }
}
