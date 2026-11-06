using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Estatistica;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// E3, RF07 types 9 and 10 over real PostgreSQL: grouping by fila or by the fila's current setor, the "Sem setor"
/// group per unidade, percentages computed from sums (closing 100% within rounding) and the D07 columns.
/// Atendimento states and instants are written by raw UPDATE, as in the E1 tests; March has no DST in Brazil.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstatisticaTotaisTests(FshWebApplicationFactory factory)
{
    private static readonly DateOnly Dia = new(2026, 3, 10);

    // Each percentage is rounded to 2 decimals, so a column of them may sum to 99.99 or 100.01.
    private const decimal Tolerancia = 0.05m;

    [Fact]
    public async Task Tipo10_PorSetor_SemSetorEhGrupo_PercentuaisFecham100_MoverFilaMudaOPassado()
    {
        var c = await MontarAsync("paf-e3-setor");
        var setor = await SetorAsync(c, c.U1.UnidadeId, "Atendimento");
        var normal = await PrioridadeAsync(c, c.U1.UnidadeId, "Normal", 0);
        var prioritario = await PrioridadeAsync(c, c.U1.UnidadeId, "Prioritário", 1);
        var a = await FilaAsync(c, c.U1.UnidadeId, "Fila A", setor);
        var b = await FilaAsync(c, c.U1.UnidadeId, "Fila B", setor);
        var semSetor = await FilaAsync(c, c.U1.UnidadeId, "Fila C", null);

        await AtendimentoAsync(c, c.U1.UnidadeId, a, normal, EstadoAtendimento.Encerrado);
        await AtendimentoAsync(c, c.U1.UnidadeId, a, prioritario, EstadoAtendimento.Encerrado);
        await AtendimentoAsync(c, c.U1.UnidadeId, a, normal, EstadoAtendimento.EmAtendimento);
        await AtendimentoAsync(c, c.U1.UnidadeId, b, normal, EstadoAtendimento.Aguardando);
        await AtendimentoAsync(c, c.U1.UnidadeId, b, normal, EstadoAtendimento.Chamado);
        await AtendimentoAsync(c, c.U1.UnidadeId, b, normal, EstadoAtendimento.Cancelado);
        await AtendimentoAsync(c, c.U1.UnidadeId, semSetor, normal, EstadoAtendimento.Encerrado);
        await AtendimentoAsync(c, c.U1.UnidadeId, semSetor, prioritario, EstadoAtendimento.Cancelado);

        // Another day: never in the population.
        await AtendimentoAsync(c, c.U1.UnidadeId, semSetor, normal, EstadoAtendimento.Encerrado, Dia.AddDays(1));

        var porSetor = await ConsultarAsync(c, $"tipo=10&{Periodo(Dia, Dia)}&agrupamento=Setor");
        porSetor.Colunas.Select(x => x.Chave).ShouldBe(
        [
            "unidade", "setor", "atendidos", "atendidosPercentual", "emAtendimento", "emAtendimentoPercentual",
            "naoEfetuados", "naoEfetuadosPercentual", "cancelados", "canceladosPercentual", "totalAtendimentos", "totalAtendimentosPercentual",
        ]);
        porSetor.Linhas.Select(l => l.Celulas[1].Texto).ShouldBe(["Atendimento", TotaisAgrupados.RotuloSemSetor]);
        porSetor.Linhas.ShouldAllBe(l => l.UnidadeId == c.U1.UnidadeId);
        Numeros(porSetor.Linhas[0]).ShouldBe([2m, 25m, 1m, 12.5m, 2m, 25m, 1m, 12.5m, 6m, 75m]);
        Numeros(porSetor.Linhas[1]).ShouldBe([1m, 12.5m, 0m, 0m, 0m, 0m, 1m, 12.5m, 2m, 25m]);
        Numeros(TotalGeral(porSetor)).ShouldBe([3m, 37.5m, 1m, 12.5m, 2m, 25m, 2m, 25m, 8m, 100m]);
        FechaCem(porSetor.Linhas.Select(l => Numeros(l)[9]));

        var porFila = await ConsultarAsync(c, $"tipo=10&{Periodo(Dia, Dia)}&agrupamento=Fila");
        porFila.Colunas[1].Chave.ShouldBe("fila");
        porFila.Linhas.Select(l => (l.Celulas[1].Texto, Numeros(l)[8])).ShouldBe([("Fila A", 3m), ("Fila B", 3m), ("Fila C", 2m)]);
        FechaCem(porFila.Linhas.Select(l => Numeros(l)[9]));
        Numeros(TotalGeral(porFila)).ShouldBe(Numeros(TotalGeral(porSetor)));

        // The prioridade filter narrows the population, and the percentages follow the narrowed total.
        var soPrioritario = await ConsultarAsync(c, $"tipo=10&{Periodo(Dia, Dia)}&agrupamento=Setor&prioridades={prioritario}");
        Numeros(TotalGeral(soPrioritario)).ShouldBe([1m, 50m, 0m, 0m, 0m, 0m, 1m, 50m, 2m, 100m]);

        // The setor is read from the fila today, so moving Fila C regroups the past and "Sem setor" disappears.
        await c.Cliente.ExecutarAsync($"""UPDATE paf."Filas" SET "SetorId" = {setor} WHERE "Id" = {semSetor}""");
        var depois = await ConsultarAsync(c, $"tipo=10&{Periodo(Dia, Dia)}&agrupamento=Setor");
        var unica = depois.Linhas.ShouldHaveSingleItem();
        unica.Celulas[1].Texto.ShouldBe("Atendimento");
        Numeros(unica).ShouldBe([3m, 37.5m, 1m, 12.5m, 2m, 25m, 2m, 25m, 8m, 100m]);
    }

    [Fact]
    public async Task Tipo9_PrioridadesComoColunas_SoEncerrados_PercentualDaLinhaEDoTotal_SubtotalPelaSoma()
    {
        var c = await MontarAsync("paf-e3-tipo9");
        var normal = await PrioridadeAsync(c, c.U1.UnidadeId, "Normal", 0);
        var prioritario = await PrioridadeAsync(c, c.U1.UnidadeId, "Prioritário", 1);
        await PrioridadeAsync(c, c.U1.UnidadeId, "Prioritário 80+", 2);
        var normalU2 = await PrioridadeAsync(c, c.U2.UnidadeId, "Normal", 5);
        var a = await FilaAsync(c, c.U1.UnidadeId, "Fila A", null);
        var b = await FilaAsync(c, c.U1.UnidadeId, "Fila B", null);
        var d = await FilaAsync(c, c.U2.UnidadeId, "Fila D", null);

        await AtendimentoAsync(c, c.U1.UnidadeId, a, prioritario, EstadoAtendimento.Encerrado);
        await AtendimentoAsync(c, c.U1.UnidadeId, b, normal, EstadoAtendimento.Encerrado);
        await AtendimentoAsync(c, c.U1.UnidadeId, b, normal, EstadoAtendimento.Encerrado);
        await AtendimentoAsync(c, c.U1.UnidadeId, b, normal, EstadoAtendimento.Encerrado);
        await AtendimentoAsync(c, c.U1.UnidadeId, a, prioritario, EstadoAtendimento.EmAtendimento);
        await AtendimentoAsync(c, c.U1.UnidadeId, a, normal, EstadoAtendimento.Cancelado);
        await AtendimentoAsync(c, c.U1.UnidadeId, b, normal, EstadoAtendimento.Aguardando);
        await AtendimentoAsync(c, c.U2.UnidadeId, d, normalU2, EstadoAtendimento.Encerrado);

        var porFila = await ConsultarAsync(c, $"tipo=9&{Periodo(Dia, Dia)}&agrupamento=Fila");

        // One column pair per prioridade name, by precedence: "Normal" of U1 and of U2 is one column, ranked by the
        // highest value of that name (5 in U2); a prioridade with no atendimento still shows its zeros.
        porFila.Colunas.Select(x => x.Titulo).ShouldBe(["Unidade", "Fila", "Normal", "%", "Prioritário 80+", "%", "Prioritário", "%", "Total", "%"]);
        var u1 = porFila.Linhas.Where(l => l.UnidadeId == c.U1.UnidadeId).ToList();
        u1.Select(l => l.Celulas[1].Texto).ShouldBe(["Fila A", "Fila B"]);
        Numeros(u1[0]).ShouldBe([0m, 0m, 0m, 0m, 1m, 100m, 1m, 20m]);
        Numeros(u1[1]).ShouldBe([3m, 100m, 0m, 0m, 0m, 0m, 3m, 60m]);
        Numeros(porFila.Linhas.Single(l => l.UnidadeId == c.U2.UnidadeId)).ShouldBe([1m, 100m, 0m, 0m, 0m, 0m, 1m, 20m]);

        // Subtotal from the sums: Prioritário is 1 of 4 = 25%, never the mean of the rows' 100% and 0% (50%).
        Numeros(porFila.Totais.Single(t => t.UnidadeId == c.U1.UnidadeId)).ShouldBe([3m, 75m, 0m, 0m, 1m, 25m, 4m, 80m]);
        Numeros(TotalGeral(porFila)).ShouldBe([4m, 80m, 0m, 0m, 1m, 20m, 5m, 100m]);
        FechaCem(porFila.Linhas.Select(l => Numeros(l)[7]));

        // A client without setores: grouping by setor gives one "Sem setor" group per unidade.
        var soU1 = await ConsultarAsync(c, $"tipo=9&{Periodo(Dia, Dia)}&agrupamento=Setor&unidades={c.U1.UnidadeId}");
        var semSetor = soU1.Linhas.ShouldHaveSingleItem();
        semSetor.Celulas[1].Texto.ShouldBe(TotaisAgrupados.RotuloSemSetor);
        // Only U1's cadastro now: its "Normal" (value 0) ranks last.
        soU1.Colunas.Select(x => x.Titulo).ShouldBe(["Unidade", "Setor", "Prioritário 80+", "%", "Prioritário", "%", "Normal", "%", "Total", "%"]);
        Numeros(semSetor).ShouldBe([0m, 0m, 1m, 25m, 3m, 75m, 4m, 100m]);
    }

    [Fact]
    public async Task DuasUnidadesSemSetor_DoisGrupos_PercentuaisSobreOTotalDoEscopo()
    {
        var c = await MontarAsync("paf-e3-unidades");
        var p1 = await PrioridadeAsync(c, c.U1.UnidadeId, "Normal", 0);
        var p2 = await PrioridadeAsync(c, c.U2.UnidadeId, "Normal", 0);
        var f1 = await FilaAsync(c, c.U1.UnidadeId, "Caixa", null);
        var f2 = await FilaAsync(c, c.U2.UnidadeId, "Caixa", null);

        await AtendimentoAsync(c, c.U1.UnidadeId, f1, p1, EstadoAtendimento.Encerrado);
        for (var i = 0; i < 2; i++)
        {
            await AtendimentoAsync(c, c.U2.UnidadeId, f2, p2, EstadoAtendimento.Encerrado);
        }

        await AtendimentoAsync(c, c.U2.UnidadeId, f2, p2, EstadoAtendimento.Aguardando);

        var tipo10 = await ConsultarAsync(c, $"tipo=10&{Periodo(Dia, Dia)}&agrupamento=Setor");
        tipo10.Linhas.Count.ShouldBe(2);
        tipo10.Linhas.ShouldAllBe(l => l.Celulas[1].Texto == TotaisAgrupados.RotuloSemSetor);
        tipo10.Linhas.Select(l => l.UnidadeId).ShouldBe([c.U1.UnidadeId, c.U2.UnidadeId], ignoreOrder: true);
        Numeros(tipo10.Linhas.Single(l => l.UnidadeId == c.U1.UnidadeId)).ShouldBe([1m, 25m, 0m, 0m, 0m, 0m, 0m, 0m, 1m, 25m]);
        Numeros(tipo10.Linhas.Single(l => l.UnidadeId == c.U2.UnidadeId)).ShouldBe([2m, 50m, 0m, 0m, 1m, 25m, 0m, 0m, 3m, 75m]);
        tipo10.Totais.Count.ShouldBe(3);
        Numeros(tipo10.Totais.Single(t => t.UnidadeId == c.U2.UnidadeId))[9].ShouldBe(75m);
        Numeros(TotalGeral(tipo10))[8..].ShouldBe([4m, 100m]);

        var tipo9 = await ConsultarAsync(c, $"tipo=9&{Periodo(Dia, Dia)}&agrupamento=Setor");
        tipo9.Linhas.Count.ShouldBe(2);
        tipo9.Linhas.ShouldAllBe(l => l.Celulas[1].Texto == TotaisAgrupados.RotuloSemSetor);
    }

    [Fact]
    public async Task PeriodoVazio_TotalZeradoComPercentuaisNulos()
    {
        var c = await MontarAsync("paf-e3-vazio");
        foreach (var consulta in new[] { "tipo=9&agrupamento=Fila", "tipo=9&agrupamento=Setor", "tipo=10&agrupamento=Fila", "tipo=10&agrupamento=Setor" })
        {
            var vazio = await ConsultarAsync(c, $"{consulta}&{Periodo(Dia, Dia)}");
            vazio.Linhas.ShouldBeEmpty();
            var total = vazio.Totais.ShouldHaveSingleItem();
            total.UnidadeId.ShouldBeNull();
            var numeros = Numeros(total);
            numeros.Where((_, i) => i % 2 == 0).ShouldAllBe(n => n == 0m, consulta);
            numeros.Where((_, i) => i % 2 == 1).ShouldAllBe(n => n == null, consulta);
        }
    }

    [Fact]
    public async Task AgrupamentoEmOutroTipo_Recusado400()
    {
        var c = await MontarAsync("paf-e3-400");
        foreach (var tipo in Enumerable.Range(1, 8))
        {
            using var resposta = await c.ComView.GetAsync(Rota($"tipo={tipo}&{Periodo(Dia, Dia)}&agrupamento=Setor"));
            resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"tipo {tipo}: {await resposta.Content.ReadAsStringAsync()}");
        }
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
        var token = await cliente.EntrarAsync(usuario);
        return new Cenario(cliente, u1, u2, cliente.Cliente(token.AccessToken));
    }

    private async Task<Guid> SetorAsync(Cenario c, Guid unidadeId, string nome)
    {
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var setor = Setor.Criar(unidadeId, nome);
        db.Setores.Add(setor);
        await db.SaveChangesAsync();
        return setor.Id;
    }

    private async Task<Guid> FilaAsync(Cenario c, Guid unidadeId, string nome, Guid? setorId)
    {
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var fila = Fila.Criar(unidadeId, nome, nome, setorId);
        db.Filas.Add(fila);
        await db.SaveChangesAsync();
        return fila.Id;
    }

    private async Task<Guid> PrioridadeAsync(Cenario c, Guid unidadeId, string nome, int valor)
    {
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var prioridade = Prioridade.Criar(unidadeId, nome, valor);
        db.Prioridades.Add(prioridade);
        await db.SaveChangesAsync();
        return prioridade.Id;
    }

    private Task AtendimentoAsync(Cenario c, Guid unidadeId, Guid fila, Guid prioridade, EstadoAtendimento estado) =>
        AtendimentoAsync(c, unidadeId, fila, prioridade, estado, Dia);

    /// <summary>Only state and data de operação matter to types 9 and 10; the instants are filled to stay coherent.</summary>
    private async Task AtendimentoAsync(Cenario c, Guid unidadeId, Guid fila, Guid prioridade, EstadoAtendimento estado, DateOnly dia)
    {
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var numero = Interlocked.Increment(ref _numero);
        var ficha = Ficha.Criar(unidadeId, fila, prioridade, "A", numero, CanalEmissao.Totem, unidadeId == c.U1.UnidadeId ? c.U1.GuicheA : c.U2.GuicheA, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(unidadeId, ficha.Id, fila, prioridade, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();

        var criado = new DateTimeOffset(dia.Year, dia.Month, dia.Day, 12, 0, 0, TimeSpan.FromHours(-3)).ToUniversalTime();
        DateTimeOffset? chamado = estado is EstadoAtendimento.Aguardando ? null : criado.AddMinutes(5);
        DateTimeOffset? iniciado = estado is EstadoAtendimento.EmAtendimento or EstadoAtendimento.Encerrado or EstadoAtendimento.Cancelado ? criado.AddMinutes(6) : null;
        DateTimeOffset? encerrado = estado is EstadoAtendimento.Encerrado ? criado.AddMinutes(10) : null;
        DateTimeOffset? cancelado = estado is EstadoAtendimento.Cancelado ? criado.AddMinutes(10) : null;
        var nomeEstado = estado.ToString();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = {nomeEstado}, "CriadoEm" = {criado}, "ElegivelDesde" = {criado},
                   "ChamadoEm" = {chamado}, "IniciadoEm" = {iniciado}, "EncerradoEm" = {encerrado}, "CanceladoEm" = {cancelado}
             WHERE "Id" = {atendimento.Id}
            """);
    }

    private static int _numero;

    private static string Periodo(DateOnly inicial, DateOnly final) =>
        $"dataInicial={inicial:yyyy-MM-dd}&dataFinal={final:yyyy-MM-dd}";

    private static string Rota(string consulta) => $"{PafApi.Base}/estatistica?{consulta}";

    private static decimal?[] Numeros(LinhaEstatisticaDto linha) => [.. linha.Celulas.Skip(2).Select(x => x.Numero)];

    private static LinhaEstatisticaDto TotalGeral(ResultadoEstatisticaDto resultado) => resultado.Totais.Single(t => t.UnidadeId is null);

    private static void FechaCem(IEnumerable<decimal?> percentuais) =>
        Math.Abs(percentuais.Sum(p => p!.Value) - 100m).ShouldBeLessThanOrEqualTo(Tolerancia);

    private static async Task<ResultadoEstatisticaDto> ConsultarAsync(Cenario c, string consulta)
    {
        using var resposta = await c.ComView.GetAsync(Rota(consulta));
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return await resposta.DeserializeAsync<ResultadoEstatisticaDto>();
    }
}
