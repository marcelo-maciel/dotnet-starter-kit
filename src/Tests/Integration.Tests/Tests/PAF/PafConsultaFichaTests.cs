using System.Globalization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF18 "consultar ficha" (F14): the printed senha with its letter and the day (D02), in the current unit, with
/// every atendimento of the ficha; cancel and reabilitar go through the F04 and F05 routes.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafConsultaFichaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Rota = "/api/v1/paf/fichas/consulta";

    private readonly PafTenantCliente _tenant = new(factory, "paf-cons");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Consultar_FichaEncaminhada_ListaOrigemEncerradaEDestinoAguardando_ComFilaGuicheETempos()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        using var encaminhar = await atendente.PostAsJsonAsync(
            PafOperacao.Rota(chamada.AtendimentoId, "encaminhar"), new EncaminharAtendimentoRequest([c.Protocolo], c.Normal));
        encaminhar.StatusCode.ShouldBe(HttpStatusCode.OK, await encaminhar.Content.ReadAsStringAsync());
        var destinoId = (await encaminhar.Content.ReadFromJsonAsync<AtendimentoEncaminhadoDto>())!.Destinos.Single().AtendimentoId;
        var data = await DataDaFichaAsync(c, ficha);

        var consulta = (await ConsultarOkAsync(supervisor, ficha.Senha, data)).ShouldHaveSingleItem();

        consulta.FichaId.ShouldBe(ficha.FichaId);
        consulta.Senha.ShouldBe("A1");
        consulta.DataOperacional.ShouldBe(data);
        consulta.Fila.ShouldBe("Caixa");
        consulta.Prioridade.ShouldBe("Normal");
        consulta.Atendimentos.Select(a => a.AtendimentoId).ShouldBe([ficha.AtendimentoId, destinoId], "ordem de criação");
        var origem = consulta.Atendimentos[0];
        origem.Estado.ShouldBe(nameof(EstadoAtendimento.Encerrado));
        origem.Fila.ShouldBe("Caixa");
        origem.Guiche.ShouldBe("Guichê 1");
        origem.AtendimentoOrigemId.ShouldBeNull();
        origem.ChamadoEm.ShouldNotBeNull();
        origem.IniciadoEm.ShouldNotBeNull();
        origem.FimEm.ShouldNotBeNull();
        origem.TempoTotalSegundos.ShouldBe((long)Math.Floor((origem.FimEm.Value - origem.IniciadoEm.Value).TotalSeconds));
        origem.Versao.ShouldBe(3);
        var destino = consulta.Atendimentos[1];
        destino.Estado.ShouldBe(nameof(EstadoAtendimento.Aguardando));
        destino.Fila.ShouldBe("Protocolo");
        destino.Prioridade.ShouldBe("Normal");
        destino.AtendimentoOrigemId.ShouldBe(ficha.AtendimentoId);
        destino.Guiche.ShouldBeNull();
        destino.IniciadoEm.ShouldBeNull();
        destino.FimEm.ShouldBeNull();
        destino.TempoTotalSegundos.ShouldBeNull("sem início não há tempo de atendimento");
    }

    [Fact]
    public async Task Consultar_EmAtendimentoHaTresHoras_TempoCorrePeloRelogioDoBanco_SemFim()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, ficha.AtendimentoId, "iniciar");
        await _tenant.ExecutarAsync(
            $"""UPDATE paf."Atendimentos" SET "IniciadoEm" = now() - interval '3 hours' WHERE "Id" = {ficha.AtendimentoId}""");

        var atendimento = (await ConsultarOkAsync(supervisor, ficha.Senha, await DataDaFichaAsync(c, ficha)))
            .ShouldHaveSingleItem().Atendimentos.ShouldHaveSingleItem();

        atendimento.Estado.ShouldBe(nameof(EstadoAtendimento.EmAtendimento));
        atendimento.FimEm.ShouldBeNull();
        atendimento.TempoTotalSegundos.ShouldNotBeNull();
        atendimento.TempoTotalSegundos.Value.ShouldBeInRange(3 * 3600, (3 * 3600) + 120);
    }

    [Fact]
    public void TempoTotal_SaiDoSqlComNowDoBanco_NaoDoRelogioDoProcesso()
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, Guid.NewGuid());

        var sql = ConsultarFichaQueryHandler.AtendimentosDasFichas(db, [Guid.NewGuid()]).ToQueryString();

        sql.ShouldContain("now()", Case.Insensitive, "o tempo do atendimento em curso é calculado pelo PostgreSQL");
        sql.ShouldContain("\"IniciadoEm\"");
    }

    [Fact]
    public async Task Consultar_DataErradaSenhaInexistenteOuDeOutraUnidade_404_ECadaUnidadeVeSoASua()
    {
        var c1 = await _op.CriarAsync();
        var c2 = await _op.CriarAsync();
        using var totem1 = await _op.TotemAsync(c1);
        using var totem2 = await _op.TotemAsync(c2);
        using var supervisor1 = await SupervisorAsync(c1.UnidadeId);
        using var supervisor2 = await SupervisorAsync(c2.UnidadeId);
        var ficha1 = await PafOperacao.EmitirOkAsync(totem1, c1.Caixa, c1.Normal);
        var ficha2 = await PafOperacao.EmitirOkAsync(totem2, c2.Caixa, c2.Normal);
        ficha1.Senha.ShouldBe(ficha2.Senha, "a mesma senha nas duas unidades");
        var data = await DataDaFichaAsync(c1, ficha1);

        (await ConsultarOkAsync(supervisor1, ficha1.Senha, data)).ShouldHaveSingleItem().FichaId.ShouldBe(ficha1.FichaId);
        (await ConsultarOkAsync(supervisor2, ficha2.Senha, data)).ShouldHaveSingleItem().FichaId.ShouldBe(ficha2.FichaId);

        await NaoEncontradaAsync(supervisor1, ficha1.Senha, data.AddDays(-1));
        await NaoEncontradaAsync(supervisor1, ficha1.Senha, data.AddDays(1));
        await NaoEncontradaAsync(supervisor1, "A2", data);
        await NaoEncontradaAsync(supervisor1, "AP1", data);
        await NaoEncontradaAsync(supervisor1, "A01", data);
        await NaoEncontradaAsync(supervisor1, "A 1", data);
    }

    [Fact]
    public async Task Consultar_MinusculasEEspacosNasPontas_Normalizados()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var preferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        var data = await DataDaFichaAsync(c, preferencial);

        foreach (var digitada in new[] { "ap1", "  Ap1\t", "AP1" })
        {
            (await ConsultarOkAsync(supervisor, digitada, data)).ShouldHaveSingleItem().FichaId.ShouldBe(preferencial.FichaId, digitada);
        }
    }

    [Fact]
    public async Task Consultar_EntradaMalFormada_400_NuncaQuinhentos()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var longa = "A" + new string('1', ConsultarFichaQueryValidator.SenhaMaxLength);

        foreach (var consulta in new[]
                 {
                     $"?data={hoje}", $"?senha=&data={hoje}", $"?senha=%20%20&data={hoje}", $"?senha=AB&data={hoje}",
                     $"?senha={longa}&data={hoje}", "?senha=A1", "?senha=A1&data=", "?senha=A1&data=abc", "?senha=A1&data=2026-13-45",
                 })
        {
            using var resposta = await supervisor.GetAsync(Rota + consulta);
            resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{consulta}: {await resposta.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Consultar_LetraComDigitoQueImprimeAMesmaSenha_DevolveAsDuasFichas()
    {
        var c = await _op.CriarAsync();
        // Protocolo/Normal prints "A1" + 2 and Caixa/Normal prints "A" + 12: both read A12.
        await _tenant.ExecutarAsync(
            $"""UPDATE paf."FilaPrioridades" SET "Letra" = 'A1', "NumeroInicial" = 2 WHERE "FilaId" = {c.Protocolo} AND "PrioridadeId" = {c.Normal}""");
        await _tenant.ExecutarAsync(
            $"""UPDATE paf."FilaPrioridades" SET "NumeroInicial" = 12 WHERE "FilaId" = {c.Caixa} AND "PrioridadeId" = {c.Normal}""");
        using var totem = await _op.TotemAsync(c);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var caixa = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var protocolo = await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        caixa.Senha.ShouldBe("A12");
        protocolo.Senha.ShouldBe("A12");

        var fichas = await ConsultarOkAsync(supervisor, "A12", await DataDaFichaAsync(c, caixa));

        fichas.Select(f => f.FichaId).ShouldBe([caixa.FichaId, protocolo.FichaId], "ordem de emissão");
        fichas.Select(f => f.Fila).ShouldBe(["Caixa", "Protocolo"]);
    }

    [Fact]
    public async Task Consultar_CancelarEReabilitarPelasRotasDoRF12_AConsultaSegueOEstado()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var data = await DataDaFichaAsync(c, ficha);
        var aguardando = (await ConsultarOkAsync(supervisor, ficha.Senha, data))[0].Atendimentos.ShouldHaveSingleItem();

        using (var cancelar = await supervisor.PostAsync($"/api/v1/paf/fichas/atendimentos/{aguardando.AtendimentoId}/cancelar", content: null))
        {
            cancelar.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelar.Content.ReadAsStringAsync());
        }

        var cancelado = (await ConsultarOkAsync(supervisor, ficha.Senha, data))[0].Atendimentos.ShouldHaveSingleItem();
        cancelado.Estado.ShouldBe(nameof(EstadoAtendimento.Cancelado));
        cancelado.Versao.ShouldBe(aguardando.Versao + 1);
        cancelado.FimEm.ShouldNotBeNull();
        cancelado.TempoTotalSegundos.ShouldBeNull("cancelado antes de iniciar");

        using (var reabilitar = await supervisor.PostAsync($"/api/v1/paf/fichas/atendimentos/{aguardando.AtendimentoId}/reabilitar", content: null))
        {
            reabilitar.StatusCode.ShouldBe(HttpStatusCode.OK, await reabilitar.Content.ReadAsStringAsync());
        }

        var reabilitado = (await ConsultarOkAsync(supervisor, ficha.Senha, data))[0].Atendimentos.ShouldHaveSingleItem();
        reabilitado.Estado.ShouldBe(nameof(EstadoAtendimento.Aguardando));
        reabilitado.Versao.ShouldBe(cancelado.Versao + 1);
        reabilitado.FimEm.ShouldBeNull();
        reabilitado.CriadoEm.ShouldBe(aguardando.CriadoEm);
    }

    [Fact]
    public async Task Consultar_SemFichasDoDiaView_403()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        using var resposta = await atendente.GetAsync(Url(ficha.Senha, await DataDaFichaAsync(c, ficha)));

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Atendimentos.Operar não inclui a consulta");
    }

    /// <summary>A person of the unidade with the RF12/RF18 permissions only, logged in, no guichê.</summary>
    private async Task<HttpClient> SupervisorAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(
            usuario.UserId, PafPermissions.FichasDoDia.View, PafPermissions.FichasDoDia.Cancelar, PafPermissions.FichasDoDia.Reabilitar);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private async Task<DateOnly> DataDaFichaAsync(CenarioOperacao c, FichaEmitidaDto ficha) =>
        (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).DataOperacional;

    private static string Url(string senha, DateOnly data) =>
        $"{Rota}?senha={Uri.EscapeDataString(senha)}&data={data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private static async Task<IReadOnlyList<FichaConsultadaDto>> ConsultarOkAsync(HttpClient cliente, string senha, DateOnly data)
    {
        using var resposta = await cliente.GetAsync(Url(senha, data));
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<List<FichaConsultadaDto>>())!;
    }

    private static async Task NaoEncontradaAsync(HttpClient cliente, string senha, DateOnly data)
    {
        using var resposta = await cliente.GetAsync(Url(senha, data));
        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{senha} {data}: {await resposta.Content.ReadAsStringAsync()}");
        PafSessaoLicencaTests.Detalhe(await resposta.Content.ReadAsStringAsync()).ShouldBe(ConsultarFichaQueryHandler.MensagemNaoEncontrada);
    }
}
