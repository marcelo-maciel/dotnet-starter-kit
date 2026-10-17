using System.Text.Json;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Who grants which unidades (RF08, decision of 17/10/2026): the tenant admin (Unidades.Create) grants any
/// active unidade of the tenant and edits any user; whoever holds only UsuariosUnidade.Update keeps the subset
/// rule, granting only inside his own scope and editing only users whose scope fits in it.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEscopoConcessaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-conc");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AdminDoTenant_ConcedeQualquerUnidadeAtiva_ForaDoProprioEscopo()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var inativa = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" = {inativa.UnidadeId}""");
        var alvo = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);

        // The tenant admin has no unidade in his own scope.
        (await PafApi.OkAsync<List<UsuarioUnidadeDto>>(_tenant.Admin, HttpMethod.Get, $"/usuarios/{AdminId()}/unidades", null, null))
            .ShouldBeEmpty();

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u2.UnidadeId, u1.UnidadeId]));
        (await PafApi.OkAsync<List<UsuarioUnidadeDto>>(_tenant.Admin, HttpMethod.Get, $"/usuarios/{alvo}/unidades", null, null))
            .ShouldBe([new UsuarioUnidadeDto(u2.UnidadeId, 0), new UsuarioUnidadeDto(u1.UnidadeId, 1)], ignoreOrder: true);

        using var comInativa = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u1.UnidadeId, inativa.UnidadeId]));
        comInativa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        PafSessaoLicencaTests.Detalhe(await comInativa.Content.ReadAsStringAsync()).ShouldBe(SetUsuarioUnidadesCommandHandler.MensagemUnidadeInvalida);

        using var inexistente = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [Guid.NewGuid()]));
        inexistente.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await PafApi.OkAsync<List<UsuarioUnidadeDto>>(_tenant.Admin, HttpMethod.Get, $"/usuarios/{alvo}/unidades", null, null))
            .Count.ShouldBe(2, "as recusas não gravaram nada");
    }

    [Fact]
    public async Task SemUnidadesCreate_ConcedeSoDentroDoProprioEscopo()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var gerente = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await DarPapelAsync(gerente.UserId, PafPermissions.UsuariosUnidade.View, PafPermissions.UsuariosUnidade.Update);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(gerente)).AccessToken);
        var daFilial = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);
        var regional = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId)).UserId);

        using var foraDoEscopo = await PafApi.EnviarAsync(cliente, HttpMethod.Put, $"/usuarios/{daFilial}/unidades", null,
            new SetUsuarioUnidadesCommand(daFilial, [u1.UnidadeId, u2.UnidadeId]));
        foraDoEscopo.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "conceder unidade fora do próprio escopo");

        using var usuarioMaior = await PafApi.EnviarAsync(cliente, HttpMethod.Put, $"/usuarios/{regional}/unidades", null,
            new SetUsuarioUnidadesCommand(regional, [u1.UnidadeId]));
        usuarioMaior.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "editar usuário cujo escopo não cabe no seu");
        using var leituraMaior = await PafApi.EnviarAsync(cliente, HttpMethod.Get, $"/usuarios/{regional}/unidades", null, null);
        leituraMaior.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "nem ler o escopo de quem não cabe no seu");

        await PafApi.OkAsync<Guid>(cliente, HttpMethod.Put, $"/usuarios/{daFilial}/unidades", null,
            new SetUsuarioUnidadesCommand(daFilial, [u1.UnidadeId]));
    }

    private Guid AdminId() =>
        Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(_tenant.Admin.DefaultRequestHeaders.Authorization!.Parameter).Subject);

    private async Task DarPapelAsync(string usuarioId, params string[] permissoes)
    {
        var nome = $"paf-conc-{Guid.NewGuid():N}"[..20];
        using var criar = await _tenant.Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/roles", new { id = string.Empty, name = nome, description = "PAF concessão" });
        criar.StatusCode.ShouldBe(HttpStatusCode.OK, await criar.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await criar.Content.ReadAsStringAsync());
        var papelId = json.RootElement.GetProperty("id").GetString()!;

        using var definir = await _tenant.Admin.PutAsJsonAsync($"{TestConstants.IdentityBasePath}/{papelId}/permissions", new { roleId = papelId, permissions = permissoes });
        definir.StatusCode.ShouldBe(HttpStatusCode.OK, await definir.Content.ReadAsStringAsync());

        using var atribuir = await _tenant.Admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/users/{usuarioId}/roles", new
        {
            userId = usuarioId,
            userRoles = new[] { new { roleName = nome, enabled = true } },
        });
        atribuir.StatusCode.ShouldBe(HttpStatusCode.OK, await atribuir.Content.ReadAsStringAsync());
    }
}
