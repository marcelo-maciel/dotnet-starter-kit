using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;
using Integration.Tests.Infrastructure;
using Mediator;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// Coverage by guichê and the fila window on the totem buttons and on the emission (RF02 decisions of 27/08 and
/// 03/10/2026, RF04, RF14). Scenario of <see cref="PafOperacao"/>: Caixa (A, AP) is covered by guichês A and B,
/// Protocolo (B) by guichê B, Retaguarda (R) by none.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafCoberturaGuicheTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string MensagemSemCobertura = "Nenhum guichê atende esta fila nesta prioridade.";
    private const string RotaConfiguracao = "/api/v1/paf/estacao/emissao/configuracao";
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);

    private readonly PafTenantCliente _tenant = new(factory, "paf-cob");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task GuicheSoComNormal_TotemMostraNormal_EPreferencialDa409()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);

        var semPreferencialNoA = await EnviarAsync(c, new UpdateEstacaoCommand(c.GuicheA, "Guichê 1",
            [new EstacaoFilaItem("Guiche", c.Caixa, 0)], [new EstacaoPrioridadeItem(c.Normal, 0)]));
        semPreferencialNoA.CombinacoesSemCobertura.ShouldBeEmpty("o guichê B ainda cobre Caixa/Preferencial");

        var semPreferencialNoB = await EnviarAsync(c, new UpdateEstacaoCommand(c.GuicheB, "Guichê 2",
            [new EstacaoFilaItem("Guiche", c.Protocolo, 0), new EstacaoFilaItem("Guiche", c.Caixa, 1)],
            [new EstacaoPrioridadeItem(c.Normal, 0)]));
        var perdida = semPreferencialNoB.CombinacoesSemCobertura.ShouldHaveSingleItem();
        (perdida.FilaId, perdida.PrioridadeId, perdida.Letra, perdida.Fila, perdida.Prioridade)
            .ShouldBe((c.Caixa, c.Preferencial, "AP", "Caixa", "Preferencial"));

        var botoes = await BotoesAsync(totem);
        botoes.ShouldBe([(c.Caixa, c.Normal), (c.Protocolo, c.Normal)]);

        using var recusa = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Preferencial, Guid.NewGuid().ToString("N"));
        recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await recusa.Content.ReadAsStringAsync()).ShouldContain(MensagemSemCobertura);
        (await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal)).Senha.ShouldBe("A1");
        (await _op.ContarAsync(c, db => db.Fichas.Where(f => f.PrioridadeId == c.Preferencial))).ShouldBe(0);

        var filas = await EnviarAsync(c, new ListFilasQuery());
        var caixa = filas.Single(f => f.Id == c.Caixa);
        caixa.CombinacoesSemCobertura.ShouldBe([caixa.Prioridades.Single(p => p.PrioridadeId == c.Preferencial).Id]);
        filas.Single(f => f.Id == c.Protocolo).CombinacoesSemCobertura.ShouldBeEmpty();
        filas.Single(f => f.Id == c.Retaguarda).CombinacoesSemCobertura.Count.ShouldBe(1, "Retaguarda não está em guichê nenhum");
    }

    [Fact]
    public async Task FilaSoNoPapelEmissaoDeEstacaoCombinada_NaoCobre()
    {
        var c = await _op.CriarAsync();
        // Retaguarda goes into the emission list of an "Emissão de Fichas e Guichê" station, never its guichê list.
        var combinada = await EnviarAsync(c, new CreateEstacaoCommand("Totem e guichê", "EmissaoGuiche",
            [new EstacaoFilaItem("Emissao", c.Retaguarda, 0), new EstacaoFilaItem("Emissao", c.Caixa, 1), new EstacaoFilaItem("Guiche", c.Protocolo, 0)],
            [new EstacaoPrioridadeItem(c.Normal, 0)]));
        using var totem = _tenant.Cliente(await _op.CredencialAsync(c.UnidadeId, combinada, "Emissao"));

        (await BotoesAsync(totem)).ShouldBe([(c.Caixa, c.Normal), (c.Caixa, c.Preferencial)]);
        using var recusa = await PafOperacao.EmitirAsync(totem, c.Retaguarda, c.Normal, Guid.NewGuid().ToString("N"));
        recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await recusa.Content.ReadAsStringAsync()).ShouldContain(MensagemSemCobertura);

        var retaguarda = (await EnviarAsync(c, new ListFilasQuery())).Single(f => f.Id == c.Retaguarda);
        retaguarda.CombinacoesSemCobertura.ShouldBe([retaguarda.Prioridades.Single().Id]);
    }

    [Fact]
    public async Task GuicheInativoNaoCobre_SemNinguemLogadoCobre_EACoberturaVoltaAoReativar()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        (await _op.ContarAsync(c, db => db.SessoesEstacao)).ShouldBe(0, "nenhum atendente entrou: a cobertura é do cadastro");
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        (await EnviarAsync(c, new DisableEstacaoCommand(c.GuicheA))).CombinacoesSemCobertura.ShouldBeEmpty();
        var semGuiche = await EnviarAsync(c, new DisableEstacaoCommand(c.GuicheB));
        semGuiche.CombinacoesSemCobertura.Select(x => x.Letra).Order().ShouldBe(["A", "AP", "B"]);

        (await BotoesAsync(totem)).ShouldBeEmpty();
        using var recusa = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
        recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await recusa.Content.ReadAsStringAsync()).ShouldContain(MensagemSemCobertura);

        await EnviarAsync(c, new EnableEstacaoCommand(c.GuicheA));
        (await BotoesAsync(totem)).ShouldBe([(c.Caixa, c.Normal), (c.Caixa, c.Preferencial)]);
        (await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal)).Senha.ShouldBe("A2");
    }

    [Fact]
    public async Task CombinacaoQuePerdeCobertura_MantemFichasAguardando_EVoltaASerChamadaComGuiche()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);

        await EnviarAsync(c, new DisableEstacaoCommand(c.GuicheA));
        (await EnviarAsync(c, new DisableEstacaoCommand(c.GuicheB))).CombinacoesSemCobertura.ShouldContain(x => x.Letra == "AP");
        (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Aguardando);

        await EnviarAsync(c, new EnableEstacaoCommand(c.GuicheA));
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(ficha.AtendimentoId);
    }

    [Fact]
    public async Task ForaDaJanela_BotaoSome_EmissaoDa409_EFichaEmitidaAntesSegueChamavel()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        // A one-hour window in the half of the day away from now, by the database clock: never crosses midnight.
        var agora = await AgoraEmSaoPauloAsync();
        var inicio = agora < new TimeOnly(12, 0) ? new TimeOnly(13, 0) : new TimeOnly(1, 0);
        await EnviarAsync(c, new UpdateFilaCommand(c.Caixa, "Caixa", "Caixa", inicio, inicio.AddHours(1)));

        (await BotoesAsync(totem)).ShouldBe([(c.Protocolo, c.Normal)]);
        using var recusa = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
        recusa.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await recusa.Content.ReadAsStringAsync()).ShouldContain(EmitirFichaCommandHandler.MensagemForaDoHorario);

        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(ficha.AtendimentoId);

        // The window containing now brings the button back.
        var aberta = new TimeOnly(agora.Hour, 0);
        await EnviarAsync(c, new UpdateFilaCommand(c.Caixa, "Caixa", "Caixa", aberta, aberta.Hour == 23 ? new TimeOnly(23, 59, 59) : aberta.AddHours(1)));
        (await BotoesAsync(totem)).ShouldContain((c.Caixa, c.Normal));
    }

    [Fact]
    public async Task UnidadeInativa_SemBotoes()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        (await BotoesAsync(totem)).ShouldNotBeEmpty();

        // Straight in the database: the station credential keeps working, only the buttons must react.
        await _tenant.ExecutarAsync($"""UPDATE paf."Unidades" SET "Ativa" = false WHERE "Id" = {c.UnidadeId}""");
        (await BotoesAsync(totem)).ShouldBeEmpty();
    }

    /// <summary>
    /// An emission whose coverage check overlaps the station write that removes the last guichê. The test holds the
    /// write's lock and commits the removal itself, so the emission is forced to check while the write is open:
    /// it must wait and then see the committed cadastro.
    /// </summary>
    [Fact]
    public async Task EmissaoDuranteEscritaQueTiraACobertura_EsperaOCommitERecusa()
    {
        var c = await _op.CriarAsync();

        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        await using (var escrita = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Chave(c)}, 0))");
            var emissao = Task.Run(async () =>
            {
                using var totem = await _op.TotemAsync(c);
                using var r = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
                return (r.StatusCode, Corpo: await r.Content.ReadAsStringAsync());
            });
            await EsperarTravaConsultivaAsync(() => emissao.IsCompleted);
            emissao.IsCompleted.ShouldBeFalse("a emissão precisa esperar a escrita que mexe na cobertura");

            await db.Database.ExecuteSqlAsync(
                $"""UPDATE paf."Estacoes" SET "Ativa" = false WHERE "Id" IN ({c.GuicheA}, {c.GuicheB})""");
            await escrita.CommitAsync();

            var (status, corpo) = await emissao.WaitAsync(Prazo);
            status.ShouldBe(HttpStatusCode.Conflict, corpo);
            corpo.ShouldContain(MensagemSemCobertura);
        }

        (await _op.ContarAsync(c, d => d.Fichas)).ShouldBe(0);
    }

    /// <summary>The converse: an emission in flight (shared lock held) makes the station write wait for its commit.</summary>
    [Fact]
    public async Task DesativacaoDuranteEmissao_EsperaAEmissaoTerminar()
    {
        var c = await _op.CriarAsync();
        await EnviarAsync(c, new DisableEstacaoCommand(c.GuicheB));

        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        await using var emissao = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({Chave(c)}, 0))");

        var desativacao = EnviarAsync(c, new DisableEstacaoCommand(c.GuicheA));
        await EsperarTravaConsultivaAsync(() => desativacao.IsCompleted);
        desativacao.IsCompleted.ShouldBeFalse("a desativação precisa esperar a emissão que já conferiu a cobertura");

        await emissao.CommitAsync();
        (await desativacao.WaitAsync(Prazo)).CombinacoesSemCobertura.Select(x => x.Letra).Order().ShouldBe(["A", "AP"]);
    }

    /// <summary>
    /// 40 emissions racing the disable of the last guichê: whatever was emitted is already committed when the
    /// disable returns, and nothing is emitted after it.
    /// </summary>
    [Fact]
    public async Task EmissoesConcorrentesComDesativacao_NenhumaPassaDepoisDoCommit()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        await EnviarAsync(c, new DisableEstacaoCommand(c.GuicheB));

        using var largada = new SemaphoreSlim(0);
        var emissoes = Enumerable.Range(0, 40).Select(async _ =>
        {
            await largada.WaitAsync();
            using var r = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
            return r.StatusCode;
        }).ToList();
        largada.Release(40);
        await Task.Delay(20);

        await EnviarAsync(c, new DisableEstacaoCommand(c.GuicheA));
        var aposCommit = await _op.ContarAsync(c, d => d.Fichas);

        var status = await Task.WhenAll(emissoes).WaitAsync(Prazo);
        status.ShouldAllBe(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict);
        (await _op.ContarAsync(c, d => d.Fichas)).ShouldBe(aposCommit, "nenhuma emissão pode gravar depois do commit da desativação");
        status.Count(s => s == HttpStatusCode.OK).ShouldBe(aposCommit);
        using var depois = await PafOperacao.EmitirAsync(totem, c.Caixa, c.Normal, Guid.NewGuid().ToString("N"));
        depois.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private string Chave(CenarioOperacao c) => $"paf-cobertura:{_tenant.TenantId}:{c.UnidadeId:D}";

    private async Task<T> EnviarAsync<T>(CenarioOperacao c, IQuery<T> query)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(query);
    }

    private async Task<T> EnviarAsync<T>(CenarioOperacao c, ICommand<T> command)
    {
        using var scope = factory.Services.CreateScope();
        _tenant.Contexto(scope, c.UnidadeId);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command);
    }

    private static async Task<List<(Guid FilaId, Guid PrioridadeId)>> BotoesAsync(HttpClient totem)
    {
        using var resposta = await totem.GetAsync(RotaConfiguracao);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var cfg = await resposta.Content.ReadFromJsonAsync<ConfiguracaoEstacaoDto>();
        return cfg!.Botoes.OrderBy(b => b.Ordem).Select(b => (b.FilaId, b.PrioridadeId)).ToList();
    }

    private async Task<TimeOnly> AgoraEmSaoPauloAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var agora = await db.Database
            .SqlQuery<DateTime>($"""SELECT (now() AT TIME ZONE 'America/Sao_Paulo') AS "Value" """)
            .SingleAsync();
        return TimeOnly.FromDateTime(agora);
    }

    /// <summary>Until a backend waits on an advisory lock, or the request already answered.</summary>
    private async Task EsperarTravaConsultivaAsync(Func<bool> respondeu)
    {
        var limite = DateTime.UtcNow + Prazo;
        while (DateTime.UtcNow < limite)
        {
            if (respondeu())
            {
                return;
            }

            using var scope = factory.Services.CreateScope();
            var db = _tenant.Contexto(scope, unidadeCorrente: null);
            var esperando = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_stat_activity
                 WHERE datname = current_database() AND wait_event_type = 'Lock' AND wait_event = 'advisory'
                """).SingleAsync();
            if (esperando >= 1)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Nenhuma requisição chegou à trava de cobertura em {Prazo}.");
    }
}
