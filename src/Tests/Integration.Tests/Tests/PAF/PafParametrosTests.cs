using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Parametros.SetParametroEmpresa;
using FSH.Modules.PAF.Parametros;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// RF06 with inheritance (C10): no row = factory default, the empresa default reaches every unidade, a unidade
/// override wins until its DELETE, the empresa level is the company admin's alone (RF23), writes are one row per key
/// and level under concurrency, and the readers (painel size, ficha print, totem order, guichê hotkeys) follow.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafParametrosTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private static readonly string[] TodasAsPermissoes =
        [PafPermissions.Parametros.View, PafPermissions.Parametros.UpdateUnidade, PafPermissions.Parametros.UpdateEmpresa];

    private static readonly string Quantidade = CatalogoParametros.QuantidadeChamadasPainel.Chave;

    private readonly PafTenantCliente _tenant = new(factory, "paf-par");

    public Task InitializeAsync() => _tenant.InicializarAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SemLinha_TodasAsChavesVemDeFabrica_ComOsPadroesDoRf06()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var lista = await ListaAsync(u1.UnidadeId);

        var dto = await ListarAsync(lista);

        dto.UnidadeId.ShouldBe(u1.UnidadeId);
        dto.AlteraUnidade.ShouldBeTrue();
        dto.AlteraEmpresa.ShouldBeFalse("a permissão sozinha não faz do escopo de lista um administrador da empresa");
        dto.Itens.Select(i => i.Chave).ShouldBe(CatalogoParametros.Todos.Select(p => p.Chave));
        dto.Itens.ShouldAllBe(i => i.Origem == nameof(OrigemParametro.Fabrica) && i.Valor == i.Padrao && i.ValorEmpresa == null && i.ValorUnidade == null);
        Item(dto, Quantidade).Valor.ShouldBe("10");
        Item(dto, CatalogoParametros.ImprimirQrFicha.Chave).Valor.ShouldBe("false");
        Item(dto, CatalogoParametros.OrdenacaoFilas.Chave).Valor.ShouldBe("ordem");
        CatalogoParametros.Teclas.Select(t => Item(dto, t.Chave).Valor).ShouldBe(["F7", "F8", "F4", "F6", "F3", "F2"]);
    }

    [Fact]
    public async Task Empresa6_ValeParaU1EU2_U1Sobrescreve4_DeleteVoltaA6()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresa = await EmpresaAsync(u1.UnidadeId);
        using var emU1 = await ListaAsync(u1.UnidadeId);
        using var emU2 = await ListaAsync(u2.UnidadeId);

        (await ListarAsync(empresa)).AlteraEmpresa.ShouldBeTrue();
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/empresa/{Quantidade}", "6");
        Item(await ListarAsync(emU1), Quantidade).ShouldSatisfyAllConditions(
            i => i.Valor.ShouldBe("6"), i => i.Origem.ShouldBe(nameof(OrigemParametro.Empresa)));
        Item(await ListarAsync(emU2), Quantidade).Valor.ShouldBe("6");

        await SemConteudoAsync(emU1, HttpMethod.Put, $"/parametros/unidade/{Quantidade}", "4");
        Item(await ListarAsync(emU1), Quantidade).ShouldSatisfyAllConditions(
            i => i.Valor.ShouldBe("4"), i => i.Origem.ShouldBe(nameof(OrigemParametro.Unidade)),
            i => i.ValorEmpresa.ShouldBe("6"), i => i.ValorUnidade.ShouldBe("4"));
        Item(await ListarAsync(emU2), Quantidade).Valor.ShouldBe("6", "a sobrescrita de U1 não alcança U2");

        await SemConteudoAsync(emU1, HttpMethod.Delete, $"/parametros/unidade/{Quantidade}", null);
        Item(await ListarAsync(emU1), Quantidade).ShouldSatisfyAllConditions(
            i => i.Valor.ShouldBe("6"), i => i.Origem.ShouldBe(nameof(OrigemParametro.Empresa)), i => i.ValorUnidade.ShouldBeNull());

        // DELETE of an override that no longer exists is a no-op, not an error.
        await SemConteudoAsync(emU1, HttpMethod.Delete, $"/parametros/unidade/{Quantidade}", null);
    }

    [Fact]
    public async Task ListaU1_GravandoEmpresa403_EU2ForaDoEscopo403()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var lista = await ListaAsync(u1.UnidadeId);

        using (var empresa = await EnviarAsync(lista, HttpMethod.Put, $"/parametros/empresa/{Quantidade}", "6", null))
        {
            var corpo = await empresa.Content.ReadAsStringAsync();
            empresa.StatusCode.ShouldBe(HttpStatusCode.Forbidden, corpo);
            PafSessaoLicencaTests.Detalhe(corpo).ShouldBe(SetParametroEmpresaCommandHandler.MensagemSoAdministradorDaEmpresa);
        }

        using (var troca = await PafApi.EnviarAsync(lista, HttpMethod.Post, "/sessao/unidade-corrente", null, new SetUnidadeCorrenteCommand(u2.UnidadeId)))
        {
            troca.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await troca.Content.ReadAsStringAsync());
        }

        // The session fixes the unidade: a header naming U2 is ignored and the write lands in U1, never in U2.
        using (var comCabecalho = await EnviarAsync(lista, HttpMethod.Put, $"/parametros/unidade/{Quantidade}", "3", u2.UnidadeId))
        {
            comCabecalho.StatusCode.ShouldBe(HttpStatusCode.NoContent, await comCabecalho.Content.ReadAsStringAsync());
        }

        var linhas = await LinhasAsync(Quantidade);
        linhas.ShouldHaveSingleItem().UnidadeId.ShouldBe(u1.UnidadeId);
    }

    [Fact]
    public async Task ValoresInvalidos400_ChaveDesconhecida404_NadaGravado()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresa = await EmpresaAsync(u1.UnidadeId);
        (string Chave, string Valor)[] invalidos =
        [
            (Quantidade, "0"), (Quantidade, "31"), (Quantidade, "abc"), (Quantidade, "-1"), (Quantidade, ""),
            (CatalogoParametros.ImprimirQrFicha.Chave, "sim"),
            (CatalogoParametros.TeclaEncerrar.Chave, "A"), (CatalogoParametros.TeclaEncerrar.Chave, "F13"), (CatalogoParametros.TeclaEncerrar.Chave, "F0"),
            (CatalogoParametros.OrdenacaoFilas.Chave, "prioridade"),
            (CatalogoParametros.TituloFichas.Chave, new string('x', CatalogoParametros.TamanhoMaximoTitulo + 1)),
            (CatalogoParametros.MensagemRodapeFicha.Chave, "linha\nquebrada"),
            (CatalogoParametros.FonteMinimaTotemPx.Chave, "0"),
        ];

        foreach (var (chave, valor) in invalidos)
        {
            foreach (var rota in new[] { $"/parametros/empresa/{chave}", $"/parametros/unidade/{chave}" })
            {
                using var resposta = await EnviarAsync(empresa, HttpMethod.Put, rota, valor, null);
                resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{rota} = '{valor}': {await resposta.Content.ReadAsStringAsync()}");
            }
        }

        using (var desconhecida = await EnviarAsync(empresa, HttpMethod.Put, "/parametros/unidade/diretorioMidias", "C:/midias", null))
        {
            desconhecida.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var semValor = await PafApi.EnviarAsync(empresa, HttpMethod.Put, $"/parametros/unidade/{Quantidade}", null, new { }))
        {
            semValor.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await semValor.Content.ReadAsStringAsync());
        }

        (await LinhasAsync(null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task VintePutsParalelosNaUnidade_UmaLinha_EDoisDeEmpresa_UmaLinhaComUnidadeNula()
    {
        var u1 = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresa = await EmpresaAsync(u1.UnidadeId);
        var titulo = CatalogoParametros.TituloFichas.Chave;

        var naUnidade = await Task.WhenAll(Enumerable.Range(1, 20)
            .Select(i => EnviarAsync(empresa, HttpMethod.Put, $"/parametros/unidade/{titulo}", $"Título {i}", null)));
        var daEmpresa = await Task.WhenAll(Enumerable.Range(1, 2)
            .Select(i => EnviarAsync(empresa, HttpMethod.Put, $"/parametros/empresa/{titulo}", $"Rede {i}", null)));
        foreach (var resposta in naUnidade.Concat(daEmpresa))
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.NoContent, await resposta.Content.ReadAsStringAsync());
            resposta.Dispose();
        }

        var linhas = await LinhasAsync(titulo);
        linhas.Count.ShouldBe(2);
        linhas.Single(l => l.UnidadeId == u1.UnidadeId).ShouldSatisfyAllConditions(
            l => l.Nivel.ShouldBe(NivelParametro.Unidade), l => l.Valor.ShouldStartWith("Título "));
        linhas.Single(l => l.UnidadeId == null).ShouldSatisfyAllConditions(
            l => l.Nivel.ShouldBe(NivelParametro.Empresa), l => l.Valor.ShouldStartWith("Rede "));
    }

    [Fact]
    public async Task Painel_DevolveExatamenteN_DaUnidade_EODeleteVoltaAoPadrao()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        using var totem = await operacao.TotemAsync(cenario);
        using var atendente = await operacao.AtendenteAsync(cenario, cenario.GuicheA);
        using var painel = _tenant.Cliente(await operacao.CredencialAsync(cenario.UnidadeId, cenario.Painel, PafPapeisEstacao.Painel));
        using var gestor = await ListaAsync(cenario.UnidadeId);

        for (var i = 0; i < 5; i++)
        {
            await PafOperacao.EmitirOkAsync(totem, cenario.Caixa, cenario.Normal);
        }

        // Past the five-second guard of a fresh ficha (seção 4), so the guichê can call them now.
        await operacao.EnvelhecerFichasAsync(cenario.UnidadeId);
        for (var i = 0; i < 5; i++)
        {
            var chamada = await PafOperacao.ChamarOkAsync(atendente);
            await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "iniciar");
            await PafOperacao.AgirOkAsync(atendente, chamada.AtendimentoId, "encerrar");
        }

        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{Quantidade}", "3");
        var estado = (await painel.GetFromJsonAsync<EstadoPainelDto>(PafOperacao.RotaEstadoPainel))!;
        estado.QuantidadeChamadas.ShouldBe(3);
        estado.Chamadas.Count.ShouldBe(3);

        await SemConteudoAsync(gestor, HttpMethod.Delete, $"/parametros/unidade/{Quantidade}", null);
        estado = (await painel.GetFromJsonAsync<EstadoPainelDto>(PafOperacao.RotaEstadoPainel))!;
        estado.QuantidadeChamadas.ShouldBe(CatalogoParametros.QuantidadeChamadasPainel.Padrao);
        estado.Chamadas.Count.ShouldBe(5);
    }

    [Fact]
    public async Task DoisTenants_PadroesDeEmpresaIsolados()
    {
        var outro = new PafTenantCliente(factory, "paf-par2");
        await outro.InicializarAsync();
        var uA = await _tenant.CriarUnidadeAsync(limiteSessoes: 5);
        var uB = await outro.CriarUnidadeAsync(limiteSessoes: 5);
        using var empresaA = await EmpresaAsync(uA.UnidadeId);
        var usuarioB = await outro.CriarUsuarioEmpresaAsync(uB.UnidadeId);
        await outro.DarPapelAsync(usuarioB.UserId, TodasAsPermissoes);
        using var empresaB = outro.Cliente((await outro.EntrarAsync(usuarioB)).AccessToken);

        await SemConteudoAsync(empresaA, HttpMethod.Put, $"/parametros/empresa/{Quantidade}", "7");
        Item(await ListarAsync(empresaB), Quantidade).Origem.ShouldBe(nameof(OrigemParametro.Fabrica));

        await SemConteudoAsync(empresaB, HttpMethod.Put, $"/parametros/empresa/{Quantidade}", "8");
        Item(await ListarAsync(empresaA), Quantidade).Valor.ShouldBe("7");
        Item(await ListarAsync(empresaB), Quantidade).Valor.ShouldBe("8");
    }

    [Fact]
    public async Task Teclas_ConflitoNaUnidade_NoPadraoDaEmpresa_ENoDelete400_EOGuicheLeAsEfetivas()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        using var empresa = await EmpresaAsync(cenario.UnidadeId);
        using var atendente = await operacao.AtendenteAsync(cenario, cenario.GuicheA);
        var chamar = CatalogoParametros.TeclaChamarProxima.Chave;
        var encerrar = CatalogoParametros.TeclaEncerrar.Chave;

        // F7 is the default of "chamar o próximo".
        await RecusadoPorTeclaAsync(empresa, $"/parametros/unidade/{encerrar}", "F7");
        await RecusadoPorTeclaAsync(empresa, $"/parametros/empresa/{encerrar}", "f7");

        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/unidade/{chamar}", "F9");
        // A new empresa default may not collide with an override of any unidade.
        await RecusadoPorTeclaAsync(empresa, $"/parametros/empresa/{encerrar}", "F9");

        // Swap in the unidade: encerrar takes F7 now that chamar left it; dropping chamar's override would bring F7 back twice.
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/unidade/{encerrar}", "F7");
        await RecusadoPorTeclaAsync(empresa, $"/parametros/unidade/{chamar}", null);

        var teclas = await PafApi.OkAsync<TeclasGuicheDto>(atendente, HttpMethod.Get, "/guiche/teclas-atalho", null, null);
        teclas.ShouldBe(new TeclasGuicheDto("F9", "F8", "F7", "F6", "F3", "F2"));
    }

    [Fact]
    public async Task Totem_TituloERodapeDaUnidade_DeleteVoltaAoDaEmpresa_QrLidoSemMudarAFicha()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        using var totem = await operacao.TotemAsync(cenario);
        using var empresa = await EmpresaAsync(cenario.UnidadeId);
        var titulo = CatalogoParametros.TituloFichas.Chave;
        var rodape = CatalogoParametros.MensagemRodapeFicha.Chave;

        var semNada = await PafOperacao.EmitirOkAsync(totem, cenario.Caixa, cenario.Normal);
        (semNada.Titulo, semNada.Rodape, semNada.ImprimirQr).ShouldBe((string.Empty, string.Empty, false));

        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/empresa/{titulo}", "Rede Exemplo");
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/empresa/{rodape}", "Obrigado pela visita");
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/unidade/{titulo}", "  Loja Centro  ");
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/unidade/{rodape}", "Aguarde ser chamado");
        await SemConteudoAsync(empresa, HttpMethod.Put, $"/parametros/unidade/{CatalogoParametros.ImprimirQrFicha.Chave}", "TRUE");

        var naUnidade = await PafOperacao.EmitirOkAsync(totem, cenario.Caixa, cenario.Normal);
        (naUnidade.Titulo, naUnidade.Rodape, naUnidade.ImprimirQr).ShouldBe(("Loja Centro", "Aguarde ser chamado", true));
        (await LinhasAsync(CatalogoParametros.ImprimirQrFicha.Chave)).ShouldHaveSingleItem().Valor.ShouldBe("true", "gravado na forma canônica");

        await SemConteudoAsync(empresa, HttpMethod.Delete, $"/parametros/unidade/{titulo}", null);
        await SemConteudoAsync(empresa, HttpMethod.Delete, $"/parametros/unidade/{rodape}", null);
        var daEmpresa = await PafOperacao.EmitirOkAsync(totem, cenario.Caixa, cenario.Normal);
        (daEmpresa.Titulo, daEmpresa.Rodape).ShouldBe(("Rede Exemplo", "Obrigado pela visita"));
    }

    [Fact]
    public async Task OrdenacaoPorNome_MudaAOrdemDosBotoesDoTotem()
    {
        var operacao = new PafOperacao(factory, _tenant);
        var cenario = await operacao.CriarAsync();
        using var totem = await operacao.TotemAsync(cenario);
        using var gestor = await ListaAsync(cenario.UnidadeId);
        // Station order puts Protocolo before Caixa, so the two orderings disagree.
        await _tenant.ExecutarAsync($"""
            UPDATE paf."EstacaoFilas" SET "Ordem" = 5
            WHERE "EstacaoId" = {cenario.Totem} AND "FilaId" = {cenario.Caixa} AND "TenantId" = {_tenant.TenantId}
            """);

        (await FilasDosBotoesAsync(totem)).ShouldBe(["Protocolo", "Caixa"]);

        await SemConteudoAsync(gestor, HttpMethod.Put, $"/parametros/unidade/{CatalogoParametros.OrdenacaoFilas.Chave}", "nome");
        (await FilasDosBotoesAsync(totem)).ShouldBe(["Caixa", "Protocolo"]);

        await SemConteudoAsync(gestor, HttpMethod.Delete, $"/parametros/unidade/{CatalogoParametros.OrdenacaoFilas.Chave}", null);
        (await FilasDosBotoesAsync(totem)).ShouldBe(["Protocolo", "Caixa"]);
    }

    private static async Task<List<string>> FilasDosBotoesAsync(HttpClient totem)
    {
        var configuracao = (await totem.GetFromJsonAsync<ConfiguracaoEstacaoDto>("/api/v1/paf/estacao/emissao/configuracao"))!;
        return configuracao.Botoes.OrderBy(b => b.Ordem).Select(b => b.Fila).Distinct().ToList();
    }

    private async Task<HttpClient> EmpresaAsync(Guid principal)
    {
        var usuario = await _tenant.CriarUsuarioEmpresaAsync(principal);
        await _tenant.DarPapelAsync(usuario.UserId, TodasAsPermissoes);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private async Task<HttpClient> ListaAsync(params Guid[] unidades)
    {
        var usuario = await _tenant.CriarUsuarioAsync(unidades);
        await _tenant.DarPapelAsync(usuario.UserId, TodasAsPermissoes);
        return _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
    }

    private static Task<ParametrosDaUnidadeDto> ListarAsync(HttpClient cliente) =>
        PafApi.OkAsync<ParametrosDaUnidadeDto>(cliente, HttpMethod.Get, "/parametros", null, null);

    private static ParametroDto Item(ParametrosDaUnidadeDto dto, string chave) => dto.Itens.Single(i => i.Chave == chave);

    private static Task<HttpResponseMessage> EnviarAsync(HttpClient cliente, HttpMethod metodo, string rota, string? valor, Guid? unidade) =>
        PafApi.EnviarAsync(cliente, metodo, rota, unidade, valor is null ? null : new ParametroValorRequest(valor));

    private static async Task SemConteudoAsync(HttpClient cliente, HttpMethod metodo, string rota, string? valor)
    {
        using var resposta = await EnviarAsync(cliente, metodo, rota, valor, null);
        resposta.StatusCode.ShouldBe(HttpStatusCode.NoContent, $"{metodo} {rota}: {await resposta.Content.ReadAsStringAsync()}");
    }

    /// <summary>A null <paramref name="valor"/> sends the DELETE of the unidade override.</summary>
    private static async Task RecusadoPorTeclaAsync(HttpClient cliente, string rota, string? valor)
    {
        using var resposta = await EnviarAsync(cliente, valor is null ? HttpMethod.Delete : HttpMethod.Put, rota, valor, null);
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{rota}: {corpo}");
        corpo.ShouldContain("ficaria com duas ações do guichê");
    }

    /// <summary>Rows of the key (or every row) across the tenant's unidades: only the unit filter is turned off.</summary>
    private async Task<List<Parametro>> LinhasAsync(string? chave)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.Parametros.AsNoTracking()
            .IgnoreQueryFilters([PafQueryFilters.Unidade])
            .Where(p => chave == null || p.Chave == chave)
            .ToListAsync();
    }
}
