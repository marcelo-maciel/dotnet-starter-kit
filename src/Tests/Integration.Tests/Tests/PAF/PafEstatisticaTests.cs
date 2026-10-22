using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Domain;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// E0, RF07 entry point through the real HTTP pipeline: permission per type, shape errors as 400, and the
/// caller's effective scope (never the current unit) as the boundary of every filter, refused and not narrowed.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstatisticaTests(FshWebApplicationFactory factory)
{
    private const string Periodo = "dataInicial=2026-10-01&dataFinal=2026-10-18";

    [Fact]
    public async Task Permissoes_SemViewRecusa403_Tipo4ExigeViewPorAtendente_DemaisTipos200()
    {
        var cenario = await MontarAsync("paf-est-perm");

        using var semView = await EntrarAsync(cenario.Cliente, await cenario.Cliente.CriarUsuarioAsync(cenario.U1.UnidadeId));
        using (var recusada = await GetAsync(semView, $"tipo=1&{Periodo}"))
        {
            recusada.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // View alone does not open the type 4 report per atendente (D10).
        using (var porAtendente = await GetAsync(cenario.ComView, $"tipo=4&{Periodo}"))
        {
            porAtendente.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await porAtendente.Content.ReadAsStringAsync()).ShouldNotContain("linhas", Case.Insensitive);
        }

        // A client role holding ViewPorAtendente keeps it (not root since D10) and reads type 4.
        var gerente = await cenario.Cliente.CriarUsuarioAsync(cenario.U1.UnidadeId);
        await cenario.Cliente.DarPapelAsync(gerente.UserId, PafPermissions.Estatisticas.View, PafPermissions.Estatisticas.ViewPorAtendente);
        using (var comPermissao = await EntrarAsync(cenario.Cliente, gerente))
        using (var tipo4 = await GetAsync(comPermissao, $"tipo=4&{Periodo}"))
        {
            tipo4.StatusCode.ShouldBe(HttpStatusCode.OK, await tipo4.Content.ReadAsStringAsync());
            (await tipo4.DeserializeAsync<ResultadoEstatisticaDto>()).Tipo.ShouldBe(TipoEstatistica.AtendimentosPorAtendente);
        }

        foreach (var tipo in Enumerable.Range(1, 10).Where(t => t != 4))
        {
            var agrupamento = tipo >= 9 ? "&agrupamento=Setor" : string.Empty;
            using var resposta = await GetAsync(cenario.ComView, $"tipo={tipo}&{Periodo}{agrupamento}");
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, $"tipo {tipo}: {await resposta.Content.ReadAsStringAsync()}");
            (await resposta.DeserializeAsync<ResultadoEstatisticaDto>()).Tipo.ShouldBe((TipoEstatistica)tipo);
        }
    }

    [Fact]
    public async Task OperadorRaiz_ComViewPorAtendente_ConsultaOTipo4()
    {
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();

        using var resposta = await GetAsync(root, $"tipo=AtendimentosPorAtendente&{Periodo}");

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        (await resposta.DeserializeAsync<ResultadoEstatisticaDto>()).Tipo.ShouldBe(TipoEstatistica.AtendimentosPorAtendente);
    }

    [Fact]
    public async Task FormaInvalida_Recusada400()
    {
        var cenario = await MontarAsync("paf-est-400");

        string[] invalidas =
        [
            "tipo=1&dataInicial=2026-10-18&dataFinal=2026-10-17",
            $"tipo=1&{Periodo}&agrupamento=Fila",
            $"tipo=9&{Periodo}",
            $"tipo=10&{Periodo}",
            $"tipo=11&{Periodo}",
            $"tipo=0&{Periodo}",
            $"tipo=abc&{Periodo}",
            Periodo,
            "tipo=1",
            $"tipo=9&{Periodo}&agrupamento=7",
            $"tipo=1&{Periodo}&somenteEstacoesComAtendimento=true",
            $"tipo=1&{Periodo}&prioridades={Guid.NewGuid()}",
            $"tipo=1&{Periodo}&filas={Guid.NewGuid()}",
        ];

        foreach (var consulta in invalidas)
        {
            using var resposta = await GetAsync(cenario.ComView, consulta);
            resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{consulta}: {await resposta.Content.ReadAsStringAsync()}");
        }

        // Inclusive period: one single day is a valid period.
        using var umDia = await GetAsync(cenario.ComView, "tipo=1&dataInicial=2026-10-18&dataFinal=2026-10-18");
        umDia.StatusCode.ShouldBe(HttpStatusCode.OK, await umDia.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Escopo_UnidadeFilaEPrioridadeForaDoEscopo403_DeOutraUnidadeDoEscopo200()
    {
        var cenario = await MontarAsync("paf-est-escopo");
        var fora = await cenario.Cliente.CriarUnidadeAsync(limiteSessoes: null);
        var (filaFora, prioridadeFora) = await CriarFilaEPrioridadeAsync(cenario.Cliente, fora.UnidadeId);
        var (filaU2, prioridadeU2) = await CriarFilaEPrioridadeAsync(cenario.Cliente, cenario.U2.UnidadeId);

        string[] proibidas =
        [
            $"tipo=1&{Periodo}&unidades={fora.UnidadeId}",
            $"tipo=1&{Periodo}&unidades={cenario.U1.UnidadeId}&unidades={fora.UnidadeId}",
            $"tipo=1&{Periodo}&unidades={Guid.NewGuid()}",
            $"tipo=1&{Periodo}&prioridades={prioridadeFora}",
            $"tipo=1&{Periodo}&filas={filaFora}",
        ];
        foreach (var consulta in proibidas)
        {
            using var resposta = await GetAsync(cenario.ComView, consulta);
            resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{consulta}: {await resposta.Content.ReadAsStringAsync()}");
            (await resposta.Content.ReadAsStringAsync()).ShouldNotContain("linhas", Case.Insensitive);
        }

        // The session's current unit is U1: a prioridade of U2, also in scope, is found by the named filter bypass.
        using (var deU2 = await GetAsync(cenario.ComView, $"tipo=1&{Periodo}&prioridades={prioridadeU2}&filas={filaU2}"))
        {
            deU2.StatusCode.ShouldBe(HttpStatusCode.OK, await deU2.Content.ReadAsStringAsync());
        }

        // In scope but outside the unidades asked for: refused, because the filter would silently empty the result.
        using (var foraDasPedidas = await GetAsync(cenario.ComView, $"tipo=1&{Periodo}&unidades={cenario.U1.UnidadeId}&prioridades={prioridadeU2}"))
        {
            foraDasPedidas.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await foraDasPedidas.Content.ReadAsStringAsync());
        }

        using var subconjunto = await GetAsync(cenario.ComView, $"tipo=1&{Periodo}&unidades={cenario.U2.UnidadeId}");
        subconjunto.StatusCode.ShouldBe(HttpStatusCode.OK, await subconjunto.Content.ReadAsStringAsync());
    }

    private sealed record Cenario(PafTenantCliente Cliente, UnidadeTeste U1, UnidadeTeste U2, HttpClient ComView);

    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var cliente = new PafTenantCliente(factory, prefixo);
        await cliente.InicializarAsync();
        var u1 = await cliente.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await cliente.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await cliente.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        await cliente.DarPapelAsync(usuario.UserId, PafPermissions.Estatisticas.View);
        return new Cenario(cliente, u1, u2, await EntrarAsync(cliente, usuario));
    }

    private async Task<(Guid Fila, Guid Prioridade)> CriarFilaEPrioridadeAsync(PafTenantCliente cliente, Guid unidadeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var fila = Fila.Criar(unidadeId, "Caixa", "Caixa", setorId: null);
        var prioridade = Prioridade.Criar(unidadeId, "Normal", valor: 0);
        db.Filas.Add(fila);
        db.Prioridades.Add(prioridade);
        await db.SaveChangesAsync();
        return (fila.Id, prioridade.Id);
    }

    private static async Task<HttpClient> EntrarAsync(PafTenantCliente cliente, Integration.Tests.Tests.Sessions.SeededUser usuario) =>
        cliente.Cliente((await cliente.EntrarAsync(usuario)).AccessToken);

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string consulta) =>
        client.GetAsync($"{PafApi.Base}/estatistica?{consulta}");
}
