using System.Globalization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Estatistica;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// E2, RF07 types 7 and 8 through the real host and PostgreSQL: the hour of creation in America/Sao_Paulo, the
/// inclusive period on the ficha's own data de operação, fila and prioridade of the emission, every emitting
/// station (totem included) and the scope sum with its breakdown per unidade.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstatisticaFichasTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private readonly PafTenantCliente _tenant = new(factory, "paf-est-fichas");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Tipo7_HoraDeSaoPaulo_02h59ZContaNaHora23DoDiaAnterior_PeriodoInclusivo()
    {
        var c = await _op.CriarAsync();
        using var consulta = await ConsultorAsync(c.UnidadeId);
        // 02:59Z is 23:59 of the previous day in São Paulo (UTC-3): both the hour and the day move back.
        await FichaEmAsync(c, 1, CanalEmissao.Totem, c.Totem, "2026-09-10 02:59:00+00");
        await FichaEmAsync(c, 2, CanalEmissao.Totem, c.Totem, "2026-09-10 03:00:00+00");
        // Last second of 10/09 in São Paulo: still inside a period that ends on 10/09.
        await FichaEmAsync(c, 3, CanalEmissao.Totem, c.Totem, "2026-09-11 02:59:59+00");
        // First second of 11/09 in São Paulo: outside it.
        await FichaEmAsync(c, 4, CanalEmissao.Totem, c.Totem, "2026-09-11 03:00:00+00");

        var dia09 = await ConsultarAsync(consulta, "tipo=7&dataInicial=2026-09-09&dataFinal=2026-09-09");
        Corpo(dia09).ShouldBe(["23h=1"]);
        TotalDoEscopo(dia09).ShouldBe(1);

        var dia10 = await ConsultarAsync(consulta, "tipo=7&dataInicial=2026-09-10&dataFinal=2026-09-10");
        Corpo(dia10).ShouldBe(["00h=1", "23h=1"]);

        var periodo = await ConsultarAsync(consulta, "tipo=7&dataInicial=2026-09-09&dataFinal=2026-09-10");
        TotalDoEscopo(periodo).ShouldBe(3, "período inclusivo nas duas pontas, a ficha de 11/09 fica de fora");
        Corpo(periodo).ShouldBe(["00h=1", "23h=2"]);

        var vazio = await ConsultarAsync(consulta, "tipo=7&dataInicial=2026-09-08&dataFinal=2026-09-08");
        vazio.Linhas.ShouldBeEmpty();
        TotalDoEscopo(vazio).ShouldBe(0);
        vazio.Colunas.Select(col => col.Chave).ShouldBe(["unidade", "hora", "fichas"]);
        vazio.Totais.Single().UnidadeId.ShouldBeNull("sem ficha, só a linha do total do escopo");
    }

    [Fact]
    public async Task Tipo7_FichaEncaminhadaContaUmaVez_FiltroDeFilaEPrioridadeSaoOsDaEmissao()
    {
        var c = await _op.CriarAsync();
        using var consulta = await ConsultorAsync(c.UnidadeId);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var totem = await _op.TotemAsync(c);
        var emitida = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        using (var encaminhar = await atendente.PostAsJsonAsync(
            PafOperacao.Rota(chamada.AtendimentoId, "encaminhar"), new EncaminharAtendimentoRequest([c.Protocolo], c.Normal)))
        {
            encaminhar.StatusCode.ShouldBe(HttpStatusCode.OK, await encaminhar.Content.ReadAsStringAsync());
        }

        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.FichaId == emitida.FichaId))).ShouldBe(2, "a origem e a passagem no Protocolo");
        var periodo = await PeriodoDaFichaAsync(c, emitida.FichaId);

        TotalDoEscopo(await ConsultarAsync(consulta, $"tipo=7&{periodo}")).ShouldBe(1, "a ficha conta uma vez, não por atendimento");
        TotalDoEscopo(await ConsultarAsync(consulta, $"tipo=7&{periodo}&filas={c.Caixa}")).ShouldBe(1, "fila da emissão");
        TotalDoEscopo(await ConsultarAsync(consulta, $"tipo=7&{periodo}&filas={c.Protocolo}")).ShouldBe(0, "o destino do encaminhamento não é a fila da ficha");
        TotalDoEscopo(await ConsultarAsync(consulta, $"tipo=7&{periodo}&prioridades={c.Normal}")).ShouldBe(1);
        TotalDoEscopo(await ConsultarAsync(consulta, $"tipo=7&{periodo}&prioridades={c.Preferencial}")).ShouldBe(0);
        TotalDoEscopo(await ConsultarAsync(consulta, $"tipo=8&{periodo}&filas={c.Protocolo}")).ShouldBe(0);
    }

    [Fact]
    public async Task Tipo8_PorEstacaoECanal_TotemNaLinhaDoTotem_EstacaoDesativadaAparece()
    {
        var c = await _op.CriarAsync();
        using var consulta = await ConsultorAsync(c.UnidadeId);
        using var totem = await _op.TotemAsync(c);
        var pelaApi = await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        var periodo = await PeriodoDaFichaAsync(c, pelaApi.FichaId);
        // Guichê emission arrives with F12 (wave 3): written here as that endpoint will write it.
        await FichaHojeAsync(c, 10, CanalEmissao.Guiche, c.GuicheA);
        await FichaHojeAsync(c, 11, CanalEmissao.Guiche, c.GuicheA);
        await FichaHojeAsync(c, 12, CanalEmissao.Guiche, c.GuicheB);
        await _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Ativa" = false WHERE "Id" = {c.GuicheB}""");

        var resultado = await ConsultarAsync(consulta, $"tipo=8&{periodo}");

        resultado.Colunas.Select(col => col.Chave).ShouldBe(["unidade", "estacao", "canal", "fichas"]);
        resultado.Linhas.ShouldAllBe(l => l.UnidadeId == c.UnidadeId);
        resultado.Linhas.Select(Rotulo).ShouldBe(
        [
            $"Guichê 1|{FichasEmitidasPorGuiche.CanalGuiche}=2",
            $"Guichê 2|{FichasEmitidasPorGuiche.CanalGuiche}=1",
            $"Totem|{FichasEmitidasPorGuiche.CanalTotem}=1",
        ]);
        TotalDoEscopo(resultado).ShouldBe(4);
        TotalDaUnidade(resultado, c.UnidadeId).ShouldBe(4);

        TotalDoEscopo(await ConsultarAsync(consulta, $"tipo=7&{periodo}")).ShouldBe(4, "os tipos 7 e 8 contam a mesma população");
    }

    [Fact]
    public async Task Escopo_SomaDasUnidadesEAberturaPorUnidade_UnidadeForaDoEscopoInvisivel()
    {
        var u1 = await _op.CriarAsync();
        var u2 = await _op.CriarAsync();
        using var dasDuas = await ConsultorAsync(u1.UnidadeId, u2.UnidadeId);
        using var soU1 = await ConsultorAsync(u1.UnidadeId);
        const string hora = "2026-09-20 13:15:00+00";
        await FichaEmAsync(u1, 1, CanalEmissao.Totem, u1.Totem, hora);
        await FichaEmAsync(u2, 1, CanalEmissao.Totem, u2.Totem, hora);
        await FichaEmAsync(u2, 2, CanalEmissao.Guiche, u2.GuicheA, hora);
        const string periodo = "dataInicial=2026-09-20&dataFinal=2026-09-20";

        var soma = await ConsultarAsync(dasDuas, $"tipo=7&{periodo}");
        soma.Linhas.Where(l => l.UnidadeId == u1.UnidadeId).Select(Rotulo).ShouldBe(["10h=1"], "13:15Z é 10h em São Paulo");
        soma.Linhas.ShouldAllBe(l => l.UnidadeId != null, "toda linha abre por unidade; a soma do escopo é o total");
        soma.Totais[^1].UnidadeId.ShouldBeNull("o total do escopo vem por último");
        TotalDoEscopo(soma).ShouldBe(3);
        TotalDaUnidade(soma, u1.UnidadeId).ShouldBe(1);
        TotalDaUnidade(soma, u2.UnidadeId).ShouldBe(2);
        soma.Linhas.Where(l => l.UnidadeId == u2.UnidadeId).Select(Rotulo).ShouldBe(["10h=2"]);

        var porGuiche = await ConsultarAsync(dasDuas, $"tipo=8&{periodo}");
        TotalDoEscopo(porGuiche).ShouldBe(3);
        porGuiche.Linhas.Where(l => l.UnidadeId == u2.UnidadeId).Select(Rotulo)
            .ShouldBe([$"Guichê 1|{FichasEmitidasPorGuiche.CanalGuiche}=1", $"Totem|{FichasEmitidasPorGuiche.CanalTotem}=1"]);

        var soUmaPedida = await ConsultarAsync(dasDuas, $"tipo=7&{periodo}&unidades={u2.UnidadeId}");
        TotalDoEscopo(soUmaPedida).ShouldBe(2);
        soUmaPedida.Linhas.ShouldNotContain(l => l.UnidadeId == u1.UnidadeId);

        foreach (var tipo in new[] { 7, 8 })
        {
            var restrito = await ConsultarAsync(soU1, $"tipo={tipo}&{periodo}");
            TotalDoEscopo(restrito).ShouldBe(1, $"tipo {tipo}: U2 fora do escopo não entra na soma");
            restrito.Linhas.ShouldNotContain(l => l.UnidadeId == u2.UnidadeId);
            restrito.Totais.ShouldNotContain(l => l.UnidadeId == u2.UnidadeId);
        }
    }

    private async Task<HttpClient> ConsultorAsync(params Guid[] unidades)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidades);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Estatisticas.View);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    /// <summary>Ficha of Caixa/Normal whose creation instant is moved to <paramref name="instanteUtc"/>; the data de operação follows (generated column).</summary>
    private async Task FichaEmAsync(CenarioOperacao c, int numero, CanalEmissao canal, Guid estacaoId, string instanteUtc)
    {
        var fichaId = await FichaHojeAsync(c, numero, canal, estacaoId);
        var instante = DateTimeOffset.Parse(instanteUtc, CultureInfo.InvariantCulture);
        (await _tenant.ExecutarAsync($"""UPDATE paf."Fichas" SET "CriadoEm" = {instante} WHERE "Id" = {fichaId}""")).ShouldBe(1);
    }

    private async Task<Guid> FichaHojeAsync(CenarioOperacao c, int numero, CanalEmissao canal, Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, c.Caixa, c.Normal, "A", numero, canal, estacaoId, Guid.NewGuid().ToString("N"));
        db.Fichas.Add(ficha);
        await db.SaveChangesAsync();
        return ficha.Id;
    }

    /// <summary>The ficha's own day, read from the database: the test never derives "today" from the process clock.</summary>
    private async Task<string> PeriodoDaFichaAsync(CenarioOperacao c, Guid fichaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var dia = await db.Fichas.AsNoTracking().Where(f => f.Id == fichaId).Select(f => f.DataOperacional).SingleAsync();
        return $"dataInicial={dia:yyyy-MM-dd}&dataFinal={dia:yyyy-MM-dd}";
    }

    private static async Task<ResultadoEstatisticaDto> ConsultarAsync(HttpClient client, string consulta)
    {
        using var resposta = await client.GetAsync($"{PafApi.Base}/estatistica?{consulta}");
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, $"{consulta}: {await resposta.Content.ReadAsStringAsync()}");
        return await resposta.DeserializeAsync<ResultadoEstatisticaDto>();
    }

    /// <summary>The row without its leading unidade name: the other labels, then the count.</summary>
    private static string Rotulo(LinhaEstatisticaDto linha) =>
        string.Join('|', linha.Celulas.Skip(1).Where(c => !string.IsNullOrEmpty(c.Texto)).Select(c => c.Texto))
        + "=" + linha.Celulas[^1].Numero!.Value.ToString(CultureInfo.InvariantCulture);

    private static string[] Corpo(ResultadoEstatisticaDto resultado) => [.. resultado.Linhas.Select(Rotulo)];

    private static decimal TotalDoEscopo(ResultadoEstatisticaDto resultado) =>
        resultado.Totais.Single(l => l.UnidadeId is null).Celulas[^1].Numero!.Value;

    private static decimal TotalDaUnidade(ResultadoEstatisticaDto resultado, Guid unidadeId) =>
        resultado.Totais.Single(l => l.UnidadeId == unidadeId).Celulas[^1].Numero!.Value;
}
