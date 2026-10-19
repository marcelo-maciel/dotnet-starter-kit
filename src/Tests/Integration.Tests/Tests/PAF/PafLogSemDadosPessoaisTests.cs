using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Identity.Domain;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Usuarios;
using FSH.Modules.PAF.Observabilidade;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;
using Mediator;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR.Client;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RNF06 e ARQ l.392 (O10) pelo host real, com o log em JSON compacto num arquivo (<see cref="PafHostValkey.ArquivoDeLog"/>):
/// login, emissão pelo totem e conexão da pessoa ao PafHub com o token na URL não deixam e-mail, CPF nem token em
/// nenhum evento, e o evento da conexão traz tenant, unidade, TraceId e o e-mail omitido.
/// </summary>
[Collection(PafHostValkeyColecao.Nome)]
public sealed partial class PafLogSemDadosPessoaisTests(PafHostValkey host) : IAsyncLifetime
{
    private static readonly TimeSpan EsperaDoLog = TimeSpan.FromSeconds(10);

    private readonly string _tenantId = $"paf-log-{Guid.NewGuid().ToString("N")[..8]}";
    private AppTenantInfo _tenant = default!;
    private HttpClient _admin = default!;
    private HubConnection? _conexao;

    public async Task InitializeAsync()
    {
        using var root = await ClienteRootAsync();
        var adminEmail = $"admin-{_tenantId}@paf.test";
        using var criar = await root.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = _tenantId,
            name = $"Tenant {_tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{_tenantId}.issuer",
        });
        criar.StatusCode.ShouldBe(HttpStatusCode.Created, await criar.Content.ReadAsStringAsync());
        await TenantProvisioningWait.WaitForProvisioningAsync(root, _tenantId);
        _admin = Cliente((await TokenAsync(_tenantId, adminEmail, TestConstants.DefaultPassword)).AccessToken);

        using var scope = host.Services.CreateScope();
        _tenant = (await scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(_tenantId))!;
        _tenant.ShouldNotBeNull();
    }

    public async Task DisposeAsync()
    {
        if (_conexao is not null)
        {
            await _conexao.DisposeAsync();
        }

        _admin?.Dispose();
    }

    [Fact]
    public async Task LoginEmissaoEHubComTokenNaUrl_NenhumEventoTemEmailCpfOuToken_EAConexaoTrazTenantUnidadeETraceId()
    {
        long inicio = TamanhoDoLog();
        var c = await CenarioAsync();
        var usuario = await UsuarioAsync(c.UnidadeId);
        var token = await TokenAsync(_tenantId, usuario.Email, usuario.Password);
        string credencialTotem = await CredencialTotemAsync(c);
        using (var totem = Cliente(credencialTotem))
        {
            await PafOperacao.EmitirOkAsync(totem, c.Fila, c.Prioridade);
        }

        _conexao = new HubConnectionBuilder()
            .WithUrl(
                $"http://localhost{PafHubRota.Caminho}?tenant={Uri.EscapeDataString(_tenantId)}&access_token={Uri.EscapeDataString(token.AccessToken)}",
                options =>
                {
                    // TestServer closes a WebSocket before the handshake (PafHubTests); the token travels in the URL.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                })
            .Build();
        await _conexao.StartAsync();
        await PafOperacao.ProntaAsync(_conexao);

        var conexaoDaPessoa = await EsperarEventoAsync(inicio, e =>
            e.TryGetProperty("UsuarioId", out var u) && u.GetString() == usuario.UserId
            && e.GetProperty("@mt").GetString()!.StartsWith("PafHub pessoa conectada", StringComparison.Ordinal));

        Propriedade(conexaoDaPessoa, UnidadeCorrenteMiddleware.PropriedadeTenant).ShouldBe(_tenantId);
        Propriedade(conexaoDaPessoa, UnidadeCorrenteMiddleware.PropriedadeUnidade).ShouldBe(c.UnidadeId.ToString());
        Propriedade(conexaoDaPessoa, UnidadeCorrenteMiddleware.PropriedadeUsuario).ShouldBe(usuario.UserId);
        Propriedade(conexaoDaPessoa, "@tr").ShouldNotBeNullOrWhiteSpace("o evento da requisição carrega o TraceId");

        string novo = LerLogDesde(inicio);
        string[] eventos = novo.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        eventos.Length.ShouldBeGreaterThan(0);
        // An event written inside an authenticated HTTP request (the kit's register handler, under the admin's token):
        // where the kit's enricher writes the e-mail, the PAF's placeholder is what the sink got.
        eventos.ShouldContain(
            l => l.Contains($"\"{EnriquecedorSemDadosPessoais.PropriedadeEmail}\":\"{EnriquecedorSemDadosPessoais.Omitido}\"", StringComparison.Ordinal),
            string.Join('\n', eventos));
        eventos.Where(l => Email().IsMatch(l)).ShouldBeEmpty("nenhum evento com e-mail (RNF06)");
        eventos.Where(l => Cpf().IsMatch(l)).ShouldBeEmpty("nenhum evento com CPF (RNF06)");
        foreach (string segredo in new[] { token.AccessToken, token.RefreshToken, credencialTotem })
        {
            // The last segment (a JWT's signature, or the whole opaque refresh token) is unique to that credential.
            string assinatura = segredo.Split('.')[^1];
            eventos.Where(l => l.Contains(assinatura, StringComparison.Ordinal)).ShouldBeEmpty("token em nenhum log");
        }
    }

    // C16: the password an administrator defines, accepted or refused, and the login with it leave no trace in the log.
    [Fact]
    public async Task SenhaDefinidaPeloAdministrador_AceitaOuRecusada_NenhumEventoTemASenha()
    {
        long inicio = TamanhoDoLog();
        var c = await CenarioAsync();
        var usuario = await UsuarioAsync(c.UnidadeId);
        string sufixo = Guid.NewGuid().ToString("N")[..10];
        string fraca = $"c16fraca{sufixo}";
        string forte = $"C16Forte{sufixo}!";
        string rota = $"/usuarios/{usuario.UserId}/senha";

        using (var recusada = await PafApi.EnviarAsync(_admin, HttpMethod.Post, rota, null, new DefinirSenhaUsuarioPafRequest(fraca, fraca)))
        {
            recusada.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await recusada.Content.ReadAsStringAsync());
        }

        using (var diferente = await PafApi.EnviarAsync(_admin, HttpMethod.Post, rota, null, new DefinirSenhaUsuarioPafRequest(forte, fraca)))
        {
            diferente.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await diferente.Content.ReadAsStringAsync());
        }

        await PafApi.OkAsync<Guid>(_admin, HttpMethod.Post, rota, null, new DefinirSenhaUsuarioPafRequest(forte, forte));
        using (var repetida = await PafApi.EnviarAsync(_admin, HttpMethod.Post, rota, null, new DefinirSenhaUsuarioPafRequest(forte, forte)))
        {
            repetida.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await repetida.Content.ReadAsStringAsync());
        }

        await TokenAsync(_tenantId, usuario.Email, forte);

        // The kit logs the change at Information: once it is in the file, the requests above were all written too.
        await EsperarEventoAsync(inicio, e =>
            e.GetProperty("@mt").GetString()!.StartsWith("Password changed for user", StringComparison.Ordinal)
            && e.TryGetProperty("UserId", out var u) && u.ToString() == usuario.UserId);
        string[] eventos = LerLogDesde(inicio).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        eventos.Where(l => l.Contains(fraca, StringComparison.Ordinal) || l.Contains(forte, StringComparison.Ordinal))
            .ShouldBeEmpty("senha em nenhum log");
    }

    // ─── log ─────────────────────────────────────────────────────

    // Compact JSON escapes '@' nowhere, so a plain pattern over the raw line sees every property and the message.
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // Formatted, or 11 bare digits not glued to other digits (ids, epoch ms and GUID runs stay out).
    [GeneratedRegex(@"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b|(?<!\d)\d{11}(?!\d)")]
    private static partial Regex Cpf();

    private static string? Propriedade(JsonElement evento, string nome) =>
        evento.TryGetProperty(nome, out var valor)
            ? valor.GetString()
            : throw new ShouldAssertException($"evento sem {nome}: {evento.GetRawText()}");

    private long TamanhoDoLog() => File.Exists(host.ArquivoDeLog) ? new FileInfo(host.ArquivoDeLog).Length : 0;

    private string LerLogDesde(long inicio)
    {
        if (!File.Exists(host.ArquivoDeLog))
        {
            return string.Empty;
        }

        // The sink holds the file open for writing; share it instead of copying.
        using var arquivo = new FileStream(host.ArquivoDeLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        arquivo.Seek(inicio, SeekOrigin.Begin);
        using var leitor = new StreamReader(arquivo, Encoding.UTF8);
        return leitor.ReadToEnd();
    }

    private async Task<JsonElement> EsperarEventoAsync(long inicio, Func<JsonElement, bool> criterio)
    {
        var relogio = Stopwatch.StartNew();
        while (true)
        {
            foreach (string linha in LerLogDesde(inicio).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var documento = JsonDocument.Parse(linha);
                if (criterio(documento.RootElement))
                {
                    return documento.RootElement.Clone();
                }
            }

            if (relogio.Elapsed > EsperaDoLog)
            {
                throw new ShouldAssertException($"evento esperado não apareceu em {EsperaDoLog.TotalSeconds} s no log {host.ArquivoDeLog}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    // ─── cenário ─────────────────────────────────────────────────

    private sealed record Cenario(Guid UnidadeId, Guid Fila, Guid Prioridade, Guid Totem);

    // Synchronous on purpose: the Finbuckle context is an AsyncLocal that would not flow back from an async helper.
    private PafDbContext Contexto(IServiceScope scope, Guid? unidadeCorrente)
    {
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(_tenant);
        if (unidadeCorrente is { } unidadeId)
        {
            scope.ServiceProvider.GetRequiredService<IUnidadeCorrente>().Definir(unidadeId);
        }

        return scope.ServiceProvider.GetRequiredService<PafDbContext>();
    }

    private async Task<Cenario> CenarioAsync()
    {
        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var unidade = Unidade.Criar($"Loja {Guid.NewGuid():N}", documentoFiscal: null);
        var u = unidade.Id;
        var fila = Fila.Criar(u, "Caixa", "Caixa", setorId: null);
        var normal = Prioridade.Criar(u, "Normal", valor: 0);
        var guiche = Estacao.Criar(u, "Guichê 1", TipoEstacao.Guiche);
        var totem = Estacao.Criar(u, "Totem", TipoEstacao.Emissao);
        db.Unidades.Add(unidade);
        db.Filas.Add(fila);
        db.Prioridades.Add(normal);
        db.Estacoes.AddRange(guiche, totem);
        db.FilaPrioridades.Add(FilaPrioridade.Criar(u, fila.Id, normal.Id, "A", numeroInicial: 1, ordem: 0));
        db.EstacaoFilas.AddRange(
            EstacaoFila.Criar(u, guiche.Id, PapelEstacao.Guiche, fila.Id, 0),
            EstacaoFila.Criar(u, totem.Id, PapelEstacao.Emissao, fila.Id, 0));
        db.EstacaoPrioridades.Add(EstacaoPrioridade.Criar(u, guiche.Id, normal.Id, 0));
        // Started two days ago so the America/Sao_Paulo calendar day is inside the validity at any UTC hour.
        db.ContratosUnidade.Add(ContratoUnidade.Criar(u, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), null, "Teste", 5, PoliticaLimiteLicenca.Recusar));
        await db.SaveChangesAsync();
        return new Cenario(u, fila.Id, normal.Id, totem.Id);
    }

    private async Task<string> CredencialTotemAsync(Cenario c)
    {
        VinculoEstacaoGeradoDto link;
        using (var scope = host.Services.CreateScope())
        {
            Contexto(scope, c.UnidadeId);
            link = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new GerarVinculoEstacaoCommand(c.Totem));
        }

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", _tenantId);
        using var resgate = await client.PostAsJsonAsync("/api/v1/paf/estacoes/vinculo/resgatar", new { token = link.Token });
        resgate.StatusCode.ShouldBe(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
        var credenciais = await resgate.Content.ReadFromJsonAsync<CredenciaisEstacaoDto>();
        return credenciais!.Credenciais.Single(x => x.Papel == PafPapeisEstacao.Emissao).AccessToken;
    }

    private HttpClient Cliente(string accessToken)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("tenant", _tenantId);
        return client;
    }

    private async Task<HttpClient> ClienteRootAsync()
    {
        var token = await TokenAsync(TestConstants.RootTenantId, TestConstants.RootAdminEmail, TestConstants.DefaultPassword);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);
        return client;
    }

    private async Task<TokenResult> TokenAsync(string tenant, string email, string senha)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.IdentityBasePath}/token/issue");
        request.Headers.Add("tenant", tenant);
        request.Content = JsonContent.Create(new { email, password = senha });
        using var resposta = await client.SendAsync(request);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<TokenResult>())!;
    }

    /// <summary>Usuário com login confirmado e escopo na unidade dada.</summary>
    private async Task<SeededUser> UsuarioAsync(Guid unidade)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var email = $"paf-log-{id}@example.com";
        const string senha = "Test@1234!";
        using var registro = await _admin.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/register", new
        {
            firstName = "Seeded",
            lastName = "User",
            email,
            userName = $"paf-log-{id}",
            password = senha,
            confirmPassword = senha,
        });
        registro.StatusCode.ShouldBe(HttpStatusCode.Created, await registro.Content.ReadAsStringAsync());
        var usuarioId = (await registro.Content.ReadFromJsonAsync<RegisterResult>())!.UserId;

        using var scope = host.Services.CreateScope();
        var db = Contexto(scope, unidadeCorrente: null);
        var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<FshUser>>();
        var usuario = (await usuarios.FindByEmailAsync(email))!;
        (await usuarios.ConfirmEmailAsync(usuario, await usuarios.GenerateEmailConfirmationTokenAsync(usuario))).Succeeded.ShouldBeTrue();
        db.UsuariosUnidade.Add(UsuarioUnidade.Criar(Guid.Parse(usuarioId), unidade, 0));
        await db.SaveChangesAsync();
        return new SeededUser(usuarioId, email, senha);
    }
}
