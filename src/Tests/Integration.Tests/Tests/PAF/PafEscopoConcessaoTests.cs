using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade.SetUsuarioUnidades;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Who grants which scope (RF08): only the tenant admin (escopo empresa, or the holder of Unidades.Create by the
/// decision of 17/10/2026) grants escopo empresa and any active unidade of the tenant; anyone else grants only
/// inside his own effective scope and touches only users whose scope fits in it, so an escopo empresa user is
/// reached only by the tenant admin.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEscopoConcessaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-conc");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EscopoEmpresa_ConcedeQualquerUnidadeAtiva_ERecusaInativaOuInexistente()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var inativa = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" = {inativa.UnidadeId}""");
        using var empresa = await AdministradorEmpresaAsync(u1.UnidadeId);
        var alvo = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);

        await PafApi.OkAsync<Guid>(empresa, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u2.UnidadeId, u1.UnidadeId], false));
        var lido = await LerAsync(empresa, alvo);
        lido.EscopoEmpresa.ShouldBeFalse();
        lido.Unidades.ShouldBe([new UsuarioUnidadeDto(u2.UnidadeId, 0), new UsuarioUnidadeDto(u1.UnidadeId, 1)]);

        using var comInativa = await PafApi.EnviarAsync(empresa, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u1.UnidadeId, inativa.UnidadeId], false));
        comInativa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        PafSessaoLicencaTests.Detalhe(await comInativa.Content.ReadAsStringAsync()).ShouldBe(SetUsuarioUnidadesCommandHandler.MensagemUnidadeInvalida);

        using var inexistente = await PafApi.EnviarAsync(empresa, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [Guid.NewGuid()], false));
        inexistente.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await LerAsync(empresa, alvo)).Unidades.Count.ShouldBe(2, "as recusas não gravaram nada");
    }

    [Fact]
    public async Task AdminComUnidadesCreate_SemPerfilEmpresa_AdministraOTenant()
    {
        // The kit admin holds every permission, Unidades.Create included, and has no PAF scope (option A of 17/10).
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var alvo = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);
        var empresa = Guid.Parse((await _tenant.CriarUsuarioEmpresaAsync(u1.UnidadeId)).UserId);

        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u2.UnidadeId, u1.UnidadeId], false));
        (await LerAsync(_tenant.Admin, alvo)).Unidades
            .ShouldBe([new UsuarioUnidadeDto(u2.UnidadeId, 0), new UsuarioUnidadeDto(u1.UnidadeId, 1)]);

        (await LerAsync(_tenant.Admin, empresa)).EscopoEmpresa.ShouldBeTrue("lê o usuário de escopo empresa");
        await PafApi.OkAsync<Guid>(_tenant.Admin, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u2.UnidadeId], true));
        var lido = await LerAsync(_tenant.Admin, alvo);
        lido.EscopoEmpresa.ShouldBeTrue("concede escopo empresa");
        lido.Unidades.ShouldBe([new UsuarioUnidadeDto(u2.UnidadeId, 0)]);
    }

    [Fact]
    public async Task SemEscopoEmpresa_ConcedeSoDentroDoProprioEscopo()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var cliente = await AdministradorDeListaAsync(u1.UnidadeId);
        var daFilial = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);
        var regional = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId)).UserId);

        using var foraDoEscopo = await PafApi.EnviarAsync(cliente, HttpMethod.Put, $"/usuarios/{daFilial}/unidades", null,
            new SetUsuarioUnidadesCommand(daFilial, [u1.UnidadeId, u2.UnidadeId], false));
        foraDoEscopo.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "conceder unidade fora do próprio escopo");

        using var usuarioMaior = await PafApi.EnviarAsync(cliente, HttpMethod.Put, $"/usuarios/{regional}/unidades", null,
            new SetUsuarioUnidadesCommand(regional, [u1.UnidadeId], false));
        usuarioMaior.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "editar usuário cujo escopo não cabe no seu");
        using var leituraMaior = await PafApi.EnviarAsync(cliente, HttpMethod.Get, $"/usuarios/{regional}/unidades", null, null);
        leituraMaior.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "nem ler o escopo de quem não cabe no seu");

        await PafApi.OkAsync<Guid>(cliente, HttpMethod.Put, $"/usuarios/{daFilial}/unidades", null,
            new SetUsuarioUnidadesCommand(daFilial, [u1.UnidadeId], false));
    }

    [Fact]
    public async Task AdminDeLista_ConcedendoEscopoEmpresa_Recusado403SemGravar()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var lista = await AdministradorDeListaAsync(u1.UnidadeId);
        var alvo = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);

        using var resposta = await PafApi.EnviarAsync(lista, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u1.UnidadeId], true));
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync())
            .ShouldBe(SetUsuarioUnidadesCommandHandler.MensagemSoEmpresaConcedeEmpresa);

        (await LerAsync(lista, alvo)).EscopoEmpresa.ShouldBeFalse();
    }

    [Fact]
    public async Task EscopoEmpresa_ComDuasUnidadesOuSemPrincipal_Recusado400()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresa = await AdministradorEmpresaAsync(u1.UnidadeId);
        var alvo = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);

        using var duas = await PafApi.EnviarAsync(empresa, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u1.UnidadeId, u2.UnidadeId], true));
        duas.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await duas.Content.ReadAsStringAsync()).ShouldContain(SetUsuarioUnidadesCommandValidator.MensagemEmpresaUmaUnidade);

        using var semPrincipal = await PafApi.EnviarAsync(empresa, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [], true));
        semPrincipal.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var lido = await LerAsync(empresa, alvo);
        lido.EscopoEmpresa.ShouldBeFalse();
        lido.Unidades.ShouldBe([new UsuarioUnidadeDto(u1.UnidadeId, 0)]);

        await PafApi.OkAsync<Guid>(empresa, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u2.UnidadeId], true));
        lido = await LerAsync(empresa, alvo);
        lido.EscopoEmpresa.ShouldBeTrue();
        lido.Unidades.ShouldBe([new UsuarioUnidadeDto(u2.UnidadeId, 0)], "escopo empresa grava exatamente a linha da principal");
    }

    [Fact]
    public async Task AdminDeLista_ComTodasAsUnidades_NaoLeNemEditaUsuarioEmpresa()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        // The list holds every unidade of the tenant, so only the explicit escopo empresa rule refuses.
        using var lista = await AdministradorDeListaAsync(u1.UnidadeId, u2.UnidadeId);
        var alvo = Guid.Parse((await _tenant.CriarUsuarioEmpresaAsync(u1.UnidadeId)).UserId);

        using var leitura = await PafApi.EnviarAsync(lista, HttpMethod.Get, $"/usuarios/{alvo}/unidades", null, null);
        leitura.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        PafSessaoLicencaTests.Detalhe(await leitura.Content.ReadAsStringAsync()).ShouldBe(EscopoUsuario.MensagemAlvoForaDoAlcance);

        using var edicao = await PafApi.EnviarAsync(lista, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [u1.UnidadeId], false));
        edicao.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var empresa = await AdministradorEmpresaAsync(u2.UnidadeId);
        (await LerAsync(empresa, alvo)).EscopoEmpresa.ShouldBeTrue("a recusa não rebaixou o usuário empresa");
    }

    [Fact]
    public async Task EscopoEmpresa_NaoConcedeUnidadeDeOutroTenant()
    {
        var outro = new PafTenantCliente(factory, "paf-conc-b");
        await outro.InicializarAsync();
        var deB = await outro.CriarUnidadeAsync(limiteSessoes: 5);
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresa = await AdministradorEmpresaAsync(u1.UnidadeId);
        var alvo = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);

        using var resposta = await PafApi.EnviarAsync(empresa, HttpMethod.Put, $"/usuarios/{alvo}/unidades", null,
            new SetUsuarioUnidadesCommand(alvo, [deB.UnidadeId], true));
        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(SetUsuarioUnidadesCommandHandler.MensagemUnidadeInvalida);
    }

    private static Task<EscopoUsuarioDto> LerAsync(HttpClient cliente, Guid usuarioId) =>
        PafApi.OkAsync<EscopoUsuarioDto>(cliente, HttpMethod.Get, $"/usuarios/{usuarioId}/unidades", null, null);

    private async Task<HttpClient> AdministradorEmpresaAsync(Guid principal)
    {
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(principal);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.UsuariosUnidade.View, PafPermissions.UsuariosUnidade.Update);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private async Task<HttpClient> AdministradorDeListaAsync(params Guid[] unidades)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidades);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.UsuariosUnidade.View, PafPermissions.UsuariosUnidade.Update);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }
}
