using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.DTOs;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using FSH.Modules.Identity.Domain;
using FSH.Modules.Identity.Features.v1.Tokens.TokenGeneration;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Revogação da sessão de pessoa (RF08, RF22, seção 9, ADR 0008): retirada da unidade corrente do escopo,
/// usuário desativado numa tela do kit, sessão raiz revogada, credencial forjada sem papel de estação, sessão
/// pendente de um login que falhou, renovação que perde a corrida para o login em outra máquina e login com 2FA.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafSessaoRevogacaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Perfil = $"{TestConstants.IdentityBasePath}/profile";
    private readonly PafTenantCliente _tenant = new(factory, "paf-rev");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RetirarUnidadeCorrenteDoEscopo_RevogaASessaoNoMesmoAto()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        using var cliente = _tenant.Cliente(token.AccessToken);
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var operador = await OperadorRaizComEscopoAsync(u1.UnidadeId, u2.UnidadeId);

        await PafApi.OkAsync<Guid>(operador, HttpMethod.Put, $"/usuarios/{usuario.UserId}/unidades", null,
            new SetUsuarioUnidadesCommand(Guid.Parse(usuario.UserId), [u2.UnidadeId], false));

        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
        (await SessaoAtivaAsync(_tenant.TenantId, PafTenantCliente.SessaoDoToken(token.AccessToken))).ShouldBeFalse();
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RetirarOutraUnidadeDoEscopo_SessaoContinua()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        using var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        using var operador = await OperadorRaizComEscopoAsync(u1.UnidadeId, u2.UnidadeId);

        await PafApi.OkAsync<Guid>(operador, HttpMethod.Put, $"/usuarios/{usuario.UserId}/unidades", null,
            new SetUsuarioUnidadesCommand(Guid.Parse(usuario.UserId), [u1.UnidadeId], false));

        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldHaveSingleItem();
    }

    // The scope row gone without the edit's revocation (failed half-way): the next request revokes.
    [Fact]
    public async Task UnidadeCorrenteForaDoEscopo_ProximaRequisicaoRevoga()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        await _tenant.ExecutarAsync($"""DELETE FROM paf."UsuariosUnidade" WHERE "UsuarioId" = {Guid.Parse(usuario.UserId)}""");
        using var cliente = _tenant.Cliente(token.AccessToken);

        var resposta = await cliente.GetAsync(Perfil);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(RevogacaoSessao.MensagemUnidadeRetirada);
        (await SessaoAtivaAsync(_tenant.TenantId, PafTenantCliente.SessaoDoToken(token.AccessToken))).ShouldBeFalse();
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
    }

    [Fact]
    public async Task UsuarioDesativadoNoKit_RevogadoNaRenovacaoDaMarca()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        using var cliente = _tenant.Cliente(token.AccessToken);
        await ComUsuariosAsync(async usuarios =>
        {
            var fsh = await usuarios.FindByIdAsync(usuario.UserId);
            fsh!.Deactivate();
            (await usuarios.UpdateAsync(fsh)).Succeeded.ShouldBeTrue();
        });

        // Declared limit (seção 9): inside the renewal interval the deactivation is not seen yet.
        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var sessaoId = PafTenantCliente.SessaoDoToken(token.AccessToken);
        (await _tenant.ExecutarAsync($"""
            UPDATE paf."AtividadesSessao" SET "UltimaAtividadeEm" = now() - interval '2 minutes' WHERE "SessaoId" = {sessaoId}
            """)).ShouldBe(1);

        (await cliente.GetAsync(Perfil)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessaoAtivaAsync(_tenant.TenantId, sessaoId)).ShouldBeFalse();
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
    }

    [Fact]
    public async Task OperadorRaiz_SessaoRevogada_RecusadoTambemNoTenantCliente()
    {
        var token = await new AuthHelper(factory).GetRootAdminTokenAsync();
        using var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        cliente.DefaultRequestHeaders.Add("tenant", _tenant.TenantId);
        const string rota = $"{TestConstants.IdentityBasePath}/users/search?PageNumber=1&PageSize=5";
        (await cliente.GetAsync(rota)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await RevogarAsync(TestConstants.RootTenantId, PafTenantCliente.SessaoDoToken(token.AccessToken));

        (await cliente.GetAsync(rota)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Station tokens are told apart by paf_papel, as in ConferenciaRequisicaoMiddleware: paf_unidade alone
    // does not skip the session check.
    [Fact]
    public async Task TokenComUnidadeSemPapel_NaoEhIsentoDaConferenciaDeSessao()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        string forjado;
        using (var scope = factory.Services.CreateScope())
        {
            (forjado, _) = await scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessOnlyAsync(
                usuario.UserId,
                [
                    new Claim(JwtRegisteredClaimNames.Sub, usuario.UserId),
                    new Claim(ClaimTypes.NameIdentifier, usuario.UserId),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                    new Claim(ClaimConstants.Tenant, _tenant.TenantId),
                    new Claim(PafClaims.Unidade, unidade.UnidadeId.ToString()),
                ],
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
        }

        using var cliente = _tenant.Cliente(forjado);
        var resposta = await cliente.GetAsync(Perfil);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EmissaoDoTokenFalha_SessaoPendenteERevogada()
    {
        var usuario = await IdentityUserSeeder.CreateLoginableUserAsync(factory, _tenant.Admin, "paf-falha", _tenant.TenantId);

        // The kit handler over the host's real services, only the token issuer swapped: a derived host would
        // replace Hangfire's global in-memory storage and break the rest of the collection when disposed.
        using (var scope = factory.Services.CreateScope())
        {
            _ = _tenant.Contexto(scope, unidadeCorrente: null);
            var s = scope.ServiceProvider;
            var handler = new GenerateTokenCommandHandler(
                s.GetRequiredService<IIdentityService>(),
                new TokenServiceQueFalha(),
                s.GetRequiredService<ISecurityAudit>(),
                s.GetRequiredService<IRequestContext>(),
                s.GetRequiredService<IOutboxStore>(),
                s.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>(),
                s.GetRequiredService<ISessionService>(),
                s.GetRequiredService<ILogger<GenerateTokenCommandHandler>>());

            await Should.ThrowAsync<InvalidOperationException>(
                handler.Handle(new GenerateTokenCommand(usuario.Email, usuario.Password), CancellationToken.None).AsTask());
        }

        (await SessoesAtivasAsync(_tenant.TenantId, usuario.UserId)).ShouldBe(0);
    }

    // A refresh of the old session is waiting on the unit lock when the login on the other machine revokes
    // it; once the lock is free it must stop, not re-occupy and delete the new session's row.
    [Fact]
    public async Task RenovacaoEsperandoOBloqueio_SessaoRevogadaNoIntervalo_NaoReocupa()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        var token = await _tenant.EntrarAsync(usuario);
        var sessaoId = PafTenantCliente.SessaoDoToken(token.AccessToken);
        (await _tenant.ExecutarAsync($"""DELETE FROM paf."AtividadesSessao" WHERE "SessaoId" = {sessaoId}""")).ShouldBe(1);

        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        Task<HttpResponseMessage> renovacao;
        await using (var bloqueio = await db.Database.BeginTransactionAsync())
        {
            var chave = $"paf-licenca:{_tenant.TenantId}:{unidade.UnidadeId}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({chave}, 0))");
            renovacao = _tenant.RenovarAsync(token);
            await EsperarBloqueioAsync(renovacao);
            await RevogarAsync(_tenant.TenantId, sessaoId);
            await bloqueio.CommitAsync();
        }

        using var resposta = await renovacao;
        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await resposta.Content.ReadAsStringAsync());
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldBeEmpty();
    }

    // The licence behavior validates the credential and the kit handler validates it again; a TOTP code must
    // survive both.
    [Fact]
    public async Task LoginCom2FA_CodigoValido_AceitoComLicenca()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        string? chave = null;
        await ComUsuariosAsync(async usuarios =>
        {
            var fsh = await usuarios.FindByIdAsync(usuario.UserId);
            await usuarios.ResetAuthenticatorKeyAsync(fsh!);
            await usuarios.SetTwoFactorEnabledAsync(fsh!, true);
            chave = await usuarios.GetAuthenticatorKeyAsync(fsh!);
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", _tenant.TenantId);
        using var resposta = await client.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/token/issue",
            new { email = usuario.Email, password = usuario.Password, twoFactorCode = Totp(chave!) });

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        (await _tenant.AtividadesAsync(Guid.Parse(usuario.UserId))).ShouldHaveSingleItem();
    }

    private async Task<HttpClient> OperadorRaizComEscopoAsync(params Guid[] unidades)
    {
        var rootId = await new PafApi(factory).RootAdminIdAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, unidadeCorrente: null);
            for (var ordem = 0; ordem < unidades.Length; ordem++)
            {
                db.UsuariosUnidade.Add(UsuarioUnidade.Criar(rootId, unidades[ordem], ordem));
            }

            await db.SaveChangesAsync();
        }

        var token = await new AuthHelper(factory).GetRootAdminTokenAsync();
        var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        cliente.DefaultRequestHeaders.Add("tenant", _tenant.TenantId);
        return cliente;
    }

    private async Task ComUsuariosAsync(Func<UserManager<FshUser>, Task> acao)
    {
        using var scope = factory.Services.CreateScope();
        _ = _tenant.Contexto(scope, unidadeCorrente: null);
        await acao(scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>());
    }

    private async Task<AppTenantInfo> TenantAsync(string tenantId)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId);
        tenant.ShouldNotBeNull();
        return tenant;
    }

    // Synchronous on purpose, like PafTenantCliente.Contexto: the Finbuckle context is an AsyncLocal.
    private static ISessionService Sessoes(IServiceScope scope, AppTenantInfo tenant)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);
        return scope.ServiceProvider.GetRequiredService<ISessionService>();
    }

    private async Task<bool> SessaoAtivaAsync(string tenantId, Guid sessaoId)
    {
        var tenant = await TenantAsync(tenantId);
        using var scope = factory.Services.CreateScope();
        var sessoes = Sessoes(scope, tenant);
        return (await sessoes.GetSessionAsync(sessaoId, CancellationToken.None))?.IsActive == true;
    }

    private async Task<int> SessoesAtivasAsync(string tenantId, string usuarioId)
    {
        var tenant = await TenantAsync(tenantId);
        using var scope = factory.Services.CreateScope();
        var sessoes = Sessoes(scope, tenant);
        return (await sessoes.GetUserSessionsForAdminAsync(usuarioId, CancellationToken.None)).Count;
    }

    private async Task RevogarAsync(string tenantId, Guid sessaoId)
    {
        var tenant = await TenantAsync(tenantId);
        using var scope = factory.Services.CreateScope();
        var sessoes = Sessoes(scope, tenant);
        (await sessoes.RevokeSessionForAdminAsync(sessaoId, "teste", "teste", CancellationToken.None)).ShouldBeTrue();
    }

    private async Task EsperarBloqueioAsync(Task renovacao)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            renovacao.IsCompleted.ShouldBeFalse("the refresh finished without waiting for the unit lock");
            var esperando = await db.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted""")
                .SingleAsync();
            if (esperando > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("the refresh never waited on the unit lock");
    }

    // RFC 6238 with the authenticator defaults of ASP.NET Core Identity: SHA-1, 30 s step, 6 digits.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "RFC 6238 authenticator codes are HMAC-SHA1, the algorithm Identity verifies.")]
    private static string Totp(string chaveBase32)
    {
        var chave = Base32(chaveBase32);
        var passo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var contador = BitConverter.GetBytes(passo);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(contador);
        }

        var hash = HMACSHA1.HashData(chave, contador);
        var deslocamento = hash[^1] & 0x0F;
        var binario = ((hash[deslocamento] & 0x7F) << 24) | (hash[deslocamento + 1] << 16)
            | (hash[deslocamento + 2] << 8) | hash[deslocamento + 3];
        return (binario % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string texto)
    {
        const string alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var acumulado = 0;
        var saida = new List<byte>();
        foreach (var c in texto.TrimEnd('=').ToUpperInvariant())
        {
            acumulado = (acumulado << 5) | alfabeto.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                saida.Add((byte)(acumulado >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. saida];
    }

    private sealed class TokenServiceQueFalha : ITokenService
    {
        public Task<TokenResponse> IssueAsync(string subject, IEnumerable<Claim> claims, string? tenant = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("falha simulada na emissão do token");

        public Task<(string AccessToken, DateTime ExpiresAtUtc)> IssueAccessOnlyAsync(
            string subject, IEnumerable<Claim> claims, TimeSpan? lifetime = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("falha simulada na emissão do token");
    }
}
