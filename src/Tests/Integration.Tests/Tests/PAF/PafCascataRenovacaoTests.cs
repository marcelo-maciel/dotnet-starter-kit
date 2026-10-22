using System.Text.Json;
using FSH.Framework.Eventing.Persistence;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Cascata de licença pelo host real (S05, RF22, ADR 0008): login e renovação passam pelos comportamentos do PAF.
/// O harness sobe sem Valkey, então aqui a decisão é a do modo degradado; o conjunto no Valkey e os dois modos lado
/// a lado estão em <see cref="PafCascataLicencaTests"/>.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafCascataRenovacaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private readonly PafTenantCliente _tenant = new(factory, "paf-cr");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_PrincipalCheia_AbreNaSegundaDoEscopo()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(a.UnidadeId));
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);

        var token = await _tenant.EntrarAsync(usuario);

        var atividade = (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldHaveSingleItem();
        atividade.SessaoId.ShouldBe(PafTenantCliente.SessaoDoToken(token.AccessToken));
        atividade.UnidadeId.ShouldBe(b.UnidadeId);
    }

    // RF01: a wrong password with every unit full answers exactly like an unknown user, never with the licence message.
    [Fact]
    public async Task SenhaErrada_TodasCheias_MesmaRespostaDaCredencialInvalida()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(a.UnidadeId));
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(b.UnidadeId));
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);

        var senhaErrada = await _tenant.LoginAsync(usuario.Email, "Senha@Errada1");
        var inexistente = await _tenant.LoginAsync($"ninguem-{Guid.NewGuid():N}@paf.test", "Senha@Errada1");

        senhaErrada.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        inexistente.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var corpoSenha = await senhaErrada.Content.ReadAsStringAsync();
        PafSessaoLicencaTests.Detalhe(corpoSenha).ShouldBe(PafSessaoLicencaTests.Detalhe(await inexistente.Content.ReadAsStringAsync()));
        corpoSenha.ShouldNotContain(Licencas.MensagemLimite);
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_TodasCheias_RecusadoSemSessaoTokenNemLinha()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(a.UnidadeId));
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(b.UnidadeId));
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);

        var resposta = await _tenant.LoginAsync(usuario.Email, usuario.Password);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var corpo = await resposta.Content.ReadAsStringAsync();
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(Licencas.MensagemLimite);
        corpo.ShouldNotContain("accessToken", Case.Insensitive);
        (await SessoesDoKitAsync(usuario.UserId)).ShouldBe(0);
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
    }

    // L28: the licence is checked before the kit rotates the refresh token. Refused, the old pair keeps working.
    [Fact]
    public async Task Renovacao_TodasCheias_403SemRotacaoERefreshAntigoValeQuandoAbreVaga()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        await EnvelhecerAsync(PafTenantCliente.SessaoDoToken(token.AccessToken));
        var outroA = await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(a.UnidadeId));
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(b.UnidadeId));
        var hashAntes = await HashDaSessaoAsync(token);

        var recusa = await _tenant.RenovarAsync(token);

        recusa.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var corpo = await recusa.Content.ReadAsStringAsync();
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(Licencas.MensagemLimite);
        corpo.ShouldNotContain("refreshToken", Case.Insensitive);
        (await HashDaSessaoAsync(token)).ShouldBe(hashAntes, "nenhuma rotação gravada");
        (await SessoesDoKitAsync(usuario.UserId)).ShouldBe(1);

        // A seat frees up (the other user's mark expires): the same refresh token now revives the session.
        await EnvelhecerAsync(PafTenantCliente.SessaoDoToken(outroA.AccessToken));
        var aceita = await _tenant.RenovarAsync(token);

        aceita.StatusCode.ShouldBe(HttpStatusCode.OK, await aceita.Content.ReadAsStringAsync());
        var renovado = (await aceita.Content.ReadFromJsonAsync<TokenRefreshResult>())!;
        using var cliente = _tenant.Cliente(renovado.Token);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var atividade = (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldHaveSingleItem();
        atividade.UnidadeId.ShouldBe(a.UnidadeId);
    }

    // The kit refuses after the reservation (session revoked elsewhere): the reservation is undone, no seat renewed.
    [Fact]
    public async Task Renovacao_SessaoRevogadaNoKit_401SemOcuparLicenca()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        var sessaoId = PafTenantCliente.SessaoDoToken(token.AccessToken);
        await EnvelhecerAsync(sessaoId);
        using (var scope = factory.Services.CreateScope())
        {
            _tenant.Contexto(scope, unidadeCorrente: null);
            (await scope.ServiceProvider.GetRequiredService<ISessionService>()
                .RevokeSessionForAdminAsync(sessaoId, usuario.UserId, "teste", CancellationToken.None)).ShouldBeTrue();
        }

        var resposta = await _tenant.RenovarAsync(token);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await MarcaVivaAsync(sessaoId)).ShouldBeFalse();
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(a.UnidadeId));
    }

    // RF22: a session revived by the cascade in another unit tells the user's windows. The window's own connection
    // died with the mark (every request of it passes the session check), so the realistic path is the reconnection
    // with the renewed token, reached by the outbox copy; the direct send covers a connection still open elsewhere.
    [Fact]
    public async Task Renovacao_RevividaEmOutraUnidade_AvisoNaCaixaDeSaidaChegaNaAbaReconectada()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        await EnvelhecerAsync(PafTenantCliente.SessaoDoToken(token.AccessToken));
        await _tenant.EntrarAsync(await _tenant.CriarUsuarioAsync(a.UnidadeId));

        var resposta = await _tenant.RenovarAsync(token);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldHaveSingleItem().UnidadeId.ShouldBe(b.UnidadeId);
        (await AvisosDeUnidadeAsync(usuario.UserId)).ShouldBe(1);

        var renovado = (await resposta.Content.ReadFromJsonAsync<TokenRefreshResult>())!;
        await using var conexao = await new PafOperacao(factory, _tenant).ConectarAsync(renovado.Token);
        using var caixa = new Caixa<UnidadeCorrenteAlterada>(conexao, UnidadeCorrenteAlterada.Metodo);
        await OutboxDrain.DrainAsync(factory.Services);

        var aviso = await caixa.EsperarAsync(x => x.UnidadeId == b.UnidadeId, TimeSpan.FromSeconds(10));
        aviso.ShouldNotBeNull("a aba reconectada precisa saber que a sessão passou para a unidade B");
    }

    [Fact]
    public async Task Renovacao_RevividaNaMesmaUnidade_SemAviso()
    {
        var a = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var b = await _tenant.CriarUnidadeAsync(limiteSessoes: 1);
        var usuario = await _tenant.CriarUsuarioAsync(a.UnidadeId, b.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        await EnvelhecerAsync(PafTenantCliente.SessaoDoToken(token.AccessToken));

        var resposta = await _tenant.RenovarAsync(token);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldHaveSingleItem().UnidadeId.ShouldBe(a.UnidadeId);
        (await AvisosDeUnidadeAsync(usuario.UserId)).ShouldBe(0);
    }

    // Outbox rows of the unit-change notice addressed to one user, processed or not.
    private async Task<int> AvisosDeUnidadeAsync(string usuarioId)
    {
        using var scope = factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        return await eventing.OutboxMessages.CountAsync(
            m => m.Type.Contains(nameof(UnidadeCorrenteAlteradaPaf)) && m.Payload.Contains(usuarioId));
    }

    private async Task EnvelhecerAsync(Guid sessaoId) =>
        (await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '11 minutes' WHERE "SessaoId" = {sessaoId}
            """)).ShouldBe(1);

    private async Task<bool> MarcaVivaAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidadeCorrente: null).AtividadesSessao.AsNoTracking()
            .AnyAsync(x => x.SessaoId == sessaoId && x.UltimaAtividadeEm > DateTime.UtcNow.AddMinutes(-10));
    }

    // The kit's session row keyed by the refresh token digest: unchanged means the kit did not rotate.
    private async Task<Guid?> HashDaSessaoAsync(TokenResult token)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, unidadeCorrente: null);
        var sessoes = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token.RefreshToken)).AsSpan(0, 8));
        var sessao = await sessoes.GetSessionIdByRefreshTokenAsync(hash, CancellationToken.None);
        sessao.ShouldBe(PafTenantCliente.SessaoDoToken(token.AccessToken), "o resumo do refresh token é o mesmo do kit");
        return sessao;
    }

    private async Task<int> SessoesDoKitAsync(string usuarioId)
    {
        var resposta = await _tenant.Admin.GetAsync($"{TestConstants.IdentityBasePath}/users/{usuarioId}/sessions");
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await resposta.Content.ReadAsStringAsync()).RootElement.GetArrayLength();
    }
}
