using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Fichas.ListFichasEmAtendimento;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF13 fichas em atendimento (F07, requisitos seção 4): the instrument that detects the abandoned atendimento.
/// Only Chamado and EmAtendimento, no date filter, stay on the PostgreSQL clock, longest first, unit and tenant
/// isolation, and the row's cancel (F04 route) releasing the ficha's waiting sibling.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafFichasEmAtendimentoTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Rota = "/api/v1/paf/fichas/em-atendimento";

    private readonly PafTenantCliente _tenant = new(factory, "paf-f07");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task SoChamadoEEmAtendimento_ComTotal_OrdenadosPelaMaiorPermanencia()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(_tenant, c.UnidadeId);
        await AtendimentoAsync(_tenant, c, c.Caixa, 1);
        var chamado = await AtendimentoAsync(_tenant, c, c.Caixa, 2);
        var emAtendimento = await AtendimentoAsync(_tenant, c, c.Caixa, 3);
        var encerrado = await AtendimentoAsync(_tenant, c, c.Caixa, 4);
        var cancelado = await AtendimentoAsync(_tenant, c, c.Caixa, 5);
        await ColocarEmCursoAsync(_tenant, chamado, c.GuicheA, "10 minutes", iniciado: false);
        await ColocarEmCursoAsync(_tenant, emAtendimento, c.GuicheB, "3 hours", iniciado: true);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Encerrado', "ChamadoEm" = now() - interval '5 hours',
                "IniciadoEm" = now() - interval '5 hours', "EncerradoEm" = now()
            WHERE "Id" = {encerrado} AND "TenantId" = {_tenant.TenantId}
            """);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Cancelado', "ChamadoEm" = now() - interval '6 hours', "CanceladoEm" = now()
            WHERE "Id" = {cancelado} AND "TenantId" = {_tenant.TenantId}
            """);

        var lista = await ListarOkAsync(supervisor, filaId: null);

        lista.Total.ShouldBe(2);
        lista.Atendimentos.Select(a => a.AtendimentoId).ShouldBe(new[] { emAtendimento, chamado }, "a maior permanência vem primeiro");
        var primeiro = lista.Atendimentos[0];
        primeiro.Estado.ShouldBe(nameof(EstadoAtendimento.EmAtendimento));
        primeiro.Senha.ShouldBe("A3");
        primeiro.Fila.ShouldBe("Caixa");
        primeiro.Prioridade.ShouldBe("Normal");
        primeiro.EstacaoId.ShouldBe(c.GuicheB);
        primeiro.Guiche.ShouldBe("Guichê 2");
        primeiro.IniciadoEm.ShouldNotBeNull();
        primeiro.PermanenciaSegundos.ShouldBeGreaterThanOrEqualTo(3 * 3600, "ChamadoEm gravado há 3 h mostra pelo menos 3 h");
        primeiro.PermanenciaSegundos.ShouldBeLessThan(3 * 3600 + 120);
        var segundo = lista.Atendimentos[1];
        segundo.Estado.ShouldBe(nameof(EstadoAtendimento.Chamado));
        segundo.IniciadoEm.ShouldBeNull();
        segundo.PermanenciaSegundos.ShouldBeInRange(600, 600 + 120);
        lista.Filas.Select(f => f.Nome).ShouldBe(new[] { "Caixa", "Protocolo", "Retaguarda" });
    }

    [Fact]
    public async Task ChamadoOntem_ApareceHoje_ComPermanenciaDeMaisDeUmDia()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(_tenant, c.UnidadeId);
        var abandonado = await AtendimentoAsync(_tenant, c, c.Caixa, 1);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Fichas" SET "CriadoEm" = "CriadoEm" - interval '1 day'
            WHERE "UnidadeId" = {c.UnidadeId} AND "TenantId" = {_tenant.TenantId}
            """);
        await _tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET "CriadoEm" = "CriadoEm" - interval '1 day', "ElegivelDesde" = "ElegivelDesde" - interval '1 day'
            WHERE "Id" = {abandonado} AND "TenantId" = {_tenant.TenantId}
            """);
        await ColocarEmCursoAsync(_tenant, abandonado, c.GuicheA, "1 day", iniciado: true);
        var linha = await _op.AtendimentoAsync(c, abandonado);
        linha.DataOperacional.ShouldBeLessThan(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)), "o atendimento é de ontem pelo calendário de São Paulo");

        var lista = await ListarOkAsync(supervisor, filaId: null);

        lista.Atendimentos.Single().AtendimentoId.ShouldBe(abandonado, "sem filtro de data: o abandonado atravessa a meia-noite");
        lista.Atendimentos.Single().PermanenciaSegundos.ShouldBeGreaterThanOrEqualTo(86400);
    }

    [Fact]
    public async Task FiltroDeFila_RestringeLinhasETotal_EFilaDeOutraUnidadeE404()
    {
        var c = await _op.CriarAsync();
        var outra = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(_tenant, c.UnidadeId);
        var noCaixa = await AtendimentoAsync(_tenant, c, c.Caixa, 1);
        var noProtocolo = await AtendimentoAsync(_tenant, c, c.Protocolo, 1);
        await ColocarEmCursoAsync(_tenant, noCaixa, c.GuicheA, "1 minute", iniciado: false);
        await ColocarEmCursoAsync(_tenant, noProtocolo, c.GuicheB, "2 minutes", iniciado: false);

        var soCaixa = await ListarOkAsync(supervisor, c.Caixa);
        soCaixa.Total.ShouldBe(1);
        soCaixa.Atendimentos.Single().AtendimentoId.ShouldBe(noCaixa);
        (await ListarOkAsync(supervisor, c.Protocolo)).Atendimentos.Single().AtendimentoId.ShouldBe(noProtocolo);
        (await ListarOkAsync(supervisor, c.Retaguarda)).Total.ShouldBe(0);
        (await ListarOkAsync(supervisor, filaId: null)).Total.ShouldBe(2);

        foreach (var filaAlheia in new[] { outra.Caixa, Guid.NewGuid() })
        {
            using var resposta = await supervisor.GetAsync($"{Rota}?filaId={filaAlheia}");
            resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, "filtro nunca vira lista vazia silenciosa");
            PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync())
                .ShouldBe(ListFichasEmAtendimentoQueryHandler.MensagemFilaNaoEncontrada);
        }
    }

    [Fact]
    public async Task OutraUnidadeEOutroTenant_NuncaAparecem()
    {
        var c = await _op.CriarAsync();
        var outra = await _op.CriarAsync();
        var outroTenant = new PafTenantCliente(factory, "paf-f07b");
        await outroTenant.InicializarAsync();
        var opOutroTenant = new PafOperacao(factory, outroTenant);
        var x = await opOutroTenant.CriarAsync();
        using var supervisor = await SupervisorAsync(_tenant, c.UnidadeId);
        using var supervisorDaOutra = await SupervisorAsync(_tenant, outra.UnidadeId);

        var daUnidade = await AtendimentoAsync(_tenant, c, c.Caixa, 1);
        var daOutraUnidade = await AtendimentoAsync(_tenant, outra, outra.Caixa, 1);
        var doOutroTenant = await AtendimentoAsync(outroTenant, x, x.Caixa, 1);
        await ColocarEmCursoAsync(_tenant, daUnidade, c.GuicheA, "1 minute", iniciado: false);
        await ColocarEmCursoAsync(_tenant, daOutraUnidade, outra.GuicheA, "2 hours", iniciado: false);
        await ColocarEmCursoAsync(outroTenant, doOutroTenant, x.GuicheA, "3 hours", iniciado: false);

        (await ListarOkAsync(supervisor, filaId: null)).Atendimentos.Select(a => a.AtendimentoId).ShouldBe(new[] { daUnidade });
        (await ListarOkAsync(supervisorDaOutra, filaId: null)).Atendimentos.Select(a => a.AtendimentoId).ShouldBe(new[] { daOutraUnidade });
    }

    [Fact]
    public void Permanencia_CalculadaPeloRelogioDoBanco()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, Guid.NewGuid());

        var sql = ListFichasEmAtendimentoQueryHandler.Consulta(db, filaId: null).ToQueryString();

        // A client-evaluated subtraction would leave now() out of the SQL and read the API host's clock instead.
        sql.ShouldContain("now() - a.\"ChamadoEm\"", Case.Insensitive);
    }

    [Fact]
    public async Task SemPermissao_403()
    {
        var c = await _op.CriarAsync();
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);

        using var resposta = await atendente.GetAsync(Rota);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Atendimentos.Operar não dá FichasEmAtendimento.View");
    }

    [Fact]
    public async Task CancelarPelaLinha_TiraDaLista_ELiberaOIrmao()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(_tenant, c.UnidadeId);
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        var (caixa, protocolo) = await FichaComDoisAtendimentosAsync(c);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(guiche)).AtendimentoId.ShouldBe(caixa);
        var irmaoAntes = await _op.AtendimentoAsync(c, protocolo);

        var linha = (await ListarOkAsync(supervisor, filaId: null)).Atendimentos.Single();
        linha.AtendimentoId.ShouldBe(caixa, "o irmão aguardando não está em curso");
        using (var cancelar = await supervisor.PostAsync($"/api/v1/paf/fichas/atendimentos/{linha.AtendimentoId}/cancelar", content: null))
        {
            cancelar.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelar.Content.ReadAsStringAsync());
            (await cancelar.Content.ReadFromJsonAsync<AtendimentoAlteradoDto>())!.Versao.ShouldBe(linha.Versao + 1);
        }

        (await ListarOkAsync(supervisor, filaId: null)).Total.ShouldBe(0);
        var liberado = await _op.AtendimentoAsync(c, protocolo);
        liberado.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        liberado.ElegivelDesde.ShouldBeGreaterThan(irmaoAntes.ElegivelDesde, "a liberação reescreve o elegível desde");
        (await _op.AtendimentoAsync(c, caixa)).Estado.ShouldBe(EstadoAtendimento.Cancelado);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>A person of the unidade with RF13's view and RF12's cancel beyond the basic set, with no guichê.</summary>
    private static async Task<HttpClient> SupervisorAsync(PafTenantCliente tenant, Guid unidadeId)
    {
        var usuario = await tenant.CriarUsuarioAsync(unidadeId);
        await tenant.DarPapelAsync(usuario.UserId, PafPermissions.FichasEmAtendimento.View, PafPermissions.FichasDoDia.Cancelar);
        return tenant.Cliente((await tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static async Task<FichasEmAtendimentoDto> ListarOkAsync(HttpClient cliente, Guid? filaId)
    {
        using var resposta = await cliente.GetAsync(filaId is null ? Rota : $"{Rota}?filaId={filaId}");
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<FichasEmAtendimentoDto>())!;
    }

    /// <summary>One ficha with one waiting atendimento in the fila, Normal prioridade, letter A.</summary>
    private async Task<Guid> AtendimentoAsync(PafTenantCliente tenant, CenarioOperacao c, Guid filaId, int numero)
    {
        using var scope = factory.Services.CreateScope();
        var db = tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, filaId, c.Normal, "A", numero, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(c.UnidadeId, ficha.Id, filaId, c.Normal, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();
        return atendimento.Id;
    }

    /// <summary>Puts an atendimento in progress on a station, called <paramref name="ha"/> ago by the PostgreSQL clock.</summary>
    private static Task<int> ColocarEmCursoAsync(PafTenantCliente tenant, Guid atendimentoId, Guid estacaoId, string ha, bool iniciado) =>
        tenant.ExecutarAsync($"""
            UPDATE paf."Atendimentos" SET
                "Estado" = CASE WHEN {iniciado} THEN 'EmAtendimento' ELSE 'Chamado' END,
                "EstacaoId" = {estacaoId},
                "ChamadoEm" = now() - CAST({ha} AS interval),
                "IniciadoEm" = CASE WHEN {iniciado} THEN now() ELSE NULL END,
                "Versao" = "Versao" + 1
            WHERE "Id" = {atendimentoId} AND "TenantId" = {tenant.TenantId}
            """);

    /// <summary>One ficha routed to Caixa and to Protocolo, both waiting: guichê A serves only Caixa.</summary>
    private async Task<(Guid Caixa, Guid Protocolo)> FichaComDoisAtendimentosAsync(CenarioOperacao c)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, c.Caixa, c.Normal, "A", 1, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
        var caixa = Atendimento.Criar(c.UnidadeId, ficha.Id, c.Caixa, c.Normal, atendimentoOrigemId: null);
        var protocolo = Atendimento.Criar(c.UnidadeId, ficha.Id, c.Protocolo, c.Normal, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.AddRange(caixa, protocolo);
        await db.SaveChangesAsync();
        return (caixa.Id, protocolo.Id);
    }
}
