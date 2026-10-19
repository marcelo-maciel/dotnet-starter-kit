using System.IdentityModel.Tokens.Jwt;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Contracts.v1.Prioridades;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.PAF;

/// <summary>Calls the PAF cadastro endpoints as a real client: token, tenant header and, when given, the unit header.</summary>
internal sealed class PafApi(FshWebApplicationFactory factory)
{
    public const string Base = "/api/v1/paf";
    public const string CabecalhoUnidade = "X-Paf-Unidade";

    public static readonly AparenciaFilaDto SemAparencia = new(null, null, null);

    public Task<HttpClient> RootAsync() => new AuthHelper(factory).CreateRootAdminClientAsync();

    public async Task<Guid> RootAdminIdAsync()
    {
        using var scope = factory.Services.CreateScope();
        var root = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(root!);
        var usuarios = await scope.ServiceProvider.GetRequiredService<IUserProfileService>().GetListAsync(CancellationToken.None);
        return Guid.Parse(usuarios.Single(u => u.Email == TestConstants.RootAdminEmail).Id!);
    }

    public static async Task<HttpResponseMessage> EnviarAsync(HttpClient client, HttpMethod metodo, string rota, Guid? unidade, object? corpo)
    {
        using var request = new HttpRequestMessage(metodo, $"{Base}{rota}");
        if (unidade is { } id)
        {
            request.Headers.Add(CabecalhoUnidade, id.ToString());
        }

        if (corpo is not null)
        {
            request.Content = JsonContent.Create(corpo);
        }

        return await client.SendAsync(request);
    }

    public static async Task<T> OkAsync<T>(HttpClient client, HttpMethod metodo, string rota, Guid? unidade, object? corpo)
    {
        using var response = await EnviarAsync(client, metodo, rota, unidade, corpo);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.DeserializeAsync<T>();
    }

    /// <summary>
    /// A new unidade through the API, then granted to the caller straight in the database: creation does not
    /// add the creator to the scope, and granting through the API needs the unidade in the granter's scope.
    /// </summary>
    public async Task<Guid> CriarUnidadeAsync(HttpClient client)
    {
        var unidadeId = await OkAsync<Guid>(client, HttpMethod.Post, "/unidades", null, new CreateUnidadeCommand($"Loja {Guid.NewGuid():N}", null));
        var token = new JwtSecurityTokenHandler().ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter);
        var usuarioId = Guid.Parse(token.Subject);
        var tenantId = client.DefaultRequestHeaders.GetValues("tenant").Single();

        using var scope = factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant!);
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();
        int? ultimaOrdem = await db.UsuariosUnidade.Where(u => u.UsuarioId == usuarioId).MaxAsync(u => (int?)u.Ordem);
        db.UsuariosUnidade.Add(UsuarioUnidade.Criar(usuarioId, unidadeId, (ultimaOrdem ?? -1) + 1));
        await db.SaveChangesAsync();
        return unidadeId;
    }

    public static Task<Guid> CriarPrioridadeAsync(HttpClient client, Guid unidade, string nome) =>
        OkAsync<Guid>(client, HttpMethod.Post, "/prioridades", unidade, new CreatePrioridadeCommand(nome, 0));

    public static Task<Guid> CriarFilaAsync(HttpClient client, Guid unidade, string nome, Guid prioridadeId, string letra) =>
        OkAsync<Guid>(client, HttpMethod.Post, "/filas", unidade,
            new CreateFilaCommand(nome, nome, null, null, null, SemAparencia, [new FilaPrioridadeItem(prioridadeId, letra, 1, 0, null)]));

    public static Task<List<FilaDto>> FilasAsync(HttpClient client, Guid? unidade) =>
        OkAsync<List<FilaDto>>(client, HttpMethod.Get, "/filas", unidade, null);

    public static Task<List<PrioridadeDto>> PrioridadesAsync(HttpClient client, Guid unidade) =>
        OkAsync<List<PrioridadeDto>>(client, HttpMethod.Get, "/prioridades", unidade, null);
}
