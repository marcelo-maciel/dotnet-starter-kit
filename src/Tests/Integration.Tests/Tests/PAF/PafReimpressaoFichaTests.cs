using System.Globalization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Features.v1.Fichas.ConsultarFicha;
using FSH.Modules.PAF.Features.v1.Fichas.ReimprimirFichaDeEspera;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF20 "ficha de espera" (F15): reprint by the printed senha with its letter, the day (D02) and the printed fila, in
/// the current unit, through the F06 reader: same ficha, never a new number.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafReimpressaoFichaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string Rota = "/api/v1/paf/fichas/reimpressao";

    private readonly PafTenantCliente _tenant = new(factory, "paf-reimp");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    [Fact]
    public async Task Reimprimir_MesmaSenhaFilaEIdentificadorPublico_SemFichaNova_MesmoDepoisDeEncaminhada()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var emitida = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var chamada = await PafOperacao.ChamarOkAsync(atendente);
        await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
        using (var encaminhar = await atendente.PostAsJsonAsync(
                   PafOperacao.Rota(chamada.AtendimentoId, "encaminhar"), new EncaminharAtendimentoRequest([c.Protocolo], c.Normal)))
        {
            encaminhar.StatusCode.ShouldBe(HttpStatusCode.OK, await encaminhar.Content.ReadAsStringAsync());
        }

        var data = await DataDaFichaAsync(c, emitida);
        var fichasAntes = await _op.ContarAsync(c, db => db.Fichas);
        var atendimentosAntes = await _op.ContarAsync(c, db => db.Atendimentos);

        var reimpressa = (await ReimprimirOkAsync(supervisor, emitida.Senha, data, c.Caixa)).ShouldHaveSingleItem();

        // EnvelhecerFichasAsync moved CriadoEm back so the call could see the ficha; every other printed field is frozen.
        reimpressa.ShouldBe(emitida with { EmitidaEm = reimpressa.EmitidaEm }, "a ficha guardada: mesma senha, fila, número e IdentificadorPublico");
        reimpressa.EmitidaEm.ShouldBeLessThan(emitida.EmitidaEm, "a emissão lida do banco, não o instante da reimpressão");
        reimpressa.AtendimentoId.ShouldBe(emitida.AtendimentoId, "o atendimento da emissão, não o do encaminhamento");
        reimpressa.Fila.ShouldBe("Caixa");
        (await _op.ContarAsync(c, db => db.Fichas)).ShouldBe(fichasAntes, "reimprimir não emite");
        (await _op.ContarAsync(c, db => db.Atendimentos)).ShouldBe(atendimentosAntes, "reimprimir não cria atendimento");
        await NaoEncontradaAsync(supervisor, emitida.Senha, data, c.Protocolo);
    }

    [Fact]
    public async Task Reimprimir_FilaDiferenteDataErradaSenhaInexistenteOuOutraUnidade_404_ECadaUnidadeVeSoASua()
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

        (await ReimprimirOkAsync(supervisor1, ficha1.Senha, data, c1.Caixa)).ShouldHaveSingleItem().FichaId.ShouldBe(ficha1.FichaId);
        (await ReimprimirOkAsync(supervisor2, ficha2.Senha, data, c2.Caixa)).ShouldHaveSingleItem().FichaId.ShouldBe(ficha2.FichaId);

        await NaoEncontradaAsync(supervisor1, ficha1.Senha, data, c1.Protocolo);
        await NaoEncontradaAsync(supervisor1, ficha1.Senha, data, c2.Caixa);
        await NaoEncontradaAsync(supervisor1, ficha1.Senha, data, Guid.NewGuid());
        await NaoEncontradaAsync(supervisor1, ficha1.Senha, data.AddDays(-1), c1.Caixa);
        await NaoEncontradaAsync(supervisor1, ficha1.Senha, data.AddDays(1), c1.Caixa);
        await NaoEncontradaAsync(supervisor1, "A2", data, c1.Caixa);
        await NaoEncontradaAsync(supervisor1, "A01", data, c1.Caixa);
        await NaoEncontradaAsync(supervisor1, "A 1", data, c1.Caixa);
    }

    [Fact]
    public async Task Reimprimir_MinusculasEEspacosNasPontas_Normalizados()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var preferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        var data = await DataDaFichaAsync(c, preferencial);

        foreach (var digitada in new[] { "ap1", "  Ap1\t", "AP1" })
        {
            (await ReimprimirOkAsync(supervisor, digitada, data, c.Caixa)).ShouldHaveSingleItem().FichaId.ShouldBe(preferencial.FichaId, digitada);
        }
    }

    [Fact]
    public async Task Reimprimir_DuasSenhasDaMesmaFilaQueImprimemOMesmoTexto_DevolveAsDuas()
    {
        var c = await _op.CriarAsync();
        // In Caixa, Preferencial prints "A1" + 2 and Normal prints "A" + 12: both read A12 in the same fila.
        await _tenant.ExecutarAsync(
            $"""UPDATE paf."FilaPrioridades" SET "Letra" = 'A1', "NumeroInicial" = 2 WHERE "FilaId" = {c.Caixa} AND "PrioridadeId" = {c.Preferencial}""");
        await _tenant.ExecutarAsync(
            $"""UPDATE paf."FilaPrioridades" SET "NumeroInicial" = 12 WHERE "FilaId" = {c.Caixa} AND "PrioridadeId" = {c.Normal}""");
        using var totem = await _op.TotemAsync(c);
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var normal = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var preferencial = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        normal.Senha.ShouldBe("A12");
        preferencial.Senha.ShouldBe("A12");

        var fichas = await ReimprimirOkAsync(supervisor, "A12", await DataDaFichaAsync(c, normal), c.Caixa);

        fichas.ShouldBe([normal, preferencial], "as duas, em ordem de emissão, cada uma como foi impressa");
    }

    [Fact]
    public async Task Reimprimir_EntradaMalFormada_400_NuncaQuinhentos()
    {
        var c = await _op.CriarAsync();
        using var supervisor = await SupervisorAsync(c.UnidadeId);
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fila = c.Caixa.ToString();
        var longa = "A" + new string('1', ConsultarFichaQueryValidator.SenhaMaxLength);

        foreach (var consulta in new[]
                 {
                     $"?data={hoje}&filaId={fila}", $"?senha=&data={hoje}&filaId={fila}", $"?senha=%20%20&data={hoje}&filaId={fila}",
                     $"?senha=AB&data={hoje}&filaId={fila}", $"?senha={longa}&data={hoje}&filaId={fila}",
                     $"?senha=A1&filaId={fila}", $"?senha=A1&data=&filaId={fila}", $"?senha=A1&data=abc&filaId={fila}",
                     $"?senha=A1&data=2026-13-45&filaId={fila}", $"?senha=A1&data=15/10/2026&filaId={fila}",
                     $"?senha=A1&data={hoje}", $"?senha=A1&data={hoje}&filaId=", $"?senha=A1&data={hoje}&filaId=caixa",
                     $"?senha=A1&data={hoje}&filaId={Guid.Empty}",
                 })
        {
            using var resposta = await supervisor.GetAsync(Rota + consulta);
            resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{consulta}: {await resposta.Content.ReadAsStringAsync()}");
        }
    }

    [Fact]
    public async Task Reimprimir_SemFichasDoDiaView_403()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var atendente = await _op.AtendenteAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);

        using var resposta = await atendente.GetAsync(Url(ficha.Senha, await DataDaFichaAsync(c, ficha), c.Caixa));

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Atendimentos.Operar não inclui a reimpressão");
    }

    /// <summary>A person of the unidade with FichasDoDia.View only, logged in, no guichê.</summary>
    private async Task<HttpClient> SupervisorAsync(Guid unidadeId)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.FichasDoDia.View);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private async Task<DateOnly> DataDaFichaAsync(CenarioOperacao c, FichaEmitidaDto ficha) =>
        (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).DataOperacional;

    private static string Url(string senha, DateOnly data, Guid filaId) =>
        $"{Rota}?senha={Uri.EscapeDataString(senha)}&data={data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&filaId={filaId}";

    private static async Task<List<FichaEmitidaDto>> ReimprimirOkAsync(HttpClient cliente, string senha, DateOnly data, Guid filaId)
    {
        using var resposta = await cliente.GetAsync(Url(senha, data, filaId));
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<List<FichaEmitidaDto>>())!;
    }

    private static async Task NaoEncontradaAsync(HttpClient cliente, string senha, DateOnly data, Guid filaId)
    {
        using var resposta = await cliente.GetAsync(Url(senha, data, filaId));
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{senha} {data} {filaId}: {corpo}");
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(ReimpressaoFichaQueryHandler.MensagemNaoEncontrada);
    }
}
