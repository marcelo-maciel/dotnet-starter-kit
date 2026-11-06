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
/// RF19 "atendimento VIP" (requisitos RF19, 8.1 l.917 and l.1030; ARQ l.144, l.202; D01, D02): the guichê searches the
/// fichas by printed senha and date (a list, owner decision 19/10/2026), then calls the one picked in a fila, out of the
/// queue order and nothing else. The date only identifies the ficha; callability follows the atendimento's eligibility
/// date through the call's own predicate. A ficha in progress on another guichê is refused naming it, with no write.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafVipTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaVip = "/api/v1/paf/guiche/vip";
    private const string RotaFilas = "/api/v1/paf/guiche/vip/filas";
    private const string RotaFichas = "/api/v1/paf/guiche/vip/fichas";
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
        var encontrada = (await BuscarOkAsync(guicheA, " a2 ", hoje)).ShouldHaveSingleItem();
        encontrada.FichaId.ShouldBe(vip.FichaId);
        encontrada.Senha.ShouldBe("A2");
        encontrada.Fila.ShouldBe("Caixa");
        encontrada.Prioridade.ShouldBe("Normal");
        var chamada = await VipOkAsync(guicheA, encontrada.FichaId, c.Caixa);

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

        (await BuscarOkAsync(guicheA, ficha.Senha, hoje)).ShouldBeEmpty();
        (await BuscarOkAsync(guicheA, ficha.Senha, ontem)).Select(f => f.FichaId).ShouldBe([ficha.FichaId]);

        var chamada = await VipOkAsync(guicheA, ficha.FichaId, c.Caixa);

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
        await _tenant.ExecutarAsync($"""UPDATE paf."Atendimentos" SET "ElegivelDesde" = "ElegivelDesde" - interval '1 day' WHERE "Id" = {ficha.AtendimentoId}""");

        using var resposta = await VipAsync(guicheA, ficha.FichaId, c.Caixa);

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

        using var resposta = await VipAsync(guicheA, ficha.FichaId, c.Caixa);

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

        using var resposta = await VipAsync(guicheA, ficha.FichaId, c.Retaguarda);

        await ShouldBeConflitoAsync(resposta, ChamarFichaVipCommandHandler.MensagemEmCurso("Guichê 2"));
        var retaguarda = await _op.ContarAsync(c, db => db.Atendimentos.Where(a =>
            a.FichaId == ficha.FichaId && a.FilaId == c.Retaguarda && a.Estado == EstadoAtendimento.Aguardando && a.Versao == 0));
        retaguarda.ShouldBe(1, "o atendimento de Retaguarda continua aguardando, sem escrita");
    }

    [Fact]
    public async Task Busca_SenhaOuDataErradas_ListaVazia()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var hoje = await DataDaFichaAsync(c, ficha.FichaId);

        var erradas = new (string Senha, DateOnly Data)[]
        {
            ("A9", hoje),
            ("AP1", hoje),
            ("A01", hoje),
            ("A", hoje),
            ("1", hoje),
            (ficha.Senha, hoje.AddDays(-1)),
            (ficha.Senha, hoje.AddDays(1)),
        };
        foreach (var (senha, data) in erradas)
        {
            (await BuscarOkAsync(guicheA, senha, data)).ShouldBeEmpty($"{senha} {data}");
        }

        await ShouldBeIntactoAsync(c, ficha.AtendimentoId);
    }

    /// <summary>
    /// Owner decision 19/10/2026: two fichas printing the same senha are both listed, in emission order, and the call
    /// takes the one picked. Protocolo/Normal prints "A1" + 2 and Caixa/Normal prints "A" + 12: both read A12.
    /// </summary>
    [Fact]
    public async Task Busca_DuasFichasComAMesmaSenha_ListaAsDuasEChamaAEscolhida()
    {
        var c = await _op.CriarAsync();
        await _tenant.ExecutarAsync(
            $"""UPDATE paf."FilaPrioridades" SET "Letra" = 'A1', "NumeroInicial" = 2 WHERE "FilaId" = {c.Protocolo} AND "PrioridadeId" = {c.Normal}""");
        await _tenant.ExecutarAsync(
            $"""UPDATE paf."FilaPrioridades" SET "NumeroInicial" = 12 WHERE "FilaId" = {c.Caixa} AND "PrioridadeId" = {c.Normal}""");
        using var totem = await _op.TotemAsync(c);
        using var guicheB = await AtendenteVipAsync(c, c.GuicheB);
        var caixa = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        var protocolo = await PafOperacao.EmitirOkAsync(totem, c.Protocolo, c.Normal);
        caixa.Senha.ShouldBe("A12");
        protocolo.Senha.ShouldBe("A12");
        await _op.EnvelhecerFichasAsync(c.UnidadeId);
        var hoje = await DataDaFichaAsync(c, caixa.FichaId);

        var fichas = await BuscarOkAsync(guicheB, "a12", hoje);

        fichas.Select(f => f.FichaId).ShouldBe([caixa.FichaId, protocolo.FichaId], "ordem de emissão");
        fichas.Select(f => f.Fila).ShouldBe(["Caixa", "Protocolo"]);
        fichas.ShouldAllBe(f => f.Senha == "A12");
        (await VipOkAsync(guicheB, protocolo.FichaId, c.Protocolo)).AtendimentoId.ShouldBe(protocolo.AtendimentoId);
        await ShouldBeIntactoAsync(c, caixa.AtendimentoId);
    }

    [Fact]
    public async Task Busca_OutraUnidadeEOutroTenant_NuncaAparecemNemSaoChamadas()
    {
        var a = await _op.CriarAsync();
        var b = await _op.CriarAsync();
        var outroTenant = new PafTenantCliente(factory, "paf-vip-b");
        await outroTenant.InicializarAsync();
        var opOutroTenant = new PafOperacao(factory, outroTenant);
        var x = await opOutroTenant.CriarAsync();
        using var totemA = await _op.TotemAsync(a);
        using var totemB = await _op.TotemAsync(b);
        using var totemX = await opOutroTenant.TotemAsync(x);
        using var guicheA = await AtendenteVipAsync(a, a.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totemA, a.Caixa, a.Normal);
        var deB = await PafOperacao.EmitirOkAsync(totemB, b.Caixa, b.Normal);
        var deX = await PafOperacao.EmitirOkAsync(totemX, x.Caixa, x.Normal);
        // The three print A1 on the same day: only the unidade's own one is listed.
        deB.Senha.ShouldBe(ficha.Senha);
        deX.Senha.ShouldBe(ficha.Senha);
        await _op.EnvelhecerFichasAsync(a.UnidadeId);
        await _op.EnvelhecerFichasAsync(b.UnidadeId);
        await opOutroTenant.EnvelhecerFichasAsync(x.UnidadeId);
        var hoje = await DataDaFichaAsync(a, ficha.FichaId);

        (await BuscarOkAsync(guicheA, ficha.Senha, hoje)).Select(f => f.FichaId).ShouldBe([ficha.FichaId]);

        // A leaked id with its own fila does not help: the unit and tenant filters hide it from the call too.
        foreach (var (fichaId, fila) in new[] { (deB.FichaId, b.Caixa), (deX.FichaId, x.Caixa) })
        {
            using var resposta = await VipAsync(guicheA, fichaId, fila);
            await ShouldBeNaoEncontradaAsync(resposta);
        }

        await ShouldBeIntactoAsync(b, deB.AtendimentoId);
        (await opOutroTenant.AtendimentoAsync(x, deX.AtendimentoId)).Estado.ShouldBe(EstadoAtendimento.Aguardando);
    }

    [Fact]
    public async Task Vip_FichaOuFilaErradas_404SemEscrita()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await AtendenteVipAsync(c, c.GuicheA);
        var ficha = await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        foreach (var (fichaId, fila) in new[] { (ficha.FichaId, c.Protocolo), (ficha.FichaId, Guid.NewGuid()), (Guid.NewGuid(), c.Caixa) })
        {
            using var resposta = await VipAsync(guicheA, fichaId, fila);
            await ShouldBeNaoEncontradaAsync(resposta);
        }

        await ShouldBeIntactoAsync(c, ficha.AtendimentoId);
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

        foreach (var (ficha, fila) in new[] { (foraDoPapel, c.Protocolo), (recente, c.Caixa) })
        {
            using var resposta = await VipAsync(guicheA, ficha.FichaId, fila);
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
        var emCurso = await PafOperacao.ChamarOkAsync(guicheA);

        using (var comChamado = await VipAsync(guicheA, vip.FichaId, c.Caixa))
        {
            await ShouldBeConflitoAsync(comChamado, ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        await PafOperacao.AgirOkAsync(guicheA, emCurso.AtendimentoId, "iniciar");
        using (var emAtendimento = await VipAsync(guicheA, vip.FichaId, c.Caixa))
        {
            await ShouldBeConflitoAsync(emAtendimento, ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        await ShouldBeIntactoAsync(c, vip.AtendimentoId);
        await PafOperacao.AgirOkAsync(guicheA, emCurso.AtendimentoId, "encerrar");
        (await VipOkAsync(guicheA, vip.FichaId, c.Caixa)).AtendimentoId.ShouldBe(vip.AtendimentoId);
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

        using (var chamada = await VipAsync(semVip, ficha.FichaId, c.Caixa))
        {
            chamada.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await chamada.Content.ReadAsStringAsync());
        }

        foreach (var rota in new[] { RotaFilas, RotaBusca(ficha.Senha, hoje) })
        {
            using var leitura = await semVip.GetAsync(rota);
            leitura.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{rota}: {await leitura.Content.ReadAsStringAsync()}");
        }

        foreach (var corpo in new object[] { new { fichaId = Guid.Empty, filaId = c.Caixa }, new { fichaId = ficha.FichaId, filaId = Guid.Empty } })
        {
            using var invalido = await comVip.PostAsJsonAsync(RotaVip, corpo);
            invalido.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await invalido.Content.ReadAsStringAsync());
        }

        foreach (var busca in new[] { $"{RotaFichas}?data={hoje:yyyy-MM-dd}", $"{RotaFichas}?senha=A1", RotaBusca("ABCDE12345678901", hoje), RotaBusca("  ", hoje) })
        {
            using var invalida = await comVip.GetAsync(busca);
            invalida.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{busca}: {await invalida.Content.ReadAsStringAsync()}");
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
            var largada = new Largada(2);
            var vip = Task.Run(async () => { await largada.ChegarAsync(); return await VipAsync(guicheA, ficha.FichaId, c.Caixa); });
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

    private static string RotaBusca(string senha, DateOnly data) =>
        $"{RotaFichas}?senha={Uri.EscapeDataString(senha)}&data={data:yyyy-MM-dd}";

    private static async Task<List<FichaVipDto>> BuscarOkAsync(HttpClient guiche, string senha, DateOnly data)
    {
        using var resposta = await guiche.GetAsync(RotaBusca(senha, data));
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return (await resposta.Content.ReadFromJsonAsync<List<FichaVipDto>>())!;
    }

    private static Task<HttpResponseMessage> VipAsync(HttpClient guiche, Guid fichaId, Guid filaId) =>
        guiche.PostAsJsonAsync(RotaVip, new ChamarFichaVipCommand(fichaId, filaId));

    private static async Task<ChamadaDto> VipOkAsync(HttpClient guiche, Guid fichaId, Guid filaId)
    {
        using var resposta = await VipAsync(guiche, fichaId, filaId);
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
