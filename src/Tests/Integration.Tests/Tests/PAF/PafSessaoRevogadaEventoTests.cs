using System.Text.Json;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;
using FSH.Modules.Identity.Contracts.Events;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Evento de integração de sessão revogada do kit (ADR 0008, S01): toda revogação de sessão, por qualquer caminho
/// do Identity, grava uma linha na caixa de saída com sessão, usuário, tenant e motivo, sem dado pessoal. O usuário
/// dos testes não tem unidade no PAF, para que a sessão única do login não revogue as sessões anteriores.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafSessaoRevogadaEventoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Sessoes = $"{TestConstants.IdentityBasePath}/sessions";
    private readonly PafTenantCliente _tenant = new(factory, "paf-evrev");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RevogarUmaSessao_GravaUmaLinhaComSessaoTenantESemDadoPessoal()
    {
        var usuario = await _tenant.CriarUsuarioAsync();
        var primeira = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(usuario)).AccessToken);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);

        var resposta = await cliente.DeleteAsync($"{Sessoes}/{primeira}");

        resposta.StatusCode.ShouldBe(HttpStatusCode.NoContent, await resposta.Content.ReadAsStringAsync());
        var linha = (await LinhasAsync(primeira)).ShouldHaveSingleItem();
        linha.TenantId.ShouldBe(_tenant.TenantId);
        linha.Payload.ShouldNotContain(usuario.Email, Case.Insensitive);
        using var payload = JsonDocument.Parse(linha.Payload);
        var raiz = payload.RootElement;
        raiz.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["id", "occurredOnUtc", "tenantId", "correlationId", "source", "sessionId", "userId", "reason"],
            ignoreOrder: true);
        raiz.GetProperty("sessionId").GetGuid().ShouldBe(primeira);
        raiz.GetProperty("userId").GetString().ShouldBe(usuario.UserId);
        raiz.GetProperty("tenantId").GetString().ShouldBe(_tenant.TenantId);
        raiz.GetProperty("reason").GetString().ShouldNotBeNullOrWhiteSpace();
        raiz.GetProperty("id").GetGuid().ShouldBe(linha.Id);
    }

    [Fact]
    public async Task RevogarSessaoJaRevogada_NaoGravaOutraLinha()
    {
        var usuario = await _tenant.CriarUsuarioAsync();
        var primeira = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(usuario)).AccessToken);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        (await cliente.DeleteAsync($"{Sessoes}/{primeira}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var repetida = await cliente.DeleteAsync($"{Sessoes}/{primeira}");

        repetida.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LinhasAsync(primeira)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task RevogarTodasComExcecao_GravaUmaLinhaPorSessaoRevogada()
    {
        var usuario = await _tenant.CriarUsuarioAsync();
        var a = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(usuario)).AccessToken);
        var b = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(usuario)).AccessToken);
        var tokenAtual = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var atual = PafTenantCliente.SessaoDoToken(tokenAtual);
        using var cliente = _tenant.Cliente(tokenAtual);
        (await LinhasAsync(a)).ShouldBeEmpty();
        (await LinhasAsync(b)).ShouldBeEmpty();

        var resposta = await cliente.PostAsJsonAsync($"{Sessoes}/revoke-all", new { exceptSessionId = atual });

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        (await LinhasAsync(a)).Count.ShouldBe(1);
        (await LinhasAsync(b)).Count.ShouldBe(1);
        (await LinhasAsync(atual)).ShouldBeEmpty();
    }

    // The admin bulk path skips the already-revoked session: only the live ones get a row.
    [Fact]
    public async Task AdminRevogaTodas_SessaoJaRevogadaNaoGravaDeNovo()
    {
        var usuario = await _tenant.CriarUsuarioAsync();
        var a = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(usuario)).AccessToken);
        var b = PafTenantCliente.SessaoDoToken((await _tenant.EntrarAsync(usuario)).AccessToken);
        var tokenC = (await _tenant.EntrarAsync(usuario)).AccessToken;
        var c = PafTenantCliente.SessaoDoToken(tokenC);
        using (var cliente = _tenant.Cliente(tokenC))
        {
            (await cliente.DeleteAsync($"{Sessoes}/{a}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await LinhasAsync(a)).Count.ShouldBe(1);
        (await LinhasAsync(b)).ShouldBeEmpty();
        (await LinhasAsync(c)).ShouldBeEmpty();

        var resposta = await _tenant.Admin.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/users/{usuario.UserId}/sessions/revoke-all", new { userId = usuario.UserId });

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        (await LinhasAsync(a)).Count.ShouldBe(1);
        (await LinhasAsync(b)).Count.ShouldBe(1);
        (await LinhasAsync(c)).Count.ShouldBe(1);
    }

    private async Task<List<OutboxMessage>> LinhasAsync(Guid sessaoId)
    {
        using var scope = factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        var tipo = typeof(SessionRevokedIntegrationEvent).FullName!;
        var sessao = sessaoId.ToString();
        return await eventing.OutboxMessages.AsNoTracking()
            .Where(m => m.Type.StartsWith(tipo) && m.Payload.Contains(sessao))
            .ToListAsync();
    }
}
