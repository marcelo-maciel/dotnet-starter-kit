using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// M05 against real PostgreSQL: the RF10 programação cadastro through its endpoints, the search filters at their
/// limits, D13 (midnight and overlap) and the single "on air now" read with the database's São Paulo clock. Root
/// operator with the unit header, so each test owns fresh unidades; mídias are inserted straight in the database.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafProgramacoesMidiaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Rota = "/programacoes-midia";
    private static readonly DateOnly Novembro1 = new(2026, 11, 1);
    private static readonly DateOnly Novembro30 = new(2026, 11, 30);

    private readonly PafApi _api = new(factory);
    private readonly PafBanco _banco = new(factory);

    public Task InitializeAsync() => _banco.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CriarEditar_TemposCalculados_HoraFinalAcompanhaAEdicaoDaDuracaoDaMidia()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var midia = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 900);

        var criada = await CriarAsync(root, unidade, Comando(midia, "  Abertura  ", new TimeOnly(10, 0), 2, DiasSemana.Segunda | DiasSemana.Quarta));
        (criada.Nome, criada.Ativa, criada.Versao, criada.UnidadeId, criada.Dias).ShouldBe(("Abertura", true, 0, unidade, 2 | 8));
        (criada.TempoMidiaSegundos, criada.TempoTotalSegundos, criada.HoraFinalSegundos).ShouldBe((900, 1800L, 37_800L), "10:00 + 2 x 15 min = 10:30");

        // The mídia's duration is edited by M03's endpoint: the end moves without rewriting the programação.
        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Put, $"/midias/{midia}", unidade, new UpdateMidiaCommand(midia, "Vídeo", 1200, 0));
        var lida = (await PesquisarAsync(root, unidade, "")).Items.ShouldHaveSingleItem();
        (lida.Versao, lida.TempoMidiaSegundos, lida.TempoTotalSegundos, lida.HoraFinalSegundos).ShouldBe((0, 1200, 2400L, 38_400L));

        var editada = await OkAsync(root, HttpMethod.Put, $"{Rota}/{criada.Id}", unidade, Edicao(criada.Id, midia, "Abertura", new TimeOnly(11, 0), 3, DiasSemana.Todos, 0));
        (editada.Versao, editada.HoraInicial, editada.Repeticoes, editada.Dias, editada.HoraFinalSegundos).ShouldBe((1, new TimeOnly(11, 0), 3, 127, 39_600L + 3600));

        using var obsoleta = await PafApi.EnviarAsync(root, HttpMethod.Put, $"{Rota}/{criada.Id}", unidade, Edicao(criada.Id, midia, "Velha", new TimeOnly(9, 0), 1, DiasSemana.Todos, 0));
        obsoleta.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var desativada = await OkAsync(root, HttpMethod.Post, $"{Rota}/{criada.Id}/disable", unidade, null);
        (desativada.Ativa, desativada.Versao).ShouldBe((false, 2));
        var ativada = await OkAsync(root, HttpMethod.Post, $"{Rota}/{criada.Id}/enable", unidade, null);
        (ativada.Ativa, ativada.Versao).ShouldBe((true, 3));
    }

    [Fact]
    public async Task Pesquisa_FiltrosNosLimites_HoraFinalFora_DiasPeriodoNomeEPaginacao()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var midia = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 900);
        var manha = await CriarAsync(root, unidade, new CreateProgramacaoMidiaCommand(
            midia, "Abertura Manhã", Novembro1, Novembro30, new TimeOnly(10, 0), 2, (int)(DiasSemana.Segunda | DiasSemana.Quarta)));
        var tarde = await CriarAsync(root, unidade, new CreateProgramacaoMidiaCommand(
            midia, "Fechamento 50%_off", new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31), new TimeOnly(18, 0), 1, (int)DiasSemana.Todos));

        async Task<Guid[]> Ids(string filtro) => (await PesquisarAsync(root, unidade, filtro)).Items.Select(p => p.Id).ToArray();

        (await Ids("")).ShouldBe([manha.Id, tarde.Id], "ordered by name");
        (await Ids("hora=10:00:00")).ShouldBe([manha.Id]);
        (await Ids("hora=10:29:59")).ShouldBe([manha.Id]);
        (await Ids("hora=10:30:00")).ShouldBeEmpty("10:30 is the computed end, outside");
        (await Ids("hora=09:59:59")).ShouldBeEmpty();
        (await Ids("hora=18:14:59")).ShouldBe([tarde.Id]);

        (await Ids("nome=ABERTURA%20MAN")).ShouldBe([manha.Id], "case-insensitive contains");
        (await Ids("nome=50%25_")).ShouldBe([tarde.Id], "% and _ are literal");
        (await Ids("nome=%25")).ShouldBe([tarde.Id]);
        (await Ids("nome=a_e")).ShouldBeEmpty("_ is not a wildcard");

        (await Ids($"dias={(int)DiasSemana.Domingo}")).ShouldBe([tarde.Id], "todos os dias includes Sunday, segunda/quarta does not");
        (await Ids($"dias={(int)DiasSemana.Segunda}")).ShouldBe([manha.Id, tarde.Id]);
        (await Ids($"dias={(int)(DiasSemana.Sexta | DiasSemana.Sabado)}")).ShouldBe([tarde.Id]);

        (await Ids("de=2026-11-30&ate=2026-11-30")).ShouldBe([manha.Id], "inclusive end of the period");
        (await Ids("de=2026-12-01")).ShouldBe([tarde.Id]);
        (await Ids("ate=2026-11-01")).ShouldBe([manha.Id], "inclusive start of the period");
        (await Ids("ate=2026-10-31")).ShouldBeEmpty();
        (await Ids($"hora=18:00:00&dias={(int)DiasSemana.Domingo}&de=2026-12-25&nome=fecha")).ShouldBe([tarde.Id]);

        var pagina1 = await PesquisarAsync(root, unidade, "pageSize=1&pageNumber=1");
        var pagina2 = await PesquisarAsync(root, unidade, "pageSize=1&pageNumber=2");
        (pagina1.TotalCount, pagina1.TotalPages, pagina1.Items.Single().Id, pagina2.Items.Single().Id).ShouldBe((2L, 2, manha.Id, tarde.Id));

        foreach (var invalido in new[] { "dias=0", "dias=128", "de=2026-11-02&ate=2026-11-01" })
        {
            using var resposta = await PafApi.EnviarAsync(root, HttpMethod.Get, $"{Rota}?{invalido}", unidade, null);
            resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, invalido);
        }
    }

    [Fact]
    public async Task MidiaInativaNaoConfirmadaOuDeOutraUnidade_400_SemGravar()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var outraUnidade = await _api.CriarUnidadeAsync(root);
        var boa = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 60);
        var inativa = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 60);
        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{inativa}/disable", unidade, null);
        var naoConfirmada = await MidiaSemConfirmacaoAsync(unidade);
        var deOutraUnidade = await MidiaConfirmadaAsync(outraUnidade, duracaoSegundos: 60);
        var programacao = await CriarAsync(root, unidade, Comando(boa, "Base", new TimeOnly(8, 0), 1, DiasSemana.Todos));

        foreach (var (midia, caso) in new[] { (inativa, "inativa"), (naoConfirmada, "não confirmada"), (deOutraUnidade, "de outra unidade"), (Guid.CreateVersion7(), "inexistente") })
        {
            using var criar = await PafApi.EnviarAsync(root, HttpMethod.Post, Rota, unidade, Comando(midia, "Recusada", new TimeOnly(9, 0), 1, DiasSemana.Todos));
            criar.StatusCode.ShouldBe(HttpStatusCode.BadRequest, caso);
            using var editar = await PafApi.EnviarAsync(root, HttpMethod.Put, $"{Rota}/{programacao.Id}", unidade, Edicao(programacao.Id, midia, "Recusada", new TimeOnly(9, 0), 1, DiasSemana.Todos, 0));
            editar.StatusCode.ShouldBe(HttpStatusCode.BadRequest, caso);
        }

        (await PesquisarAsync(root, unidade, "")).Items.ShouldBe([programacao], "nothing written");

        // A programação whose mídia was disabled cannot be switched back on, but can always be switched off.
        await OkAsync(root, HttpMethod.Post, $"{Rota}/{programacao.Id}/disable", unidade, null);
        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{boa}/disable", unidade, null);
        using var reativar = await PafApi.EnviarAsync(root, HttpMethod.Post, $"{Rota}/{programacao.Id}/enable", unidade, null);
        reativar.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task D13_HoraFinalDepoisDaMeiaNoite_400NaCriacaoEdicaoEReativacao_MeiaNoiteExataAceita()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var hora = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 3600);
        var umSegundoAMais = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 3601);

        var exata = await CriarAsync(root, unidade, Comando(hora, "Até meia-noite", new TimeOnly(23, 0), 1, DiasSemana.Todos));
        exata.HoraFinalSegundos.ShouldBe(AgendaMidia.SegundosNoDia);

        using (var passa = await PafApi.EnviarAsync(root, HttpMethod.Post, Rota, unidade, Comando(umSegundoAMais, "Passa", new TimeOnly(23, 0), 1, DiasSemana.Todos)))
        {
            passa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await passa.Content.ReadAsStringAsync()).ShouldContain("meia-noite");
        }

        using (var repeticoes = await PafApi.EnviarAsync(root, HttpMethod.Post, Rota, unidade, Comando(hora, "Passa", new TimeOnly(22, 0), 3, DiasSemana.Todos)))
        {
            repeticoes.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var editar = await PafApi.EnviarAsync(root, HttpMethod.Put, $"{Rota}/{exata.Id}", unidade, Edicao(exata.Id, hora, "Passa", new TimeOnly(23, 0, 1), 1, DiasSemana.Todos, 0)))
        {
            editar.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await PesquisarAsync(root, unidade, "")).Items.ShouldBe([exata], "the refused edit left it as it was");

        // A later duration edit of the mídia pushes it past midnight: switching it back on is refused.
        await OkAsync(root, HttpMethod.Post, $"{Rota}/{exata.Id}/disable", unidade, null);
        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Put, $"/midias/{hora}", unidade, new UpdateMidiaCommand(hora, "Vídeo", 7200, 0));
        using var reativar = await PafApi.EnviarAsync(root, HttpMethod.Post, $"{Rota}/{exata.Id}/enable", unidade, null);
        reativar.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NoArAgora_PeloRelogioDoBanco_ExcluirSome_MidiaDesativadaSome_SobreposicaoVenceAMaisRecente()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var hoje = await HojeNoBancoAsync(unidade);
        var diaInteiro = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 86_400);
        var outraDiaInteiro = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 86_400);
        var dia = new CreateProgramacaoMidiaCommand(diaInteiro, "O dia todo", hoje.AddDays(-1), hoje.AddDays(1), TimeOnly.MinValue, 1, (int)DiasSemana.Todos);
        var programacao = await CriarAsync(root, unidade, dia);
        var deAmanha = await CriarAsync(root, unidade, dia with { Nome = "Amanhã", DataInicial = hoje.AddDays(2), DataFinal = hoje.AddDays(3) });

        var agenda = await AgendaAsync(unidade);
        agenda.Programacoes.Select(p => p.Id).ShouldBe([programacao.Id], "only today's period");
        agenda.NoAr!.Id.ShouldBe(programacao.Id);
        DateOnly.FromDateTime(agenda.AgoraSaoPaulo).ShouldBe(hoje);

        // Same start: the newest wins (D13 tie), and disabling its mídia takes it off the air.
        var mesmaHora = await CriarAsync(root, unidade, dia with { MidiaId = outraDiaInteiro, Nome = "Mais nova" });
        (await AgendaAsync(unidade)).NoAr!.Id.ShouldBe(mesmaHora.Id);
        await PafMidiaCadastroTests.MidiaOkAsync(root, HttpMethod.Post, $"/midias/{outraDiaInteiro}/disable", unidade, null);
        (await AgendaAsync(unidade)).NoAr!.Id.ShouldBe(programacao.Id);

        await OkAsync(root, HttpMethod.Post, $"{Rota}/{programacao.Id}/disable", unidade, null);
        (await AgendaAsync(unidade)).NoAr.ShouldBeNull("a disabled programação is off the air");
        await OkAsync(root, HttpMethod.Post, $"{Rota}/{programacao.Id}/enable", unidade, null);

        using (var excluir = await PafApi.EnviarAsync(root, HttpMethod.Delete, $"{Rota}/{programacao.Id}", unidade, null))
        {
            excluir.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AgendaAsync(unidade)).NoAr.ShouldBeNull("deleted, gone from the painel's read");
        (await PesquisarAsync(root, unidade, "")).Items.Select(p => p.Id).ShouldBe([deAmanha.Id, mesmaHora.Id]);
        using var deNovo = await PafApi.EnviarAsync(root, HttpMethod.Delete, $"{Rota}/{programacao.Id}", unidade, null);
        deNovo.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ProgramacaoDas2130Brt_NoArAs0030Utc_PelaConversaoDoPostgreSql()
    {
        var unidade = Guid.CreateVersion7();
        using var scope = factory.Services.CreateScope();
        var db = _banco.Contexto(scope, unidade);
        var agora = (await db.Database
            .SqlQuery<DateTime>($"SELECT (timestamptz '2026-10-20 00:30:00+00' AT TIME ZONE 'America/Sao_Paulo') AS \"Value\"")
            .ToListAsync()).Single();
        var criadoEm = DateTimeOffset.UtcNow;
        var segunda = new ProgramacaoDoDia(Guid.CreateVersion7(), Guid.CreateVersion7(), "Noite", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31),
            new TimeOnly(21, 30), 1, DiasSemana.Segunda, 1800, criadoEm);
        var terca = segunda with { Id = Guid.CreateVersion7(), HoraInicial = TimeOnly.MinValue, Dias = DiasSemana.Terca, TempoMidiaSegundos = 3600 };

        agora.ShouldBe(new DateTime(2026, 10, 19, 21, 30, 0, DateTimeKind.Unspecified), "Tuesday 00:30 UTC is Monday 21:30 in São Paulo");
        AgendaMidia.NoAr([segunda, terca], agora).ShouldBe(segunda);
    }

    [Fact]
    public async Task DuasEdicoesComAMesmaVersao_Uma200_Outra409()
    {
        using var root = await _api.RootAsync();
        var unidade = await _api.CriarUnidadeAsync(root);
        var midia = await MidiaConfirmadaAsync(unidade, duracaoSegundos: 60);
        var programacao = await CriarAsync(root, unidade, Comando(midia, "Base", new TimeOnly(8, 0), 1, DiasSemana.Todos));

        var respostas = await Task.WhenAll(Enumerable.Range(1, 2).Select(async i =>
        {
            using var resposta = await PafApi.EnviarAsync(root, HttpMethod.Put, $"{Rota}/{programacao.Id}", unidade,
                Edicao(programacao.Id, midia, $"Edição {i}", new TimeOnly(8, i), 1, DiasSemana.Todos, 0));
            return resposta.StatusCode;
        }));

        respostas.Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await PesquisarAsync(root, unidade, "")).Items.Single().Versao.ShouldBe(1);
    }

    [Fact]
    public async Task OutraUnidade_404EmTodaEscrita_EPesquisaVazia()
    {
        using var root = await _api.RootAsync();
        var unidadeA = await _api.CriarUnidadeAsync(root);
        var unidadeB = await _api.CriarUnidadeAsync(root);
        var midia = await MidiaConfirmadaAsync(unidadeA, duracaoSegundos: 60);
        var programacao = await CriarAsync(root, unidadeA, Comando(midia, "Base", new TimeOnly(8, 0), 1, DiasSemana.Todos));

        (HttpMethod Metodo, string Rota, object? Corpo)[] escritas =
        [
            (HttpMethod.Put, $"{Rota}/{programacao.Id}", Edicao(programacao.Id, midia, "Roubada", new TimeOnly(9, 0), 1, DiasSemana.Todos, 0)),
            (HttpMethod.Post, $"{Rota}/{programacao.Id}/enable", null),
            (HttpMethod.Post, $"{Rota}/{programacao.Id}/disable", null),
            (HttpMethod.Delete, $"{Rota}/{programacao.Id}", null),
        ];
        foreach (var (metodo, rota, corpo) in escritas)
        {
            using var resposta = await PafApi.EnviarAsync(root, metodo, rota, unidadeB, corpo);
            resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{metodo} {rota}");
        }

        (await PesquisarAsync(root, unidadeB, "")).Items.ShouldBeEmpty();
        (await PesquisarAsync(root, unidadeA, "")).Items.ShouldBe([programacao], "nothing written from the other unidade");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    internal static CreateProgramacaoMidiaCommand Comando(Guid midia, string nome, TimeOnly inicio, int repeticoes, DiasSemana dias) =>
        new(midia, nome, Novembro1, Novembro30, inicio, repeticoes, (int)dias);

    internal static UpdateProgramacaoMidiaCommand Edicao(Guid id, Guid midia, string nome, TimeOnly inicio, int repeticoes, DiasSemana dias, int versao) =>
        new(id, midia, nome, Novembro1, Novembro30, inicio, repeticoes, (int)dias, versao);

    internal static Task<ProgramacaoMidiaDto> CriarAsync(HttpClient client, Guid? unidade, CreateProgramacaoMidiaCommand comando) =>
        OkAsync(client, HttpMethod.Post, Rota, unidade, comando);

    internal static Task<ProgramacaoMidiaDto> OkAsync(HttpClient client, HttpMethod metodo, string rota, Guid? unidade, object? corpo) =>
        PafApi.OkAsync<ProgramacaoMidiaDto>(client, metodo, rota, unidade, corpo);

    internal static Task<PagedResponse<ProgramacaoMidiaDto>> PesquisarAsync(HttpClient client, Guid? unidade, string filtro) =>
        PafApi.OkAsync<PagedResponse<ProgramacaoMidiaDto>>(client, HttpMethod.Get, $"{Rota}?{filtro}", unidade, null);

    private Task<Guid> MidiaConfirmadaAsync(Guid unidade, int duracaoSegundos) =>
        InserirMidiaConfirmadaAsync(factory, _banco.Contexto, unidade, duracaoSegundos);

    private Task<Guid> MidiaSemConfirmacaoAsync(Guid unidade) =>
        InserirMidiaAsync(factory, _banco.Contexto, unidade, duracaoSegundos: 60);

    /// <summary>
    /// An envio and its mídia written straight in the database (the upload path is M03's and is proven there), the envio
    /// left unconfirmed.
    /// </summary>
    internal static async Task<Guid> InserirMidiaAsync(
        FshWebApplicationFactory factory, Func<IServiceScope, Guid?, PafDbContext> contexto, Guid unidade, int duracaoSegundos)
    {
        using var scope = factory.Services.CreateScope();
        var db = contexto(scope, unidade);
        var envio = EnvioMidia.Criar(Guid.CreateVersion7(), unidade, $"paf-midia/teste/{unidade:N}/{Guid.CreateVersion7():N}.mp4", TipoMidia.Video, "video/mp4");
        var midia = Midia.Criar(envio, "Vídeo", duracaoSegundos);
        db.EnviosMidia.Add(envio);
        db.Midias.Add(midia);
        await db.SaveChangesAsync();
        return midia.Id;
    }

    /// <summary>Same, then the envio confirmed by the same raw <c>now()</c> UPDATE the confirmation uses.</summary>
    internal static async Task<Guid> InserirMidiaConfirmadaAsync(
        FshWebApplicationFactory factory, Func<IServiceScope, Guid?, PafDbContext> contexto, Guid unidade, int duracaoSegundos)
    {
        var midiaId = await InserirMidiaAsync(factory, contexto, unidade, duracaoSegundos);
        using var scope = factory.Services.CreateScope();
        var db = contexto(scope, unidade);
        (await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."EnviosMidia" e SET "ConfirmadoEm" = now()
              FROM paf."Midias" m WHERE m."Id" = {midiaId} AND e."Id" = m."EnvioId"
            """)).ShouldBe(1);
        return midiaId;
    }

    private async Task<DateOnly> HojeNoBancoAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        return await PafRelogio.HojeAsync(_banco.Contexto(scope, unidade), CancellationToken.None);
    }

    private async Task<AgendaDeHoje> AgendaAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        return await AgendaMidia.DeHojeAsync(_banco.Contexto(scope, unidade), CancellationToken.None);
    }
}

/// <summary>M05 in a client tenant: permission (403), the root tenant's rows (404 / 400) and the impersonation acts (D28).</summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafProgramacoesMidiaTenantTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Rota = "/programacoes-midia";
    private readonly PafTenantCliente _tenant = new(factory, "paf-m05");
    private readonly PafBanco _banco = new(factory);

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        await _banco.InicializarAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SoComView_403NasEscritas_ProgramacaoEMidiaDoTenantRaiz404E400()
    {
        var api = new PafApi(factory);
        using var root = await api.RootAsync();
        var unidadeRaiz = await api.CriarUnidadeAsync(root);
        var midiaRaiz = await PafProgramacoesMidiaTests.InserirMidiaConfirmadaAsync(factory, _banco.Contexto, unidadeRaiz, 60);
        var programacaoRaiz = await PafProgramacoesMidiaTests.CriarAsync(root, unidadeRaiz,
            PafProgramacoesMidiaTests.Comando(midiaRaiz, "Raiz", new TimeOnly(8, 0), 1, DiasSemana.Todos));

        var leitor = await _tenant.CriarUsuarioAsync(_tenant.UnidadeInicial);
        await _tenant.DarPapelAsync(leitor.UserId, PafPermissions.Programacoes.View);
        using var soLeitura = _tenant.Cliente((await _tenant.EntrarAsync(leitor)).AccessToken);

        (await PafProgramacoesMidiaTests.PesquisarAsync(soLeitura, null, "")).Items.ShouldBeEmpty();
        foreach (var (metodo, rota, corpo) in Escritas(programacaoRaiz.Id, midiaRaiz))
        {
            using var resposta = await PafApi.EnviarAsync(soLeitura, metodo, rota, null, corpo);
            resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{metodo} {rota}");
        }

        // The tenant admin holds every Programacoes permission, and still never reaches the root tenant's rows.
        foreach (var (metodo, rota, corpo) in Escritas(programacaoRaiz.Id, midiaRaiz))
        {
            using var resposta = await PafApi.EnviarAsync(_tenant.Admin, metodo, rota, null, corpo);
            var esperado = rota == Rota ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound;
            resposta.StatusCode.ShouldBe(esperado, $"{metodo} {rota}");
        }

        (await PafProgramacoesMidiaTests.PesquisarAsync(root, unidadeRaiz, "")).Items.ShouldBe([programacaoRaiz]);
    }

    [Fact]
    public async Task CodefiImpersonando_CadaEscritaGravaUmAto()
    {
        var unidade = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var alvo = await _tenant.CriarUsuarioAsync(unidade.UnidadeId);
        await _tenant.DarPapelAsync(alvo.UserId,
            PafPermissions.Programacoes.View, PafPermissions.Programacoes.Create, PafPermissions.Programacoes.Update, PafPermissions.Programacoes.Delete);
        var midia = await PafProgramacoesMidiaTests.InserirMidiaConfirmadaAsync(factory, _tenant.Contexto, unidade.UnidadeId, 60);
        using var root = await new AuthHelper(factory).CreateRootAdminClientAsync();
        var token = await IniciarImpersonacaoAsync(root, alvo.UserId);
        var concessao = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Id);
        using var suporte = _tenant.Cliente(token);

        var criada = await PafProgramacoesMidiaTests.CriarAsync(suporte, null,
            PafProgramacoesMidiaTests.Comando(midia, "Suporte", new TimeOnly(8, 0), 1, DiasSemana.Todos));
        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldBe(1);
        await PafProgramacoesMidiaTests.OkAsync(suporte, HttpMethod.Put, $"{Rota}/{criada.Id}", null,
            PafProgramacoesMidiaTests.Edicao(criada.Id, midia, "Suporte 2", new TimeOnly(9, 0), 1, DiasSemana.Todos, 0));
        await PafProgramacoesMidiaTests.OkAsync(suporte, HttpMethod.Post, $"{Rota}/{criada.Id}/disable", null, null);
        await PafProgramacoesMidiaTests.OkAsync(suporte, HttpMethod.Post, $"{Rota}/{criada.Id}/enable", null, null);
        using (var excluir = await PafApi.EnviarAsync(suporte, HttpMethod.Delete, $"{Rota}/{criada.Id}", null, null))
        {
            excluir.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldBe(5, "one act per write: create, edit, disable, enable, delete");

        using var recusada = await PafApi.EnviarAsync(suporte, HttpMethod.Post, Rota, null,
            PafProgramacoesMidiaTests.Comando(midia, "Passa", new TimeOnly(23, 59, 30), 1, DiasSemana.Todos));
        recusada.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AtosAsync(unidade.UnidadeId, concessao)).ShouldBe(5, "a refused write records no act");
    }

    private async Task<int> AtosAsync(Guid unidade, Guid concessao)
    {
        using var scope = factory.Services.CreateScope();
        return await _tenant.Contexto(scope, unidade).AtosImpersonacao.CountAsync(a => a.ConcessaoId == concessao);
    }

    private static (HttpMethod Metodo, string Rota, object? Corpo)[] Escritas(Guid programacaoId, Guid midiaId) =>
    [
        (HttpMethod.Post, Rota, PafProgramacoesMidiaTests.Comando(midiaId, "Roubada", new TimeOnly(9, 0), 1, DiasSemana.Todos)),
        (HttpMethod.Put, $"{Rota}/{programacaoId}", PafProgramacoesMidiaTests.Edicao(programacaoId, midiaId, "Roubada", new TimeOnly(9, 0), 1, DiasSemana.Todos, 0)),
        (HttpMethod.Post, $"{Rota}/{programacaoId}/enable", null),
        (HttpMethod.Post, $"{Rota}/{programacaoId}/disable", null),
        (HttpMethod.Delete, $"{Rota}/{programacaoId}", null),
    ];

    private async Task<string> IniciarImpersonacaoAsync(HttpClient ator, string alvo)
    {
        using var resposta = await ator.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/impersonation/start", new
        {
            targetUserId = alvo,
            targetTenantId = _tenant.TenantId,
            reason = "teste da programação de mídia",
        });
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
