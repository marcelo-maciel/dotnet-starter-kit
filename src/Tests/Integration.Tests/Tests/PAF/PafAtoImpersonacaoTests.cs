using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// <c>AtoImpersonacao</c> (seção 9, D28): cada escrita de cadastro feita pela impersonação da Codefi grava uma linha com
/// o operador (<c>act_sub</c>) e a concessão (<c>jti</c>) na transação da própria escrita; sem impersonação, nada.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafAtoImpersonacaoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Impersonacao = TestConstants.IdentityBasePath + "/impersonation";

    private readonly PafTenantCliente _tenant = new(factory, "paf-ato");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Codefi_Impersonando_CriaFilaEEditaEstacao_UmAtoPorEscritaComOperadorEConcessao()
    {
        var (unidade, alvo) = await AlvoAsync();
        var prioridade = await CriarPrioridadeAsync(unidade.UnidadeId);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        var operador = await new PafApi(factory).RootAdminIdAsync();
        using var suporte = _tenant.Cliente(token);

        var fila = await PafApi.OkAsync<Guid>(suporte, HttpMethod.Post, "/filas", unidade.UnidadeId, NovaFila("Caixa", prioridade, "C"));
        var depoisDaFila = await AtosAsync(unidade.UnidadeId, concessao);
        depoisDaFila.ShouldHaveSingleItem();

        // Refused by the handler before SaveChanges: the noted act never reaches the database.
        using (var recusada = await PafApi.EnviarAsync(suporte, HttpMethod.Put, $"/estacoes/{unidade.GuicheA}", unidade.UnidadeId,
            new UpdateEstacaoCommand(unidade.GuicheA, "Guichê do suporte", [], [])))
        {
            recusada.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await recusada.Content.ReadAsStringAsync());
        }

        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldHaveSingleItem("escrita recusada não grava ato");

        await PafApi.OkAsync<EstacaoAlteradaDto>(suporte, HttpMethod.Put, $"/estacoes/{unidade.GuicheA}", unidade.UnidadeId,
            new UpdateEstacaoCommand(unidade.GuicheA, "Guichê do suporte", [new EstacaoFilaItem("Guiche", fila, 1)], [new EstacaoPrioridadeItem(prioridade, 1)]));

        var atos = await AtosAsync(unidade.UnidadeId, concessao);
        atos.Count.ShouldBe(2, "um ato por escrita");
        atos.ShouldAllBe(a => a.OperadorId == operador && a.UnidadeId == unidade.UnidadeId && a.AtendimentoId == null);
        fila.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Codefi_Impersonando_RevogaVinculo_GravaOAtoNaTransacaoDoExecuteUpdate()
    {
        var (unidade, alvo) = await AlvoAsync();
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        using var suporte = _tenant.Cliente(token);

        using var resposta = await PafApi.EnviarAsync(suporte, HttpMethod.Post, $"/estacoes/{unidade.GuicheA}/vinculo/revogar", unidade.UnidadeId, corpo: null);

        resposta.IsSuccessStatusCode.ShouldBeTrue(await resposta.Content.ReadAsStringAsync());
        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SemImpersonacao_UsuarioCriaFila_NenhumAto()
    {
        var (unidade, alvo) = await AlvoAsync();
        var prioridade = await CriarPrioridadeAsync(unidade.UnidadeId);
        using var proprio = _tenant.Cliente((await _tenant.EntrarAsync(alvo)).AccessToken);

        await PafApi.OkAsync<Guid>(proprio, HttpMethod.Post, "/filas", unidade.UnidadeId, NovaFila("Caixa", prioridade, "C"));

        (await TodosOsAtosAsync(unidade.UnidadeId)).ShouldBe(0, "sem act_sub nenhum ato é gravado");
    }

    [Fact]
    public async Task Codefi_Impersonando_FalhaDepoisDoAto_DesfazAtoEEscritaJuntos()
    {
        var (unidade, alvo) = await AlvoAsync();
        var prioridade = await CriarPrioridadeAsync(unidade.UnidadeId);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        using var suporte = _tenant.Cliente(token);

        // A deferred constraint trigger fails at COMMIT, after both the act and the fila rows were inserted, so the
        // failure lands strictly after the act; only this test's fila name trips it.
        var sufixo = Guid.NewGuid().ToString("N")[..12];
        var nome = $"falha-{sufixo}";
        await _tenant.ExecutarAsync(FormattableStringFactory.Create(
            $"CREATE FUNCTION paf.s11a_falha_{sufixo}() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN " +
            $"IF NEW.\"Nome\" = '{nome}' THEN RAISE EXCEPTION 'falha forcada'; END IF; RETURN NULL; END $f$"));
        try
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create(
                $"CREATE CONSTRAINT TRIGGER s11a_falha_{sufixo} AFTER INSERT ON paf.\"Filas\" " +
                $"DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION paf.s11a_falha_{sufixo}()"));

            using var resposta = await PafApi.EnviarAsync(suporte, HttpMethod.Post, "/filas", unidade.UnidadeId, NovaFila(nome, prioridade, "F"));

            resposta.IsSuccessStatusCode.ShouldBeFalse(await resposta.Content.ReadAsStringAsync());
            (await AtosAsync(unidade.UnidadeId, concessao)).ShouldBeEmpty("o ato sai junto com a escrita desfeita");
            (await FilasComNomeAsync(unidade.UnidadeId, nome)).ShouldBe(0, "a fila sai junto com o ato");
        }
        finally
        {
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP TRIGGER IF EXISTS s11a_falha_{sufixo} ON paf.\"Filas\""));
            await _tenant.ExecutarAsync(FormattableStringFactory.Create($"DROP FUNCTION IF EXISTS paf.s11a_falha_{sufixo}()"));
        }

        // The same write without the trigger goes through and records its act: the failure above was the trigger's.
        await PafApi.OkAsync<Guid>(suporte, HttpMethod.Post, "/filas", unidade.UnidadeId, NovaFila(nome, prioridade, "F"));
        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldHaveSingleItem();
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private static CreateFilaCommand NovaFila(string nome, Guid prioridade, string letra) =>
        new(nome, nome, HoraInicial: null, HoraFinal: null, [new FilaPrioridadeItem(prioridade, letra, 1, 1)]);

    private async Task<(UnidadeTeste Unidade, SeededUser Alvo)> AlvoAsync()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var alvo = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId, PafPermissions.Filas.Create, PafPermissions.Estacoes.Update, PafPermissions.VinculosEstacao.Revoke);
        return (unidade, alvo);
    }

    private async Task<string> IniciarAsync(HttpClient ator, string alvo)
    {
        using var resposta = await ator.PostAsJsonAsync($"{Impersonacao}/start", new
        {
            targetUserId = alvo,
            targetTenantId = _tenant.TenantId,
            reason = "teste do AtoImpersonacao",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<List<AtoImpersonacao>> AtosAsync(Guid unidade, Guid concessao)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidade).AtosImpersonacao.AsNoTracking().Where(a => a.ConcessaoId == concessao).ToListAsync();
    }

    private async Task<Guid> CriarPrioridadeAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidade);
        var prioridade = Prioridade.Criar(unidade, "Normal", 0);
        db.Prioridades.Add(prioridade);
        await db.SaveChangesAsync();
        return prioridade.Id;
    }

    private async Task<int> FilasComNomeAsync(Guid unidade, string nome)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidade).Filas.CountAsync(f => f.Nome == nome);
    }

    private async Task<int> TodosOsAtosAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidade).AtosImpersonacao.CountAsync();
    }
}
