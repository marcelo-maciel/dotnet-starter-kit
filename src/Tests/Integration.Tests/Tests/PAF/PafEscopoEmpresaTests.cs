using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Escopo efetivo (RF08, ADR 0006): escopo empresa is the principal and then every unidade of the tenant by
/// creation, including the ones created later; the list scope is the list. The unit switch, the session check, the
/// lists and the scope edit read the same definition. The unit switch runs against a real Valkey container,
/// because the harness host runs without one.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEscopoEmpresaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private readonly RedisContainer _valkey = new RedisBuilder("valkey/valkey:9.1.0-alpine").Build();
    private readonly PafTenantCliente _tenant = new(factory, "paf-emp");
    private ConnectionMultiplexer _multiplexer = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_valkey.StartAsync(), _tenant.InicializarAsync());
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_valkey.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    [Fact]
    public async Task EscopoEmpresa_ListaPrincipalEDepoisTodasPorCriacao_InclusiveCriadaDepois()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u3 = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        // Principal U2 on purpose: the principal leads, the rest follows creation order, starting with the
        // tenant's initial unidade (C03), created with the tenant.
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(u2.UnidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Unidades.View, PafPermissions.Guiches.Selecionar, PafPermissions.Filas.View);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        (await UnidadesAsync(cliente)).ShouldBe([u2.UnidadeId, _tenant.UnidadeInicial, u1.UnidadeId, u3.UnidadeId]);

        var u4 = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var filaEmU4 = await CriarFilaAsync(u4.UnidadeId);

        (await UnidadesAsync(cliente)).ShouldBe([u2.UnidadeId, _tenant.UnidadeInicial, u1.UnidadeId, u3.UnidadeId, u4.UnidadeId]);
        (await PafApi.OkAsync<List<UnidadeDoGuicheDto>>(cliente, HttpMethod.Get, "/guiche/unidades", null, null))
            .Select(u => u.Id).ShouldBe([u2.UnidadeId, _tenant.UnidadeInicial, u1.UnidadeId, u3.UnidadeId, u4.UnidadeId]);
        (await PafApi.OkAsync<List<FilaDto>>(cliente, HttpMethod.Get, "/filas/consolidadas", null, null))
            .ShouldContain(f => f.Id == filaEmU4);
    }

    [Fact]
    public async Task EscopoDeLista_NaoVeUnidadeCriadaDepois()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Unidades.View);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        await _tenant.CriarUnidadeAsync(limiteSessoes: null);

        (await UnidadesAsync(cliente)).ShouldBe([u1.UnidadeId]);
    }

    [Fact]
    public async Task EscopoEmpresa_DoisTenants_SoVeAsUnidadesDoProprio()
    {
        var outro = new PafTenantCliente(factory, "paf-emp-b");
        await outro.InicializarAsync();
        var deB = await outro.CriarUnidadeAsync(limiteSessoes: 5);
        var deBDepois = await outro.CriarUnidadeAsync(limiteSessoes: null);
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(u1.UnidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Unidades.View);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        var vistas = await UnidadesAsync(cliente);

        vistas.ShouldBe([u1.UnidadeId, _tenant.UnidadeInicial]);
        vistas.ShouldNotContain(outro.UnidadeInicial);
        vistas.ShouldNotContain(deB.UnidadeId);
        vistas.ShouldNotContain(deBDepois.UnidadeId);
    }

    [Fact]
    public async Task EscopoEmpresa_TrocaParaUnidadeForaDaPrincipal_NaoRevoga()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var u3 = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(u1.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var token = await _tenant.EntrarAsync(usuario);
        var sessaoId = PafTenantCliente.SessaoDoToken(token.AccessToken);

        using (var scope = factory.Services.CreateScope())
        {
            var (handler, estado) = Troca(scope, u1.UnidadeId, sessaoId, usuarioId);
            await handler.Handle(new SetUnidadeCorrenteCommand(u3.UnidadeId), CancellationToken.None);
            (await estado.LerUnidadeAsync(_tenant.TenantId, sessaoId, CancellationToken.None)).ShouldBe(u3.UnidadeId);
        }

        // The harness host has no Valkey, so its session check falls back to the licence unit: moving it to U3
        // makes the next request run with U3 as the current unit, the state the switch above produced.
        await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UnidadeId" = {u3.UnidadeId}
             WHERE "TenantId" = {_tenant.TenantId} AND "SessaoId" = {sessaoId}
            """);
        using var cliente = _tenant.Cliente(token.AccessToken);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _tenant.AtividadesAsync(usuarioId)).ShouldHaveSingleItem().UnidadeId.ShouldBe(u3.UnidadeId);
    }

    [Fact]
    public async Task EscopoDeLista_TrocaParaUnidadeForaDaLista_Recusada403()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId);
        var usuarioId = Guid.Parse(usuario.UserId);
        var token = await _tenant.EntrarAsync(usuario);
        using var cliente = _tenant.Cliente(token.AccessToken);

        using var resposta = await PafApi.EnviarAsync(cliente, HttpMethod.Post, "/sessao/unidade-corrente", null, new SetUnidadeCorrenteCommand(u2.UnidadeId));
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(SetUnidadeCorrenteCommandHandler.MensagemForaDoEscopo);

        using var scope = factory.Services.CreateScope();
        var (handler, estado) = Troca(scope, u1.UnidadeId, PafTenantCliente.SessaoDoToken(token.AccessToken), usuarioId);
        await Should.ThrowAsync<ForbiddenException>(() => handler.Handle(new SetUnidadeCorrenteCommand(u2.UnidadeId), CancellationToken.None).AsTask());
        (await estado.LerUnidadeAsync(_tenant.TenantId, PafTenantCliente.SessaoDoToken(token.AccessToken), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task EmpresaViraLista_RevogaSessaoNaUnidadeQueSaiu_ETrocaDePrincipalNaoRevoga()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u3 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var admin = await AdministradorEmpresaAsync(u1.UnidadeId);
        var alvo = await _tenant.CriarUsuarioEmpresaAsync(u1.UnidadeId);
        var alvoId = Guid.Parse(alvo.UserId);
        var token = await _tenant.EntrarAsync(alvo);
        using var cliente = _tenant.Cliente(token.AccessToken);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UnidadeId" = {u3.UnidadeId}
             WHERE "TenantId" = {_tenant.TenantId} AND "SessaoId" = {PafTenantCliente.SessaoDoToken(token.AccessToken)}
            """);

        // Moving the principal keeps every unidade in the effective scope: nobody is revoked.
        await PafApi.OkAsync<Guid>(admin, HttpMethod.Put, $"/usuarios/{alvoId}/unidades", null,
            new SetUsuarioUnidadesCommand(alvoId, [u2.UnidadeId], true));
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Down to the list [U1, U2]: U3, where the session operates, leaves the scope.
        await PafApi.OkAsync<Guid>(admin, HttpMethod.Put, $"/usuarios/{alvoId}/unidades", null,
            new SetUsuarioUnidadesCommand(alvoId, [u1.UnidadeId, u2.UnidadeId], false));
        (await _tenant.AtividadesAsync(alvoId)).ShouldBeEmpty();
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var lido = await PafApi.OkAsync<EscopoUsuarioDto>(admin, HttpMethod.Get, $"/usuarios/{alvoId}/unidades", null, null);
        lido.ShouldBe(new EscopoUsuarioDto(false, lido.Unidades));
        lido.Unidades.ShouldBe([new UsuarioUnidadeDto(u1.UnidadeId, 0), new UsuarioUnidadeDto(u2.UnidadeId, 1)]);
    }

    [Fact]
    public async Task ReordenarTrocandoAsPosicoes_EUmUpdateComIdsPreservados()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var admin = await AdministradorEmpresaAsync(u1.UnidadeId);
        var alvoId = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId)).UserId);
        var idsAntes = await IdsDasLinhasAsync(alvoId);

        // A full swap: each Ordem moves to a value another row still holds until the end of the statement batch.
        await PafApi.OkAsync<Guid>(admin, HttpMethod.Put, $"/usuarios/{alvoId}/unidades", null,
            new SetUsuarioUnidadesCommand(alvoId, [u2.UnidadeId, u1.UnidadeId], false));

        (await IdsDasLinhasAsync(alvoId)).ShouldBe(idsAntes, ignoreOrder: true);
        (await PafApi.OkAsync<EscopoUsuarioDto>(admin, HttpMethod.Get, $"/usuarios/{alvoId}/unidades", null, null)).Unidades
            .ShouldBe([new UsuarioUnidadeDto(u2.UnidadeId, 0), new UsuarioUnidadeDto(u1.UnidadeId, 1)]);
    }

    [Fact]
    public async Task DoisSetUsuarioUnidadesParalelos_TerminamComUmPedidoInteiro_EUmaSoOrdemZero()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u3 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var adminA = await AdministradorEmpresaAsync(u1.UnidadeId);
        using var adminB = await AdministradorEmpresaAsync(u1.UnidadeId);
        var alvoId = Guid.Parse((await _tenant.CriarUsuarioAsync(u1.UnidadeId)).UserId);
        List<Guid> pedidoA = [u2.UnidadeId, u1.UnidadeId, u3.UnidadeId];
        List<Guid> pedidoB = [u3.UnidadeId, u2.UnidadeId];

        for (var rodada = 0; rodada < 10; rodada++)
        {
            var respostas = await Task.WhenAll(
                PafApi.EnviarAsync(adminA, HttpMethod.Put, $"/usuarios/{alvoId}/unidades", null, new SetUsuarioUnidadesCommand(alvoId, pedidoA, false)),
                PafApi.EnviarAsync(adminB, HttpMethod.Put, $"/usuarios/{alvoId}/unidades", null, new SetUsuarioUnidadesCommand(alvoId, pedidoB, false)));
            foreach (var resposta in respostas)
            {
                resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
                resposta.Dispose();
            }

            var final = (await PafApi.OkAsync<EscopoUsuarioDto>(adminA, HttpMethod.Get, $"/usuarios/{alvoId}/unidades", null, null)).Unidades;
            final.Count(u => u.Ordem == 0).ShouldBe(1);
            var lista = final.OrderBy(u => u.Ordem).Select(u => u.UnidadeId).ToList();
            (lista.SequenceEqual(pedidoA) || lista.SequenceEqual(pedidoB)).ShouldBeTrue($"rodada {rodada}: {string.Join(",", final)}");
            final.Select(u => u.Ordem).ShouldBe(Enumerable.Range(0, final.Count), ignoreOrder: true);
        }
    }

    [Fact]
    public async Task InsertManualDeDuasOrdemZero_FalhaNoCommit()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuarioId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        await using var transacao = await db.Database.BeginTransactionAsync();

        // Both inserts pass: the constraint is deferred to the commit.
        (await db.Database.ExecuteSqlAsync($"""
            INSERT INTO paf."UsuariosUnidade" ("Id", "UsuarioId", "UnidadeId", "Ordem", "TenantId")
            VALUES ({Guid.CreateVersion7()}, {usuarioId}, {u1.UnidadeId}, 0, {_tenant.TenantId}),
                   ({Guid.CreateVersion7()}, {usuarioId}, {u2.UnidadeId}, 0, {_tenant.TenantId})
            """)).ShouldBe(2);

        var erro = await Should.ThrowAsync<PostgresException>(() => transacao.CommitAsync());
        erro.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        erro.ConstraintName.ShouldBe("UX_UsuariosUnidade_Ordem");
        (await IdsDasLinhasAsync(usuarioId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RaizSemPerfilNoTenant_ForaDoEscopo403()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var rootToken = await new AuthHelper(factory).GetRootAdminTokenAsync();
        using var raiz = factory.CreateClient();
        raiz.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rootToken.AccessToken);
        raiz.DefaultRequestHeaders.Add("tenant", _tenant.TenantId);

        using var resposta = await PafApi.EnviarAsync(raiz, HttpMethod.Put, $"/unidades/{u1.UnidadeId}", null,
            new UpdateUnidadeCommand(u1.UnidadeId, "Renomeada", null));
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await resposta.Content.ReadAsStringAsync());
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(EscopoUsuario.MensagemForaDoEscopo);
    }

    private (SetUnidadeCorrenteCommandHandler Handler, EstadoSessao Estado) Troca(IServiceScope scope, Guid origem, Guid sessaoId, Guid usuarioId)
    {
        var db = _tenant.Contexto(scope, unidadeCorrente: origem);
        var sessao = new SessaoCorrente();
        sessao.Definir(_tenant.TenantId, sessaoId, usuarioId, origem);
        var estado = new EstadoSessao([_multiplexer], NullLogger<EstadoSessao>.Instance);
        var handler = new SetUnidadeCorrenteCommandHandler(db, sessao, new ConcessaoCorrente(), scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>(), estado,
            scope.ServiceProvider.GetRequiredService<EscopoUsuario>(), scope.ServiceProvider.GetRequiredService<AtosImpersonacao>());
        return (handler, estado);
    }

    private static async Task<List<Guid>> UnidadesAsync(HttpClient cliente) =>
        [.. (await PafApi.OkAsync<List<UnidadeDto>>(cliente, HttpMethod.Get, "/unidades", null, null)).Select(u => u.Id)];

    private async Task<List<Guid>> IdsDasLinhasAsync(Guid usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.UsuariosUnidade.AsNoTracking().Where(u => u.UsuarioId == usuarioId).Select(u => u.Id).ToListAsync();
    }

    private async Task<Guid> CriarFilaAsync(Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: unidadeId);
        var fila = Fila.Criar(unidadeId, "Caixa", "Caixa", setorId: null);
        db.Filas.Add(fila);
        await db.SaveChangesAsync();
        return fila.Id;
    }

    private async Task<HttpClient> AdministradorEmpresaAsync(Guid principal)
    {
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(principal);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.UsuariosUnidade.View, PafPermissions.UsuariosUnidade.Update);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }
}
