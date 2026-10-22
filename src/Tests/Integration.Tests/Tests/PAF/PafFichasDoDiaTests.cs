using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Fichas.ListAtendimentosDoDia;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF12 "fichas do dia" (F06): the list of the current unit's atendimentos on a day, with the wait computed by
/// PostgreSQL from ElegivelDesde and the waiting total, and the reprint of a stored ficha without a new number.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafFichasDoDiaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaLista = "/api/v1/paf/fichas/atendimentos";

    private readonly PafTenantCliente _tenant = new(factory, "paf-fdd");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    private static string RotaImpressao(Guid fichaId) => $"/api/v1/paf/fichas/{fichaId}/impressao";

    [Fact]
    public async Task Lista_SoDaUnidadeCorrente_TotalAguardandoConfereComContagemIndependente_FiltrosDeFilaEEstado()
    {
        var c = await _op.CriarAsync();
        var d = await _op.CriarAsync();
        var aguardando = await InserirAsync(c, c.Caixa, c.Normal, "A", 1, origem: null);
        var cancelado = await InserirAsync(c, c.Caixa, c.Normal, "A", 2, origem: null);
        var chamado = await InserirAsync(c, c.Protocolo, c.Normal, "B", 1, origem: null);
        await InserirAsync(c, c.Protocolo, c.Normal, "B", 2, origem: null);
        var deOutraUnidade = await InserirAsync(d, d.Caixa, d.Normal, "A", 1, origem: null);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Cancelado', "CanceladoEm" = now(), "Versao" = 1 WHERE "Id" = {cancelado.AtendimentoId}
            """);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Chamado', "ChamadoEm" = now(), "EstacaoId" = {c.GuicheB}, "Versao" = 1
             WHERE "Id" = {chamado.AtendimentoId}
            """);
        using var supervisor = await SupervisorAsync(c.UnidadeId);

        var tudo = await ListarOkAsync(supervisor, "");

        tudo.Atendimentos.Select(a => a.AtendimentoId).ShouldBe(
            [aguardando.AtendimentoId, cancelado.AtendimentoId, chamado.AtendimentoId, (await IdDoNumeroAsync(c, "B", 2))],
            ignoreOrder: true);
        tudo.Atendimentos.ShouldNotContain(a => a.AtendimentoId == deOutraUnidade.AtendimentoId, "outra unidade nunca aparece");
        tudo.TotalAguardando.ShouldBe(2);
        tudo.TotalAguardando.ShouldBe(await ContarAguardandoHojeAsync(c.UnidadeId, filaId: null));
        tudo.Truncada.ShouldBeFalse();
        tudo.Filas.Select(f => f.Id).ShouldBe([c.Caixa, c.Protocolo, c.Retaguarda], ignoreOrder: true);
        var linhaChamada = tudo.Atendimentos.Single(a => a.AtendimentoId == chamado.AtendimentoId);
        linhaChamada.Senha.ShouldBe("B1");
        linhaChamada.Estado.ShouldBe(nameof(EstadoAtendimento.Chamado));
        linhaChamada.EstacaoId.ShouldBe(c.GuicheB);
        linhaChamada.Guiche.ShouldBe("Guichê 2");
        linhaChamada.Fila.ShouldBe("Protocolo");
        linhaChamada.Prioridade.ShouldBe("Normal");
        linhaChamada.Versao.ShouldBe(1);
        tudo.Atendimentos.Single(a => a.AtendimentoId == aguardando.AtendimentoId).Guiche.ShouldBeNull();

        var caixa = await ListarOkAsync(supervisor, $"?filaId={c.Caixa}");
        caixa.Atendimentos.Select(a => a.AtendimentoId).ShouldBe([aguardando.AtendimentoId, cancelado.AtendimentoId], ignoreOrder: true);
        caixa.TotalAguardando.ShouldBe(await ContarAguardandoHojeAsync(c.UnidadeId, c.Caixa));
        caixa.TotalAguardando.ShouldBe(1);

        var cancelados = await ListarOkAsync(supervisor, $"?estado=Cancelado");
        cancelados.Atendimentos.Select(a => a.AtendimentoId).ShouldBe([cancelado.AtendimentoId]);
        cancelados.TotalAguardando.ShouldBe(2, "o total de aguardando não depende do filtro de estado");

        using var supervisorDeD = await SupervisorAsync(d.UnidadeId);
        var deD = await ListarOkAsync(supervisorDeD, "");
        deD.Atendimentos.Select(a => a.AtendimentoId).ShouldBe([deOutraUnidade.AtendimentoId]);
        deD.TotalAguardando.ShouldBe(1);

        using var filaDeOutraUnidade = await supervisor.GetAsync($"{RotaLista}?filaId={d.Caixa}");
        filaDeOutraUnidade.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await filaDeOutraUnidade.Content.ReadAsStringAsync()).ShouldContain(ListAtendimentosDoDiaQueryHandler.MensagemFilaNaoEncontrada);
    }

    [Fact]
    public async Task Espera_ContaDeElegivelDesde_AteAChamadaOCancelamentoOuAgora()
    {
        var c = await _op.CriarAsync();
        var liberado = await InserirAsync(c, c.Caixa, c.Normal, "A", 1, origem: null);
        var chamado = await InserirAsync(c, c.Caixa, c.Normal, "A", 2, origem: null);
        var canceladoSemChamar = await InserirAsync(c, c.Caixa, c.Normal, "A", 3, origem: null);
        // Created ten minutes ago, released two minutes ago: the wait counts from the release, never from the creation.
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "CriadoEm" = now() - interval '10 minutes', "ElegivelDesde" = now() - interval '2 minutes'
             WHERE "Id" = {liberado.AtendimentoId}
            """);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Chamado', "EstacaoId" = {c.GuicheA}, "Versao" = 1,
                   "ElegivelDesde" = now() - interval '1 hour', "ChamadoEm" = now() - interval '1 hour' + interval '5 minutes'
             WHERE "Id" = {chamado.AtendimentoId}
            """);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Cancelado', "Versao" = 1,
                   "ElegivelDesde" = now() - interval '30 minutes', "CanceladoEm" = now() - interval '29 minutes'
             WHERE "Id" = {canceladoSemChamar.AtendimentoId}
            """);
        using var supervisor = await SupervisorAsync(c.UnidadeId);

        var lista = await ListarOkAsync(supervisor, "");

        var esperaLiberado = lista.Atendimentos.Single(a => a.AtendimentoId == liberado.AtendimentoId).EsperaSegundos;
        esperaLiberado.ShouldBeInRange(120, 180, "conta da liberação, não da criação de dez minutos atrás");
        lista.Atendimentos.Single(a => a.AtendimentoId == chamado.AtendimentoId).EsperaSegundos.ShouldBe(300, "até a chamada, parada depois dela");
        lista.Atendimentos.Single(a => a.AtendimentoId == canceladoSemChamar.AtendimentoId).EsperaSegundos.ShouldBe(60, "até o cancelamento");
    }

    [Fact]
    public async Task Ontem_PorDataOperacional_EncaminhadoDepoisDaMeiaNoiteApareceNoDiaNovo()
    {
        var c = await _op.CriarAsync();
        var origem = await InserirAsync(c, c.Caixa, c.Normal, "A", 7, origem: null);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Fichas" SET "CriadoEm" = now() - interval '1 day' WHERE "Id" = {origem.FichaId}
            """);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Encerrado', "EstacaoId" = {c.GuicheA}, "Versao" = 3,
                   "CriadoEm" = now() - interval '1 day', "ElegivelDesde" = now() - interval '1 day',
                   "ChamadoEm" = now() - interval '1 day' + interval '1 minute', "IniciadoEm" = now() - interval '1 day' + interval '2 minutes',
                   "EncerradoEm" = now() - interval '5 minutes'
             WHERE "Id" = {origem.AtendimentoId}
            """);
        var destino = await InserirAsync(c, c.Protocolo, c.Normal, origem.FichaId, origem.AtendimentoId);
        using var supervisor = await SupervisorAsync(c.UnidadeId);

        var hoje = await ListarOkAsync(supervisor, "");
        var ontem = await ListarOkAsync(supervisor, $"?data={hoje.Data.AddDays(-1):yyyy-MM-dd}");

        hoje.Data.ShouldBe(await HojeEmSaoPauloAsync(), "sem data, o dia vem do relógio do PostgreSQL em São Paulo");
        hoje.Atendimentos.Select(a => a.AtendimentoId).ShouldBe([destino]);
        var encaminhado = hoje.Atendimentos.Single();
        encaminhado.Encaminhado.ShouldBeTrue();
        encaminhado.Senha.ShouldBe("A7", "o cliente segue com a senha da emissão de ontem");
        encaminhado.FichaId.ShouldBe(origem.FichaId);
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(encaminhado.ChegadaEm, SaoPaulo).DateTime).ShouldBe(ontem.Data, "chegada é a emissão");
        hoje.TotalAguardando.ShouldBe(1);

        ontem.Atendimentos.Select(a => a.AtendimentoId).ShouldBe([origem.AtendimentoId]);
        ontem.Atendimentos.Single().Estado.ShouldBe(nameof(EstadoAtendimento.Encerrado));
        ontem.Atendimentos.Single().Encaminhado.ShouldBeFalse();
        ontem.TotalAguardando.ShouldBe(0);
    }

    [Fact]
    public async Task Reimpressao_MesmaSenhaEIdentificadorPublico_SemFichaNova_OutraUnidade404()
    {
        var c = await _op.CriarAsync();
        var d = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        var emitida = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        // A forward adds an atendimento to the ficha; the printed one stays the emission's.
        await InserirAsync(c, c.Protocolo, c.Normal, emitida.FichaId, emitida.AtendimentoId);
        var fichasAntes = await _op.ContarAsync(c, db => db.Fichas);
        using var supervisor = await SupervisorAsync(c.UnidadeId);

        using var resposta = await supervisor.GetAsync(RotaImpressao(emitida.FichaId));

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var reimpressa = (await resposta.Content.ReadFromJsonAsync<FichaEmitidaDto>())!;
        reimpressa.ShouldBe(emitida);
        reimpressa.EmitidaEm.ShouldBe(emitida.EmitidaEm);
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(fichasAntes, "reimprimir não emite");
        (await ListarOkAsync(supervisor, "")).Atendimentos.Count.ShouldBe(2);

        using var supervisorDeD = await SupervisorAsync(d.UnidadeId);
        using var deOutraUnidade = await supervisorDeD.GetAsync(RotaImpressao(emitida.FichaId));
        deOutraUnidade.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var inexistente = await supervisor.GetAsync(RotaImpressao(Guid.NewGuid()));
        inexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EntradaInvalida_400_SemPermissao_403()
    {
        var c = await _op.CriarAsync();
        var ficha = await InserirAsync(c, c.Caixa, c.Normal, "A", 1, origem: null);
        using var supervisor = await SupervisorAsync(c.UnidadeId);

        foreach (var consulta in new[] { "?estado=Bogus", "?estado=aguardando", "?estado=0", "?data=2026-13-40", $"?filaId={Guid.Empty}" })
        {
            using var resposta = await supervisor.GetAsync(RotaLista + consulta);
            resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, consulta);
        }

        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.FichasDoDia.Cancelar, PafPermissions.FichasDoDia.Reabilitar);
        using var semVer = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        using var lista = await semVer.GetAsync(RotaLista);
        lista.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var impressao = await semVer.GetAsync(RotaImpressao(ficha.FichaId));
        impressao.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MaisLinhasQueOTeto_DevolveAsMaisNovas_EMarcaTruncada()
    {
        var c = await _op.CriarAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            for (var numero = 1; numero <= ListAtendimentosDoDiaQueryHandler.MaxAtendimentos + 1; numero++)
            {
                var f = Ficha.Criar(c.UnidadeId, c.Caixa, c.Normal, "A", numero, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
                db.Fichas.Add(f);
                db.Atendimentos.Add(Atendimento.Criar(c.UnidadeId, f.Id, c.Caixa, c.Normal, atendimentoOrigemId: null));
            }

            await db.SaveChangesAsync();
        }

        // The oldest is pushed back so "newest first" has a single answer for what is cut.
        var maisAntigo = await IdDoNumeroAsync(c, "A", 1);
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "CriadoEm" = "CriadoEm" - interval '1 minute' WHERE "Id" = {maisAntigo}""");
        using var supervisor = await SupervisorAsync(c.UnidadeId);

        var lista = await ListarOkAsync(supervisor, "");

        lista.Truncada.ShouldBeTrue();
        lista.Atendimentos.Count.ShouldBe(ListAtendimentosDoDiaQueryHandler.MaxAtendimentos);
        lista.Atendimentos.ShouldNotContain(a => a.AtendimentoId == maisAntigo);
        lista.TotalAguardando.ShouldBe(ListAtendimentosDoDiaQueryHandler.MaxAtendimentos + 1, "o total não é cortado pelo teto");
    }

    // ─── apoio ───────────────────────────────────────────────────────

    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private sealed record Inserida(Guid FichaId, Guid AtendimentoId);

    /// <summary>A ficha and its first atendimento written directly, Aguardando; the test moves the state by SQL.</summary>
    private async Task<Inserida> InserirAsync(CenarioOperacao c, Guid fila, Guid prioridade, string letra, int numero, Guid? origem)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, fila, prioridade, letra, numero, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(c.UnidadeId, ficha.Id, fila, prioridade, origem);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        return new Inserida(ficha.Id, atendimento.Id);
    }

    /// <summary>A forwarded atendimento of an existing ficha, created now, Aguardando.</summary>
    private async Task<Guid> InserirAsync(CenarioOperacao c, Guid fila, Guid prioridade, Guid fichaId, Guid origem)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var atendimento = Atendimento.Criar(c.UnidadeId, fichaId, fila, prioridade, origem);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        return atendimento.Id;
    }

    private async Task<Guid> IdDoNumeroAsync(CenarioOperacao c, string letra, int numero)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        return await (from a in db.Atendimentos
                      join f in db.Fichas on a.FichaId equals f.Id
                      where f.Letra == letra && f.Numero == numero
                      select a.Id).SingleAsync();
    }

    /// <summary>Independent of the handler: no shared SQL, the day taken by PostgreSQL itself.</summary>
    private async Task<int> ContarAguardandoHojeAsync(Guid unidadeId, Guid? filaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var linhas = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM paf."Atendimentos"
             WHERE "UnidadeId" = {unidadeId} AND "Estado" = 'Aguardando'
               AND ({filaId}::uuid IS NULL OR "FilaId" = {filaId}::uuid)
               AND "DataOperacional" = (now() AT TIME ZONE 'America/Sao_Paulo')::date
            """).ToListAsync();
        return linhas.Single();
    }

    private async Task<DateOnly> HojeEmSaoPauloAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        var linhas = await db.Database.SqlQuery<DateOnly>($"""SELECT (now() AT TIME ZONE 'America/Sao_Paulo')::date AS "Value" """).ToListAsync();
        return linhas.Single();
    }

    /// <summary>A person of the unidade holding only FichasDoDia.View, logged in, no guichê.</summary>
    private async Task<HttpClient> SupervisorAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.FichasDoDia.View);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static async Task<FichasDoDiaDto> ListarOkAsync(HttpClient cliente, string consulta)
    {
        using var resposta = await cliente.GetAsync(RotaLista + consulta);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<FichasDoDiaDto>())!;
    }
}
