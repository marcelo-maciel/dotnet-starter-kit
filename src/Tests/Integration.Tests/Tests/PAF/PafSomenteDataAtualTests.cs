using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Parametros;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF06 "chamar somente fichas da data atual" on the call (requisitos seção 4, "Fichas não atendidas na virada
/// do dia"): unidade row over empresa row over the factory default (on), compared on DataElegibilidade.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafSomenteDataAtualTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly string Chave = CatalogoParametros.SomenteFichasDaDataAtual.Chave;

    private readonly PafTenantCliente _tenant = new(factory, "paf-par");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("false", null, true)]
    [InlineData("false", "true", false)]
    [InlineData("true", "false", true)]
    [InlineData("abc", null, false)]
    [InlineData("false", "abc", false)]
    public async Task Chamar_FichaElegivelOntem_SegueOParametroEfetivo(string? empresa, string? unidade, bool chamada)
    {
        var c = await _op.CriarAsync();
        await GravarAsync(c.UnidadeId, empresa, unidade);
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await ElegivelOntemAsync(ficha.AtendimentoId);

        using var resposta = await PafOperacao.ChamarAsync(atendente);

        if (chamada)
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);
        }
        else
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, await resposta.Content.ReadAsStringAsync());
            (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
        }
    }

    [Fact]
    public async Task Chamar_ParametroDaUnidadeB_NaoAfetaAUnidadeA()
    {
        var a = await _op.CriarAsync();
        var b = await _op.CriarAsync();
        await GravarAsync(b.UnidadeId, empresa: null, unidade: "false");
        using var totemA = await _op.TotemAsync(a);
        using var totemB = await _op.TotemAsync(b);
        using var atendenteA = await _op.AtendenteAsync(a, a.GuicheA);
        using var atendenteB = await _op.AtendenteAsync(b, b.GuicheA);
        var fichaA = await PafOperacao.EmitirOkAsync(totemA, a.Caixa, a.Normal);
        var fichaB = await PafOperacao.EmitirOkAsync(totemB, b.Caixa, b.Normal);
        await _op.EnvelhecerFichasAsync(a.UnidadeId);
        await _op.EnvelhecerFichasAsync(b.UnidadeId);
        await ElegivelOntemAsync(fichaA.AtendimentoId);
        await ElegivelOntemAsync(fichaB.AtendimentoId);

        using var naA = await PafOperacao.ChamarAsync(atendenteA);
        naA.StatusCode.ShouldBe(HttpStatusCode.NotFound, "A não tem linha: vale o padrão de fábrica, ligado");
        (await PafOperacao.ChamarOkAsync(atendenteB)).AtendimentoId.ShouldBe(fichaB.AtendimentoId);
    }

    [Fact]
    public async Task Chamar_UltimoSegundoDeOntemEmSaoPaulo_FicaFora_EMeiaNoiteDeHojeEntra()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        // The instant comes from the database clock, so the test and the filter agree on what "today" is.
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos"
               SET "ElegivelDesde" = (((now() AT TIME ZONE 'America/Sao_Paulo')::date - 1) + time '23:59:59') AT TIME ZONE 'America/Sao_Paulo'
             WHERE "Id" = {ficha.AtendimentoId}
            """);
        var ontem = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
        ontem.DataElegibilidade.ShouldBe(ontem.DataOperacional.AddDays(-1));
        using var foraDoDia = await PafOperacao.ChamarAsync(atendente);
        foraDoDia.StatusCode.ShouldBe(HttpStatusCode.NotFound, "23:59:59 de ontem em São Paulo é ontem");

        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos"
               SET "ElegivelDesde" = ((now() AT TIME ZONE 'America/Sao_Paulo')::date + time '00:00:00') AT TIME ZONE 'America/Sao_Paulo'
             WHERE "Id" = {ficha.AtendimentoId}
            """);
        (await PafOperacao.ChamarOkAsync(atendente)).AtendimentoId.ShouldBe(ficha.AtendimentoId);
    }

    [Fact]
    public async Task Chamar_ParametroDesligado_DoisGuichesEmParalelo_ChamamTodasAsDeOntemSemRepetir()
    {
        const int fichas = 10;
        var c = await _op.CriarAsync();
        await GravarAsync(c.UnidadeId, empresa: "false", unidade: null);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        for (var i = 0; i < fichas; i++)
        {
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        }

        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "UnidadeId" = {c.UnidadeId}""");

        var chamados = new List<Guid>();
        for (var rodada = 0; rodada < fichas / 2; rodada++)
        {
            var par = await Task.WhenAll(
                Task.Run(() => ChamarEEncerrarAsync(guicheA)),
                Task.Run(() => ChamarEEncerrarAsync(guicheB)));
            chamados.AddRange(par);
        }

        chamados.Distinct().Count().ShouldBe(fichas);
        using var vazio = await PafOperacao.ChamarAsync(guicheA);
        vazio.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Leitor_ValorInvalido_DevolveOPadraoEAvisa_AusenteDevolveOPadraoSemAvisar()
    {
        var c = await _op.CriarAsync();
        var logger = new LoggerColetor();

        using (var scope = factory.Services.CreateScope())
        {
            var leitor = new LeitorParametros(_tenant.Contexto(scope, c.UnidadeId), logger);
            (await leitor.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, c.UnidadeId, CancellationToken.None)).ShouldBeTrue();
            logger.Avisos.ShouldBeEmpty("ausente é o estado de fábrica, não erro");
        }

        await GravarAsync(c.UnidadeId, empresa: "false", unidade: "abc");
        using (var scope = factory.Services.CreateScope())
        {
            var leitor = new LeitorParametros(_tenant.Contexto(scope, c.UnidadeId), logger);
            (await leitor.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, c.UnidadeId, CancellationToken.None))
                .ShouldBeTrue("valor inválido na unidade vale o padrão, não o da empresa");
        }

        var aviso = logger.Avisos.ShouldHaveSingleItem();
        aviso.ShouldContain(Chave);
        aviso.ShouldContain("Unidade");
        aviso.ShouldNotContain("abc", Case.Sensitive, "o valor gravado não vai para o log");
    }

    private static async Task<Guid> ChamarEEncerrarAsync(HttpClient atendente)
    {
        var chamada = await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "encerrar");
        return chamada.AtendimentoId;
    }

    private Task<int> ElegivelOntemAsync(Guid atendimentoId) =>
        _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "Id" = {atendimentoId}""");

    private async Task GravarAsync(Guid unidadeId, string? empresa, string? unidade)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeId);
        if (empresa is not null)
        {
            db.Parametros.Add(Parametro.CriarDeEmpresa(Chave, empresa));
        }

        if (unidade is not null)
        {
            db.Parametros.Add(Parametro.CriarDeUnidade(unidadeId, Chave, unidade));
        }

        await db.SaveChangesAsync();
    }

    private sealed class LoggerColetor : ILogger<LeitorParametros>
    {
        public List<string> Avisos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Avisos.Add(formatter(state, exception));
            }
        }
    }
}
