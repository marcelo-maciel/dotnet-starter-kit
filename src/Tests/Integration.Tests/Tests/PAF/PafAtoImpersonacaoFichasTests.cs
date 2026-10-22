using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Regra 6 da onda 1 (D28) sobre o cancelar (F04) e o reabilitar (F05) das fichas do dia: os dois escrevem por SQL bruto
/// fora do guichê, então gravam o <c>AtoImpersonacao</c> explicitamente na transação da escrita; uma escrita recusada
/// depois do ato desfaz o ato junto.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafAtoImpersonacaoFichasTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Impersonacao = TestConstants.IdentityBasePath + "/impersonation";

    private readonly PafTenantCliente _tenant = new(factory, "paf-ato-fichas");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Codefi_Impersonando_CancelaEReabilita_UmAtoPorEscritaERecusaNaoGrava()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var alvo = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId, PafPermissions.FichasDoDia.Cancelar, PafPermissions.FichasDoDia.Reabilitar);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        var operador = await new PafApi(factory).RootAdminIdAsync();
        using var suporte = _tenant.Cliente(token);

        await PostarAsync(suporte, c.UnidadeId, ficha.AtendimentoId, "cancelar", HttpStatusCode.OK);
        (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Cancelado);
        (await AtosAsync(c.UnidadeId, concessao)).ShouldHaveSingleItem();

        await PostarAsync(suporte, c.UnidadeId, ficha.AtendimentoId, "reabilitar", HttpStatusCode.OK);
        (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
        var atos = await AtosAsync(c.UnidadeId, concessao);
        atos.Count.ShouldBe(2, "um ato por escrita");
        atos.ShouldAllBe(a => a.OperadorId == operador && a.UnidadeId == c.UnidadeId);

        // Refused under the ficha lock, after the act was noted: the rollback takes nothing with it.
        await PostarAsync(suporte, c.UnidadeId, ficha.AtendimentoId, "reabilitar", HttpStatusCode.Conflict);
        (await AtosAsync(c.UnidadeId, concessao)).Count.ShouldBe(2, "escrita recusada não grava ato");
    }

    private static async Task PostarAsync(HttpClient cliente, Guid unidade, Guid atendimentoId, string acao, HttpStatusCode esperado)
    {
        using var resposta = await PafApi.EnviarAsync(cliente, HttpMethod.Post, $"/fichas/atendimentos/{atendimentoId}/{acao}", unidade, corpo: null);
        resposta.StatusCode.ShouldBe(esperado, await resposta.Content.ReadAsStringAsync());
    }

    private async Task<string> IniciarAsync(HttpClient ator, string alvo)
    {
        using var resposta = await ator.PostAsJsonAsync($"{Impersonacao}/start", new
        {
            targetUserId = alvo,
            targetTenantId = _tenant.TenantId,
            reason = "teste do AtoImpersonacao nas fichas do dia",
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
}
