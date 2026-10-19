using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;
using FSH.Modules.PAF.Parametros;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF19 "atendimento VIP" (requisitos RF19, 8.1 l.917 and l.1030; ARQ l.144, l.202; D01, D02): the guichê calls a ficha
/// named by its printed senha, its date and the fila, out of the queue order and nothing else. The date only identifies
/// the ficha; callability follows the atendimento's eligibility date through the call's own predicate. A ficha in
/// progress on another guichê is refused naming it, with no write.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafVipTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaVip = "/api/v1/paf/guiche/vip";
    private const string RotaFilas = "/api/v1/paf/guiche/vip/filas";
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(15);

    private readonly PafTenantCliente _tenant = new(factory, "paf-vip");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    // ─── chamada ─────────────────────────────────────────────────────

    [Fact]
    public async Task Vip_FichaAtrasDeOutras_ChamadaDiretoComOsMarcosDoBanco()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        var primeira = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Preferencial);
        var segunda = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var vip = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var hoje = await DataDaFichaAsync(c, vip.FichaId);

        // Lower case and padding: the letter is stored upper case (LetraDaSenha), the attendant types what is printed.
        var chamada = await VipOkAsync(guicheA, " a2 ", hoje, c.Caixa);

        chamada.AtendimentoId.ShouldBe(vip.AtendimentoId);
        chamada.Senha.ShouldBe("A2");
        chamada.Versao.ShouldBe(1);
        var atendimento = await _op.AtendimentoAsync(c, vip.AtendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Chamado);
        atendimento.EstacaoId.ShouldBe(c.GuicheA);
        atendimento.ChamadoEm.ShouldNotBeNull();
        await ShouldBeIntactoAsync(c, primeira.AtendimentoId);
        await ShouldBeIntactoAsync(c, segunda.AtendimentoId);
        (await _op.ContarAsync(c, db => db.Filas.Where(f => f.Id == c.Caixa && f.UltimaChamadaEm != null))).ShouldBe(1);
    }

    [Fact]
    public async Task Vip_FichaDeOntemComAtendimentoElegivelHoje_Chamada()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var hoje = await DataDaFichaAsync(c, ficha.FichaId);
        // The ficha was emitted yesterday (DataOperacional follows CriadoEm); its atendimento became eligible today, as
        // one forwarded after midnight. "Somente fichas da data atual" is on by default.
        await _tenant.ExecutarAsync($"""UPDATE paf."Fichas" SET "CriadoEm" = "CriadoEm" - interval '1 day' WHERE "Id" = {ficha.FichaId}""");
        var ontem = await DataDaFichaAsync(c, ficha.FichaId);
        ontem.ShouldBe(hoje.AddDays(-1));

        using (var dataDeHoje = await VipAsync(guicheA, ficha.Senha, hoje, c.Caixa))
        {
            await ShouldBeNaoEncontradaAsync(dataDeHoje);
        }

        var chamada = await VipOkAsync(guicheA, ficha.Senha, ontem, c.Caixa);

        chamada.AtendimentoId.ShouldBe(ficha.AtendimentoId);
        (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Vip_FichaDeHojeComElegibilidadeDeOntem_SegueOParametro(bool somenteDataAtual)
    {
        var c = await _op.CriarAsync();
        await GravarParametroAsync(c.UnidadeId, somenteDataAtual);
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var hoje = await DataDaFichaAsync(c, ficha.FichaId);
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "Id" = {ficha.AtendimentoId}""");

        using var resposta = await VipAsync(guicheA, ficha.Senha, hoje, c.Caixa);

        if (somenteDataAtual)
        {
            await ShouldBeConflitoAsync(resposta, ReabilitarAtendimentoDaFichaCommandHandler.MensagemForaDoDia);
            await ShouldBeIntactoAsync(c, ficha.AtendimentoId);
        }
        else
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            (await _op.AtendimentoAsync(c, ficha.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Chamado);
        }
    }

    // ─── recusas ─────────────────────────────────────────────────────

    [Fact]
    public async Task Vip_FichaEmCursoNoutroGuiche_409ComONomeSemEscrita()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        (await PafOperacao.ChamarOkAsync(guicheB)).AtendimentoId.ShouldBe(ficha.AtendimentoId);
        var hoje = await DataDaFichaAsync(c, ficha.FichaId);

        using var resposta = await VipAsync(guicheA, ficha.Senha, hoje, c.Caixa);

        await ShouldBeConflitoAsync(resposta, ChamarFichaVipCommandHandler.MensagemEmCurso("Guichê 2"));
        var atendimento = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
        atendimento.EstacaoId.ShouldBe(c.GuicheB);
        atendimento.Versao.ShouldBe(1, "o VIP não escreveu");
        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.EstacaoId == c.GuicheA))).ShouldBe(0);
    }

    /// <summary>
    /// Forwarded to two filas, the ficha waits in Retaguarda while guichê 2 serves it from Protocolo: the VIP finds the
    /// waiting atendimento, and the exclusivity check under the ficha lock refuses it naming guichê 2.
    /// </summary>
    [Fact]
    public async Task Vip_AtendimentoAguardandoComIrmaoEmCursoNoutraFila_409ComONome()
    {
        var c = await _op.CriarAsync();
        // Guichê A also serves Retaguarda, so forwarding there has coverage (RF17).
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            db.EstacaoFilas.Add(EstacaoFila.Criar(c.UnidadeId, c.GuicheA, PapelEstacao.Guiche, c.Retaguarda, 1));
            await db.SaveChangesAsync();
        }

        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var origem = await PafOperacao.ChamarOkAsync(guicheA);
        await PafOperacao.AgirOkAsync(guicheA, origem.AtendimentoId, "iniciar");
        using (var encaminhar = await guicheA.PostAsJsonAsync(PafOperacao.Rota(origem.AtendimentoId, "encaminhar"),
                   new EncaminharAtendimentoRequest([c.Protocolo, c.Retaguarda], c.Normal)))
        {
            encaminhar.StatusCode.ShouldBe(HttpStatusCode.OK, await encaminhar.Content.ReadAsStringAsync());
        }

        var protocolo = await PafOperacao.ChamarOkAsync(guicheB);
        protocolo.FichaId.ShouldBe(ficha.FichaId);
        var hoje = await DataDaFichaAsync(c, ficha.FichaId);

        using var resposta = await VipAsync(guicheA, ficha.Senha, hoje, c.Retaguarda);

        await ShouldBeConflitoAsync(resposta, ChamarFichaVipCommandHandler.MensagemEmCurso("Guichê 2"));
        var retaguarda = await _op.ContarAsync(c, db => db.Atendimentos.Where(a =>
            a.FichaId == ficha.FichaId && a.FilaId == c.Retaguarda && a.Estado == EstadoAtendimento.Aguardando && a.Versao == 0));
        retaguarda.ShouldBe(1, "o atendimento de Retaguarda continua aguardando, sem escrita");
    }

    [Fact]
    public async Task Vip_SenhaFilaDataOuUnidadeErradas_404SemEscrita()
    {
        var a = await _op.CriarAsync();
        var b = await _op.CriarAsync();
        using var totemA = await _op.TotemAsync(a);
        using var totemB = await _op.TotemAsync(b);
        using var guicheA = await AtendenteVipAsync(a, a.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totemA, a.Caixa, a.Normal);
        await PafOperacao.EmitirOkAsync(totemB, b.Caixa, b.Normal);
        var deB = await PafOperacao.EmitirOkAsync(totemB, b.Caixa, b.Normal);
        await _op.EnvelhecerFichasAsync(a.UnidadeId);
        await _op.EnvelhecerFichasAsync(b.UnidadeId);
        var hoje = await DataDaFichaAsync(a, ficha.FichaId);

        var erradas = new (string Senha, DateOnly Data, Guid Fila)[]
        {
            ("A9", hoje, a.Caixa),
            ("AP1", hoje, a.Caixa),
            ("A01", hoje, a.Caixa),
            ("A", hoje, a.Caixa),
            ("1", hoje, a.Caixa),
            (ficha.Senha, hoje, a.Protocolo),
            (ficha.Senha, hoje, Guid.NewGuid()),
            (ficha.Senha, hoje.AddDays(-1), a.Caixa),
            (ficha.Senha, hoje.AddDays(1), a.Caixa),
            // Unit B's A2 exists only there (unit A emitted just A1); its own fila id does not help: the unit filter hides it.
            (deB.Senha, hoje, b.Caixa),
        };
        foreach (var (senha, data, fila) in erradas)
        {
            using var resposta = await VipAsync(guicheA, senha, data, fila);
            await ShouldBeNaoEncontradaAsync(resposta);
        }

        await ShouldBeIntactoAsync(a, ficha.AtendimentoId);
        await ShouldBeIntactoAsync(b, deB.AtendimentoId);
    }

    [Fact]
    public async Task Vip_FichaQueAChamadaNaoChamaria_Recusa409SemEscrita()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        var foraDoPapel = await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var recente = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var hoje = await DataDaFichaAsync(c, recente.FichaId);

        foreach (var (ficha, fila) in new[] { (foraDoPapel, c.Protocolo), (recente, c.Caixa) })
        {
            using var resposta = await VipAsync(guicheA, ficha.Senha, hoje, fila);
            await ShouldBeConflitoAsync(resposta, ChamarFichaEscolhidaCommandHandler.MensagemNaoElegivel);
            await ShouldBeIntactoAsync(c, ficha.AtendimentoId);
        }
    }

    [Fact]
    public async Task Vip_ComAtendimentoEmCursoNoGuiche_Recusa409()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var vip = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var hoje = await DataDaFichaAsync(c, vip.FichaId);
        var emCurso = await PafOperacao.ChamarOkAsync(guicheA);

        using (var comChamado = await VipAsync(guicheA, vip.Senha, hoje, c.Caixa))
        {
            await ShouldBeConflitoAsync(comChamado, ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        await PafOperacao.AgirOkAsync(guicheA, emCurso.AtendimentoId, "iniciar");
        using (var emAtendimento = await VipAsync(guicheA, vip.Senha, hoje, c.Caixa))
        {
            await ShouldBeConflitoAsync(emAtendimento, ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        await ShouldBeIntactoAsync(c, vip.AtendimentoId);
        await PafOperacao.AgirOkAsync(guicheA, emCurso.AtendimentoId, "encerrar");
        (await VipOkAsync(guicheA, vip.Senha, hoje, c.Caixa)).AtendimentoId.ShouldBe(vip.AtendimentoId);
    }

    [Fact]
    public async Task Vip_SemAPermissao_403EPedidoInvalido_400()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var semVip = await _op.AtendenteAsync(c, c.GuicheA);
        using var comVip = await AtendenteVipAsync(c, c.GuicheB);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var hoje = await DataDaFichaAsync(c, ficha.FichaId);

        using (var chamada = await VipAsync(semVip, ficha.Senha, hoje, c.Caixa))
        {
            chamada.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await chamada.Content.ReadAsStringAsync());
        }

        using (var filas = await semVip.GetAsync(RotaFilas))
        {
            filas.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await filas.Content.ReadAsStringAsync());
        }

        foreach (var corpo in new object[]
                 {
                     new { senha = "", data = hoje, filaId = c.Caixa },
                     new { senha = "ABCDE12345678901", data = hoje, filaId = c.Caixa },
                     new { senha = ficha.Senha, data = default(DateOnly), filaId = c.Caixa },
                     new { senha = ficha.Senha, data = hoje, filaId = Guid.Empty },
                 })
        {
            using var invalido = await comVip.PostAsJsonAsync(RotaVip, corpo);
            invalido.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await invalido.Content.ReadAsStringAsync());
        }

        await ShouldBeIntactoAsync(c, ficha.AtendimentoId);
    }

    [Fact]
    public async Task Filas_SaoAsDoPapelDeGuicheNaOrdemDaEstacao()
    {
        var c = await _op.CriarAsync();
        using var guicheB = await AtendenteVipAsync(c, c.GuicheB);

        using var resposta = await guicheB.GetAsync(RotaFilas);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        var filas = (await resposta.Content.ReadFromJsonAsync<List<FilaVipDto>>())!;
        filas.Select(f => f.Id).ShouldBe([c.Protocolo, c.Caixa]);
        filas.Select(f => f.Nome).ShouldBe(["Protocolo", "Caixa"]);
    }

    // ─── concorrência ────────────────────────────────────────────────

    /// <summary>Plan F11: VIP against chamar-próximo on the same and only ficha. Exactly one Chamado, whichever wins.</summary>
    [Fact]
    public async Task VipXChamarProximo_NaMesmaFicha_UmChamadoSo()
    {
        const int rodadas = 20;
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        for (var rodada = 0; rodada < rodadas; rodada++)
        {
            var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
            await _op.EnvelhecerFichasAsync(c.UnidadeId);
            var hoje = await DataDaFichaAsync(c, ficha.FichaId);
            var largada = new Largada(2);
            var vip = Task.Run(async () => { await largada.ChegarAsync(); return await VipAsync(guicheA, ficha.Senha, hoje, c.Caixa); });
            var proximo = Task.Run(async () => { await largada.ChegarAsync(); return await PafOperacao.ChamarAsync(guicheB); });
            using var chamadaVip = await vip.WaitAsync(Prazo);
            using var chamada = await proximo.WaitAsync(Prazo);

            var atendimento = await _op.AtendimentoAsync(c, ficha.AtendimentoId);
            atendimento.Estado.ShouldBe(EstadoAtendimento.Chamado);
            atendimento.Versao.ShouldBe(1, "só o vencedor escreveu");
            if (chamadaVip.StatusCode == HttpStatusCode.OK)
            {
                chamada.StatusCode.ShouldBe(HttpStatusCode.NotFound, await chamada.Content.ReadAsStringAsync());
                atendimento.EstacaoId.ShouldBe(c.GuicheA);
                await EncerrarAsync(guicheA, ficha.AtendimentoId);
            }
            else
            {
                await ShouldBeConflitoAsync(chamadaVip, ChamarFichaVipCommandHandler.MensagemEmCurso("Guichê 2"));
                chamada.StatusCode.ShouldBe(HttpStatusCode.OK, await chamada.Content.ReadAsStringAsync());
                atendimento.EstacaoId.ShouldBe(c.GuicheB);
                await EncerrarAsync(guicheB, ficha.AtendimentoId);
            }
        }

        (await _op.ContarAsync(c, db => db.Atendimentos.Where(a => a.Estado == EstadoAtendimento.Encerrado))).ShouldBe(rodadas);
    }

    // ─── apoio ───────────────────────────────────────────────────────

    /// <summary>A person of the unidade with Atendimentos.Vip (D01, not basic), logged in, with the guichê selected.</summary>
    private async Task<HttpClient> AtendenteVipAsync(CenarioOperacao c, Guid guiche)
    {
        var usuario = await _tenant.CriarUsuarioAsync(c.UnidadeId);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Atendimentos.Vip);
        var cliente = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        using var selecao = await cliente.PostAsJsonAsync("/api/v1/paf/sessao/guiche", new { estacaoId = guiche });
        selecao.StatusCode.ShouldBe(HttpStatusCode.NoContent, await selecao.Content.ReadAsStringAsync());
        return cliente;
    }

    private static Task<HttpResponseMessage> VipAsync(HttpClient guiche, string senha, DateOnly data, Guid filaId) =>
        guiche.PostAsJsonAsync(RotaVip, new ChamarFichaVipCommand(senha, data, filaId));

    private static async Task<ChamadaDto> VipOkAsync(HttpClient guiche, string senha, DateOnly data, Guid filaId)
    {
        using var resposta = await VipAsync(guiche, senha, data, filaId);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<ChamadaDto>())!;
    }

    private async Task<DateOnly> DataDaFichaAsync(CenarioOperacao c, Guid fichaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        return await db.Fichas.AsNoTracking().Where(f => f.Id == fichaId).Select(f => f.DataOperacional).SingleAsync();
    }

    private async Task GravarParametroAsync(Guid unidadeId, bool valor)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeId);
        db.Parametros.Add(Parametro.CriarDeUnidade(unidadeId, CatalogoParametros.SomenteFichasDaDataAtual.Chave, valor ? "true" : "false"));
        await db.SaveChangesAsync();
    }

    private static async Task EncerrarAsync(HttpClient guiche, Guid atendimentoId)
    {
        await PafOperacao.AgirOkAsync(guiche, atendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(guiche, atendimentoId, "encerrar");
    }

    private static async Task ShouldBeConflitoAsync(HttpResponseMessage resposta, string mensagem)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.Conflict, corpo);
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(mensagem);
    }

    private static async Task ShouldBeNaoEncontradaAsync(HttpResponseMessage resposta)
    {
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound, corpo);
        PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(ChamarFichaVipCommandHandler.MensagemNaoEncontrada);
    }

    private async Task ShouldBeIntactoAsync(CenarioOperacao c, Guid atendimentoId)
    {
        var atendimento = await _op.AtendimentoAsync(c, atendimentoId);
        atendimento.Estado.ShouldBe(EstadoAtendimento.Aguardando);
        atendimento.Versao.ShouldBe(0);
        atendimento.EstacaoId.ShouldBeNull();
    }
}
