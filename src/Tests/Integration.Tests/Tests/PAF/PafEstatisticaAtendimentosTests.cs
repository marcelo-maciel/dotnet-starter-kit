using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Estatistica;
using FSH.Modules.PAF.Features.v1.Estatistica.Atendimentos;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// E1, RF07 types 1 to 6 over real PostgreSQL: the population (encerrados on the atendimento's own data de
/// operação, inclusive), weighted totals, the São Paulo hour of type 5 and the two counts of type 6. Instants are
/// written by raw UPDATE because production stamps them with now(); March has no DST in Brazil, so SP = UTC-3.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstatisticaAtendimentosTests(FshWebApplicationFactory factory)
{
    private static readonly DateOnly Dia = new(2026, 3, 10);

    [Fact]
    public async Task TemposPorFila_TotaisPonderados_SoEncerrados_FiltroDePrioridadeParticionaOTotal()
    {
        var c = await MontarAsync("paf-e1-tempos");
        var a = await FilaAsync(c, c.U1, "Fila A");
        var b = await FilaAsync(c, c.U1, "Fila B");
        var preferencial = await PrioridadeAsync(c, c.U1.UnidadeId, "Preferencial");
        var d = await FilaAsync(c, c.U2, "Fila D");

        // Fila A: three realised, 2 min wait and 10 min service each; the third was recalled 20 min after the call.
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 9, 0, 0), esperaSegundos: 120, atendimentoSegundos: 600);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 9, 30, 0), esperaSegundos: 120, atendimentoSegundos: 600);
        var rechamado = await RealizadoAsync(c, a, preferencial, c.U1.GuicheA, null, Sp(Dia, 10, 0, 0), esperaSegundos: 120, atendimentoSegundos: 600);
        await c.Cliente.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "UltimaRechamadaEm" = "ChamadoEm" + interval '20 minutes' WHERE "Id" = {rechamado}
            """);

        // Fila B: one realised, 10 min wait and 5 min service. Mean of the fila means would be 6 min; weighted is 4.
        await RealizadoAsync(c, b, b.Prioridade, c.U1.GuicheB, null, Sp(Dia, 11, 0, 0), esperaSegundos: 600, atendimentoSegundos: 300);

        // Outside every mean: cancelled after start, waiting, called, in service.
        var criado = Sp(Dia, 12, 0, 0);
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(criado, EstadoAtendimento.Cancelado)
        {
            Chamado = criado.AddMinutes(30), Iniciado = criado.AddMinutes(31), Cancelado = criado.AddMinutes(40), Estacao = c.U1.GuicheA,
        });
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(criado, EstadoAtendimento.Aguardando));
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(criado, EstadoAtendimento.Chamado) { Chamado = criado.AddMinutes(50), Estacao = c.U1.GuicheB });
        await AtendimentoAsync(c, b, b.Prioridade, new Marcos(criado, EstadoAtendimento.EmAtendimento)
        {
            Chamado = criado.AddMinutes(50), Iniciado = criado.AddMinutes(51), Estacao = c.U1.GuicheA,
        });

        // U2, in the same scope: 1 min wait, 1 min service.
        await RealizadoAsync(c, d, d.Prioridade, c.U2.GuicheA, null, Sp(Dia, 9, 0, 0), esperaSegundos: 60, atendimentoSegundos: 60);

        var espera = await ConsultarAsync(c, $"tipo=1&{Periodo(Dia, Dia)}");
        espera.Colunas.Select(x => x.Chave).ShouldBe(["unidade", "fila", "tempoMedioEspera", "tempoMedioAtendimento", "totalAtendimentos"]);
        var linhasU1 = espera.Linhas.Where(l => l.UnidadeId == c.U1.UnidadeId).ToList();
        linhasU1.Select(l => l.Celulas[1].Texto).ShouldBe(["Fila B", "Fila A"]);
        Numeros(linhasU1[0]).ShouldBe([600m, 300m, 1m]);
        Numeros(linhasU1[1]).ShouldBe([120m, 600m, 3m]);

        var subtotalU1 = espera.Totais.Single(t => t.UnidadeId == c.U1.UnidadeId);
        Numeros(subtotalU1).ShouldBe([240m, 525m, 4m], "weighted: (3 x 120 + 600) / 4 = 240 s, never (120 + 600) / 2");
        var total = espera.Totais.Single(t => t.UnidadeId is null);
        Numeros(total).ShouldBe([204m, 432m, 5m]);

        var atendimento = await ConsultarAsync(c, $"tipo=2&{Periodo(Dia, Dia)}");
        atendimento.Linhas.Where(l => l.UnidadeId == c.U1.UnidadeId).Select(l => l.Celulas[1].Texto).ShouldBe(["Fila A", "Fila B"]);
        Numeros(atendimento.Totais.Single(t => t.UnidadeId is null)).ShouldBe([204m, 432m, 5m]);

        // The prioridade filter restricts the population, and the filtered totals partition the unfiltered one.
        var soPreferencial = await ConsultarAsync(c, $"tipo=1&{Periodo(Dia, Dia)}&prioridades={preferencial}");
        Numeros(soPreferencial.Totais.Single(t => t.UnidadeId is null)).ShouldBe([120m, 600m, 1m]);
        var parciais = 0m;
        foreach (var prioridade in new[] { a.Prioridade, b.Prioridade, preferencial, d.Prioridade }.Distinct())
        {
            var parcial = await ConsultarAsync(c, $"tipo=1&{Periodo(Dia, Dia)}&prioridades={prioridade}");
            parciais += Numeros(parcial.Totais.Single(t => t.UnidadeId is null))[2]!.Value;
        }

        parciais.ShouldBe(5m);
    }

    [Fact]
    public async Task Periodo_InclusivoNaDataDeOperacaoDoAtendimento_VazioSemDivisaoPorZero()
    {
        var c = await MontarAsync("paf-e1-periodo");
        var a = await FilaAsync(c, c.U1, "Fila A");

        // Created 23:59:58 on Dia, called and closed after midnight: counts on Dia, never on the next day.
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 23, 59, 58), esperaSegundos: 12, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 0, 0, 0), esperaSegundos: 60, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia.AddDays(-1), 23, 59, 59), esperaSegundos: 60, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia.AddDays(1), 0, 0, 0), esperaSegundos: 60, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia.AddDays(1), 23, 59, 59), esperaSegundos: 60, atendimentoSegundos: 60);

        Numeros(TotalGeral(await ConsultarAsync(c, $"tipo=1&{Periodo(Dia, Dia)}")))[2].ShouldBe(2m);
        Numeros(TotalGeral(await ConsultarAsync(c, $"tipo=1&{Periodo(Dia.AddDays(1), Dia.AddDays(1))}")))[2].ShouldBe(2m);
        Numeros(TotalGeral(await ConsultarAsync(c, $"tipo=1&{Periodo(Dia.AddDays(-1), Dia.AddDays(1))}")))[2].ShouldBe(5m);

        var vazio = await ConsultarAsync(c, $"tipo=1&{Periodo(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2))}");
        vazio.Linhas.ShouldBeEmpty();
        Numeros(vazio.Totais.ShouldHaveSingleItem()).ShouldBe([null, null, 0m]);
        (await ConsultarAsync(c, $"tipo=6&{Periodo(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2))}")).Linhas.ShouldBeEmpty();
    }

    [Fact]
    public async Task PorGuiche_EstacoesVaziasSoSemOFiltro_EstacaoDesativadaAparece()
    {
        var c = await MontarAsync("paf-e1-guiche");
        var a = await FilaAsync(c, c.U1, "Fila A");
        var desativada = await EstacaoAsync(c, c.U1.UnidadeId, "Guichê 3");

        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 9, 0, 0), esperaSegundos: 60, atendimentoSegundos: 120);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 9, 10, 0), esperaSegundos: 180, atendimentoSegundos: 120);
        await RealizadoAsync(c, a, a.Prioridade, desativada, null, Sp(Dia, 9, 20, 0), esperaSegundos: 60, atendimentoSegundos: 60);
        await c.Cliente.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Ativa" = false WHERE "Id" = {desativada}""");

        var todas = await ConsultarAsync(c, $"tipo=3&{Periodo(Dia, Dia)}&unidades={c.U1.UnidadeId}");
        todas.Linhas.Select(l => (l.Celulas[1].Texto, Numeros(l)[2])).ShouldBe([("Guichê 1", 2m), ("Guichê 2", 0m), ("Guichê 3", 1m)]);
        Numeros(todas.Linhas[0]).ShouldBe([120m, 120m, 2m]);
        Numeros(todas.Linhas[1]).ShouldBe([null, null, 0m]);
        Numeros(TotalGeral(todas)).ShouldBe([100m, 100m, 3m]);

        var comAtendimento = await ConsultarAsync(c, $"tipo=3&{Periodo(Dia, Dia)}&unidades={c.U1.UnidadeId}&somenteEstacoesComAtendimento=true");
        comAtendimento.Linhas.Select(l => l.Celulas[1].Texto).ShouldBe(["Guichê 1", "Guichê 3"]);
        Numeros(TotalGeral(comAtendimento)).ShouldBe([100m, 100m, 3m]);
    }

    [Fact]
    public async Task PorAtendente_NomesPeloContratoDoIdentity_RemovidoESemAtendenteTemLinha()
    {
        var c = await MontarAsync("paf-e1-atendente");
        var a = await FilaAsync(c, c.U1, "Fila A");
        var outro = await c.Cliente.CriarUsuarioAsync(c.U1.UnidadeId);
        var usuario = Guid.Parse(c.Usuario.UserId);

        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, usuario, Sp(Dia, 9, 0, 0), esperaSegundos: 60, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, usuario, Sp(Dia, 9, 5, 0), esperaSegundos: 60, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheB, Guid.Parse(outro.UserId), Sp(Dia, 9, 10, 0), esperaSegundos: 60, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheB, Guid.NewGuid(), Sp(Dia, 9, 15, 0), esperaSegundos: 60, atendimentoSegundos: 60);
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheB, null, Sp(Dia, 9, 20, 0), esperaSegundos: 60, atendimentoSegundos: 60);

        // In process: through HTTP a client role cannot hold ViewPorAtendente until D10 flips its IsRoot flag (E0 test).
        using var scope = factory.Services.CreateScope();
        c.Cliente.Contexto(scope, unidadeCorrente: null);
        var consulta = scope.ServiceProvider.GetServices<IConsultaEstatistica>().Single(x => x.Tipo == TipoEstatistica.AtendimentosPorAtendente);
        var resultado = await consulta.ConsultarAsync(Filtro(TipoEstatistica.AtendimentosPorAtendente, c, false), CancellationToken.None);

        resultado.Linhas.Select(l => (l.Celulas[1].Texto, Numeros(l)[2])).OrderBy(x => x.Texto).ThenBy(x => x.Item2).ShouldBe(
        [
            ("Seeded User", 1m),
            ("Seeded User", 2m),
            (ConsultaAtendimentosPorAtendente.RotuloSemAtendente, 1m),
            (ConsultaAtendimentosPorAtendente.RotuloUsuarioRemovido, 1m),
        ]);
        resultado.Linhas.SelectMany(l => l.Celulas).ShouldNotContain(x => x.Texto == c.Usuario.UserId);
        Numeros(TotalGeral(resultado))[2].ShouldBe(5m);
    }

    [Fact]
    public async Task HorarioDePico_HoraDoInicioEmSaoPaulo_CanceladoFora_E_NaoEfetuadosOuCancelados()
    {
        var c = await MontarAsync("paf-e1-hora");
        var a = await FilaAsync(c, c.U1, "Fila A");

        // Started at 02:30Z of the next UTC day: 23:30 in São Paulo, hour 23 of Dia.
        var tarde = Sp(Dia, 22, 0, 0);
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(tarde, EstadoAtendimento.Encerrado)
        {
            Chamado = new DateTimeOffset(2026, 3, 11, 2, 29, 0, TimeSpan.Zero),
            Iniciado = new DateTimeOffset(2026, 3, 11, 2, 30, 0, TimeSpan.Zero),
            Encerrado = new DateTimeOffset(2026, 3, 11, 2, 40, 0, TimeSpan.Zero),
            Estacao = c.U1.GuicheA,
        });
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 8, 0, 0), esperaSegundos: 900, atendimentoSegundos: 60);
        var manha = Sp(Dia, 9, 0, 0);
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(manha, EstadoAtendimento.Cancelado)
        {
            Chamado = manha.AddMinutes(1), Iniciado = manha.AddMinutes(2), Cancelado = manha.AddMinutes(3), Estacao = c.U1.GuicheA,
        });
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(manha, EstadoAtendimento.Cancelado) { Chamado = manha.AddMinutes(5), Cancelado = manha.AddMinutes(9) });
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(manha, EstadoAtendimento.EmAtendimento)
        {
            Chamado = manha.AddMinutes(59), Iniciado = manha.AddMinutes(61), Estacao = c.U1.GuicheB,
        });
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(manha, EstadoAtendimento.Aguardando));
        await AtendimentoAsync(c, a, a.Prioridade, new Marcos(manha, EstadoAtendimento.Chamado) { Chamado = manha.AddMinutes(70), Estacao = c.U1.GuicheA });

        var preferencial = await PrioridadeAsync(c, c.U1.UnidadeId, "Preferencial");
        await RealizadoAsync(c, a, preferencial, c.U1.GuicheA, null, Sp(Dia, 14, 0, 0), esperaSegundos: 60, atendimentoSegundos: 60);

        var pico = await ConsultarAsync(c, $"tipo=5&{Periodo(Dia, Dia)}");
        pico.Linhas.Select(l => (l.Celulas[1].Texto, l.Celulas[2].Numero)).ShouldBe([("08h", 1m), ("10h", 1m), ("14h", 1m), ("23h", 1m)]);
        TotalGeral(pico).Celulas[2].Numero.ShouldBe(4m);

        // Type 5 is raw SQL: the fila and prioridade filters are written by hand there, so they are pinned here.
        var soPreferencial = await ConsultarAsync(c, $"tipo=5&{Periodo(Dia, Dia)}&prioridades={preferencial}");
        soPreferencial.Linhas.Select(l => l.Celulas[1].Texto).ShouldBe(["14h"]);
        (await ConsultarAsync(c, $"tipo=5&{Periodo(Dia, Dia)}&filas={a.Fila}")).Linhas.Count.ShouldBe(4);
        (await ConsultarAsync(c, $"tipo=5&{Periodo(Dia.AddDays(1), Dia.AddDays(1))}")).Linhas.ShouldBeEmpty();

        var naoEfetuados = await ConsultarAsync(c, $"tipo=6&{Periodo(Dia, Dia)}");
        naoEfetuados.Colunas.Select(x => x.Chave).ShouldBe(["unidade", "fila", "naoEfetuados", "cancelados"]);
        var linha = naoEfetuados.Linhas.ShouldHaveSingleItem();
        linha.Celulas[2].Numero.ShouldBe(2m);
        linha.Celulas[3].Numero.ShouldBe(2m);
    }

    [Fact]
    public async Task Consulta_NaoBloqueiaComForUpdateAbertoEmOutraConexao()
    {
        var c = await MontarAsync("paf-e1-lock");
        var a = await FilaAsync(c, c.U1, "Fila A");
        await RealizadoAsync(c, a, a.Prioridade, c.U1.GuicheA, null, Sp(Dia, 9, 0, 0), esperaSegundos: 60, atendimentoSegundos: 60);

        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: c.U1.UnidadeId);
        await using var transacao = await db.Database.BeginTransactionAsync();
        var travados = await db.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM paf."Atendimentos" WHERE "UnidadeId" = {c.U1.UnidadeId} FOR UPDATE
            """).ToListAsync();
        travados.ShouldHaveSingleItem();

        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var resposta = await c.ComView.GetAsync(Rota($"tipo=1&{Periodo(Dia, Dia)}"), prazo.Token);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        Numeros(TotalGeral(await resposta.DeserializeAsync<ResultadoEstatisticaDto>()))[2].ShouldBe(1m);
        await transacao.RollbackAsync();
    }

    private sealed record Cenario(PafTenantCliente Cliente, UnidadeTeste U1, UnidadeTeste U2, Integration.Tests.Tests.Sessions.SeededUser Usuario, HttpClient ComView);

    private sealed record FilaTeste(Guid UnidadeId, Guid Fila, Guid Prioridade, Guid Emissora);

    private sealed record Marcos(DateTimeOffset Criado, EstadoAtendimento Estado)
    {
        public DateTimeOffset? Chamado { get; init; }

        public DateTimeOffset? Iniciado { get; init; }

        public DateTimeOffset? Encerrado { get; init; }

        public DateTimeOffset? Cancelado { get; init; }

        public Guid? Estacao { get; init; }

        public Guid? Usuario { get; init; }
    }

    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var cliente = new PafTenantCliente(factory, prefixo);
        await cliente.InicializarAsync();
        var u1 = await cliente.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await cliente.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await cliente.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        await cliente.DarPapelAsync(usuario.UserId, PafPermissions.Estatisticas.View);
        var token = await cliente.EntrarAsync(usuario);
        return new Cenario(cliente, u1, u2, usuario, cliente.Cliente(token.AccessToken));
    }

    private async Task<FilaTeste> FilaAsync(Cenario c, UnidadeTeste unidade, string nome)
    {
        var unidadeId = unidade.UnidadeId;
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var fila = Fila.Criar(unidadeId, nome, nome, setorId: null);
        var prioridade = Prioridade.Criar(unidadeId, "Normal", valor: 0);
        db.Filas.Add(fila);
        db.Prioridades.Add(prioridade);
        await db.SaveChangesAsync();
        return new FilaTeste(unidadeId, fila.Id, prioridade.Id, unidade.GuicheA);
    }

    private async Task<Guid> PrioridadeAsync(Cenario c, Guid unidadeId, string nome)
    {
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var prioridade = Prioridade.Criar(unidadeId, nome, valor: 1);
        db.Prioridades.Add(prioridade);
        await db.SaveChangesAsync();
        return prioridade.Id;
    }

    private async Task<Guid> EstacaoAsync(Cenario c, Guid unidadeId, string nome)
    {
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: unidadeId);
        var estacao = Estacao.Criar(unidadeId, nome, TipoEstacao.Guiche);
        db.Estacoes.Add(estacao);
        await db.SaveChangesAsync();
        return estacao.Id;
    }

    /// <summary>Encerrado: eligible at creation, called after the wait, started one second later, closed after the service.</summary>
    private Task<Guid> RealizadoAsync(
        Cenario c, FilaTeste fila, Guid prioridade, Guid estacao, Guid? usuario, DateTimeOffset criado, int esperaSegundos, int atendimentoSegundos)
    {
        var chamado = criado.AddSeconds(esperaSegundos);
        var iniciado = chamado.AddSeconds(1);
        return AtendimentoAsync(c, fila, prioridade, new Marcos(criado, EstadoAtendimento.Encerrado)
        {
            Chamado = chamado, Iniciado = iniciado, Encerrado = iniciado.AddSeconds(atendimentoSegundos), Estacao = estacao, Usuario = usuario,
        });
    }

    private async Task<Guid> AtendimentoAsync(Cenario c, FilaTeste fila, Guid prioridade, Marcos marcos)
    {
        using var scope = factory.Services.CreateScope();
        var db = c.Cliente.Contexto(scope, unidadeCorrente: fila.UnidadeId);
        var numero = Interlocked.Increment(ref _numero);
        var ficha = Ficha.Criar(fila.UnidadeId, fila.Fila, prioridade, "A", numero, CanalEmissao.Totem, fila.Emissora, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(fila.UnidadeId, ficha.Id, fila.Fila, prioridade, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();

        var estado = marcos.Estado.ToString();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Fichas" SET "CriadoEm" = {marcos.Criado} WHERE "Id" = {ficha.Id}
            """);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = {estado}, "CriadoEm" = {marcos.Criado}, "ElegivelDesde" = {marcos.Criado},
                   "ChamadoEm" = {marcos.Chamado}, "IniciadoEm" = {marcos.Iniciado}, "EncerradoEm" = {marcos.Encerrado},
                   "CanceladoEm" = {marcos.Cancelado}, "EstacaoId" = {marcos.Estacao}, "UsuarioId" = {marcos.Usuario}
             WHERE "Id" = {atendimento.Id}
            """);
        return atendimento.Id;
    }

    private static int _numero;

    private static FiltroEstatistica Filtro(TipoEstatistica tipo, Cenario c, bool somenteComAtendimento) =>
        new(tipo, Dia, Dia, null, [c.U1.UnidadeId, c.U2.UnidadeId], [], [], somenteComAtendimento);

    // Npgsql writes only UTC offsets to timestamptz.
    private static DateTimeOffset Sp(DateOnly dia, int hora, int minuto, int segundo) =>
        new DateTimeOffset(dia.Year, dia.Month, dia.Day, hora, minuto, segundo, TimeSpan.FromHours(-3)).ToUniversalTime();

    private static string Periodo(DateOnly inicial, DateOnly final) =>
        $"dataInicial={inicial:yyyy-MM-dd}&dataFinal={final:yyyy-MM-dd}";

    private static string Rota(string consulta) => $"{PafApi.Base}/estatistica?{consulta}";

    private static decimal?[] Numeros(LinhaEstatisticaDto linha) => [.. linha.Celulas.Skip(2).Select(x => x.Numero)];

    private static LinhaEstatisticaDto TotalGeral(ResultadoEstatisticaDto resultado) => resultado.Totais.Single(t => t.UnidadeId is null);

    private static async Task<ResultadoEstatisticaDto> ConsultarAsync(Cenario c, string consulta)
    {
        using var resposta = await c.ComView.GetAsync(Rota(consulta));
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return await resposta.DeserializeAsync<ResultadoEstatisticaDto>();
    }
}
