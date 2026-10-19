using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Contratos;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF23 in the unidades cadastro: creating, disabling and enabling are the escopo empresa profile's alone, even
/// against a list-scope user holding every unidade permission; renaming needs the unidade in the effective scope;
/// the list carries the contract in force, read-only.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafCadastroUnidadesTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly string[] PermissoesDeCadastro =
        [PafPermissions.Unidades.View, PafPermissions.Unidades.Create, PafPermissions.Unidades.Update, PafPermissions.Contratos.View];

    private readonly PafTenantCliente _tenant = new(factory, "paf-cun");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Empresa_CriaUnidade_QueApareceNaListaSemEditarOEscopo_SemContrato()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresa = await EmpresaAsync(u1.UnidadeId);

        (await PafApi.OkAsync<AdministracaoUnidadesDto>(empresa, HttpMethod.Get, "/unidades/administracao", null, null))
            .AdministraUnidades.ShouldBeTrue();
        var nova = await PafApi.OkAsync<Guid>(empresa, HttpMethod.Post, "/unidades", null, new CreateUnidadeCommand("  Loja Centro  ", "12345678000199"));

        var lista = await ListarAsync(empresa);
        lista.Select(u => u.Id).ShouldBe([u1.UnidadeId, nova]);
        var criada = lista.Single(u => u.Id == nova);
        criada.Nome.ShouldBe("Loja Centro");
        criada.Ativa.ShouldBeTrue();
        criada.Contrato.ShouldBeNull("a loja nova só recebe sessões depois que a Codefi gravar o contrato");
        (await LinhasDeEscopoAsync(nova)).ShouldBe(0, "o escopo empresa alcança a unidade nova sem linha de escopo");
    }

    [Fact]
    public async Task Lista_ComTodasAsPermissoes_NaoCriaNemDesativaNemReativa_MasRenomeiaSoDentroDoEscopo()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var lista = await ListaAsync(u1.UnidadeId);

        (await PafApi.OkAsync<AdministracaoUnidadesDto>(lista, HttpMethod.Get, "/unidades/administracao", null, null))
            .AdministraUnidades.ShouldBeFalse();

        var nome = $"Loja recusada {Guid.NewGuid():N}";
        using (var criar = await PafApi.EnviarAsync(lista, HttpMethod.Post, "/unidades", null, new CreateUnidadeCommand(nome, null)))
        {
            await RecusadoPorNaoSerEmpresaAsync(criar);
        }

        (await UnidadesComNomeAsync(nome)).ShouldBe(0, "a criação recusada não deixa linha");

        using (var desativar = await PafApi.EnviarAsync(lista, HttpMethod.Post, $"/unidades/{u1.UnidadeId}/disable", null, null))
        {
            await RecusadoPorNaoSerEmpresaAsync(desativar);
        }

        using (var reativar = await PafApi.EnviarAsync(lista, HttpMethod.Post, $"/unidades/{u1.UnidadeId}/enable", null, null))
        {
            await RecusadoPorNaoSerEmpresaAsync(reativar);
        }

        (await AtivaAsync(u1.UnidadeId)).ShouldBeTrue();

        await PafApi.OkAsync<Guid>(lista, HttpMethod.Put, $"/unidades/{u1.UnidadeId}", null, new UpdateUnidadeCommand(u1.UnidadeId, "Filial renomeada", null));
        (await ListarAsync(lista)).Single().Nome.ShouldBe("Filial renomeada");

        using var foraDoEscopo = await PafApi.EnviarAsync(lista, HttpMethod.Put, $"/unidades/{u2.UnidadeId}", null, new UpdateUnidadeCommand(u2.UnidadeId, "Invasão", null));
        foraDoEscopo.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await foraDoEscopo.Content.ReadAsStringAsync());
        PafSessaoLicencaTests.Detalhe(await foraDoEscopo.Content.ReadAsStringAsync()).ShouldBe(EscopoUsuario.MensagemForaDoEscopo);
    }

    [Fact]
    public async Task PerfilEmpresaSemPrincipal_NaoAdministraUnidades()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PermissoesDeCadastro);
        // Without a scope row he is no PAF user: the login passes with no licence and the scope is empty.
        await _tenant.ExecutarAsync($"""DELETE FROM paf."UsuariosUnidade" WHERE "UsuarioId" = {Guid.Parse(usuario.UserId)} AND "TenantId" = {_tenant.TenantId}""");
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        using var criar = await PafApi.EnviarAsync(cliente, HttpMethod.Post, "/unidades", null, new CreateUnidadeCommand("Loja", null));
        await RecusadoPorNaoSerEmpresaAsync(criar);
    }

    [Fact]
    public async Task EmpresaDesativa_SessaoAbertaContinua_EmissaoVira409_EReativaVolta()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        using var atendente = await operacao.AtendenteAsync(cenario, cenario.GuicheA);
        using var totem = await operacao.TotemAsync(cenario);
        using var empresa = await EmpresaAsync(cenario.UnidadeId);

        await PafApi.OkAsync<Guid>(empresa, HttpMethod.Post, $"/unidades/{cenario.UnidadeId}/disable", null, null);

        (await AtivaAsync(cenario.UnidadeId)).ShouldBeFalse();
        using (var posto = await atendente.GetAsync("/api/v1/paf/guiche/posto"))
        {
            posto.StatusCode.ShouldBe(HttpStatusCode.OK, "desativar não revoga a sessão aberta: " + await posto.Content.ReadAsStringAsync());
        }

        using (var emissao = await PafOperacao.EmitirAsync(totem, cenario.Caixa, cenario.Normal, Guid.NewGuid().ToString("N")))
        {
            emissao.StatusCode.ShouldBe(HttpStatusCode.Conflict, await emissao.Content.ReadAsStringAsync());
        }

        await PafApi.OkAsync<Guid>(empresa, HttpMethod.Post, $"/unidades/{cenario.UnidadeId}/enable", null, null);
        await PafOperacao.EmitirOkAsync(totem, cenario.Caixa, cenario.Normal);
    }

    [Fact]
    public async Task Lista_TrazOContratoVigenteResumido_OUltimoInicioVence_VencidoEFuturoFicamFora()
    {
        var comVigente = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var semContrato = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var soVencido = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var soFuturo = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, unidadeCorrente: null);
            db.ContratosUnidade.AddRange(
                ContratoUnidade.Criar(comVigente.UnidadeId, hoje.AddDays(-60), hoje.AddDays(-30), "Antigo", 1, PoliticaLimiteLicenca.Recusar),
                ContratoUnidade.Criar(comVigente.UnidadeId, hoje.AddDays(-1), hoje.AddDays(90), "Pro", 12, PoliticaLimiteLicenca.Recusar),
                ContratoUnidade.Criar(soVencido.UnidadeId, hoje.AddDays(-60), hoje.AddDays(-3), "Vencido", 3, PoliticaLimiteLicenca.Recusar),
                ContratoUnidade.Criar(soFuturo.UnidadeId, hoje.AddDays(3), null, "Futuro", 4, PoliticaLimiteLicenca.Recusar));
            await db.SaveChangesAsync();
        }

        using var empresa = await EmpresaAsync(comVigente.UnidadeId);
        var lista = (await ListarAsync(empresa)).ToDictionary(u => u.Id);

        lista[comVigente.UnidadeId].Contrato.ShouldBe(new ContratoVigenteResumoDto("Pro", 12, hoje.AddDays(90)));
        lista[semContrato.UnidadeId].Contrato.ShouldBeNull();
        lista[soVencido.UnidadeId].Contrato.ShouldBeNull();
        lista[soFuturo.UnidadeId].Contrato.ShouldBeNull();

        // The client sees the limit and never writes it: the contract endpoints are the Codefi's (RF23).
        using var gravar = await PafApi.EnviarAsync(empresa, HttpMethod.Post, "/contratos", null,
            new CreateContratoUnidadeCommand(semContrato.UnidadeId, hoje, null, "Pro", 999, "Recusar"));
        gravar.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> EmpresaAsync(Guid principal)
    {
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(principal);
        await _tenant.DarPapelAsync(usuario.UserId, PermissoesDeCadastro);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private async Task<HttpClient> ListaAsync(params Guid[] unidades)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidades);
        await _tenant.DarPapelAsync(usuario.UserId, PermissoesDeCadastro);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static Task<List<UnidadeDto>> ListarAsync(HttpClient cliente) =>
        PafApi.OkAsync<List<UnidadeDto>>(cliente, HttpMethod.Get, "/unidades", null, null);

    private static async Task RecusadoPorNaoSerEmpresaAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, corpo);
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(EscopoUsuario.MensagemSoAdministradorDaEmpresa);
    }

    private async Task<bool> AtivaAsync(Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Unidades.AsNoTracking().Where(u => u.Id == unidadeId).Select(u => u.Ativa).SingleAsync();
    }

    private async Task<int> UnidadesComNomeAsync(string nome)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Unidades.AsNoTracking().CountAsync(u => u.Nome == nome);
    }

    private async Task<int> LinhasDeEscopoAsync(Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.UsuariosUnidade.AsNoTracking().CountAsync(u => u.UnidadeId == unidadeId);
    }
}
