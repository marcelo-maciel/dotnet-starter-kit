using System.Net.Http.Headers;
using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estatistica;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Estatistica;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// E4, RNF13 and ADR 0003 over the ten RF07 types: the consolidated read lifts only the unit filter, so another
/// tenant's rows never come back, even rows that point at the caller's own unidade ids (only the tenant filter, or
/// the explicit "TenantId" of a raw statement, separates them). The scope, never the current unit, picks the
/// unidades: [U1,U2] gets both, [U1] never sees U2, an empty current unit still gets the scope, a root operator
/// without scope gets nothing, and a user taken out of a unidade stops seeing it on the next request.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafEstatisticaIsolamentoTests(FshWebApplicationFactory factory)
{
    private static readonly DateOnly Dia = new(2026, 3, 10);

    private static readonly TipoEstatistica[] Tipos = [.. Enumerable.Range(1, 10).Select(t => (TipoEstatistica)t)];

    // Encerrados per unidade: distinct counts, so a total tells which unidades were read.
    private const int RealizadosU1 = 1;
    private const int RealizadosU2 = 2;
    private const int RealizadosU3 = 4;

    private readonly PafApi _api = new(factory);

    [Fact]
    public async Task OutroTenantComAsMesmasUnidadeIds_NuncaApareceEmNenhumTipo()
    {
        var c = await MontarAsync("paf-e4-tenant");

        var antes = new Dictionary<TipoEstatistica, string>();
        foreach (var tipo in Tipos)
        {
            var corpo = await CorpoOkAsync(c.ComView, tipo, string.Empty);
            UnidadesDasLinhas(Ler(corpo)).ShouldBe([c.U1.UnidadeId, c.U2.UnidadeId], ignoreOrder: true, $"tipo {tipo}: {corpo}");
            corpo.ShouldNotContain(c.U3.UnidadeId.ToString(), Case.Insensitive, $"tipo {tipo}");
            antes[tipo] = corpo;
        }

        Numeros(TotalGeral(Ler(antes[TipoEstatistica.AtendimentosPorTempoDeEspera])))[2].ShouldBe(RealizadosU1 + RealizadosU2);

        // Tenant B's cadastro and operation sit on A's U1 and U2 ids on purpose, same day, every state.
        var marca = $"VAZOU-{Guid.NewGuid():N}";
        var tenantB = TenantFalso();
        foreach (var unidade in new[] { c.U1.UnidadeId, c.U2.UnidadeId })
        {
            using var scopeB = Escopo(tenantB);
            await PlantarAsync(scopeB, unidade, marca, Guid.Parse(c.Usuario.UserId), 3);
        }

        (await ContarAtendimentosAsync(tenantB, [c.U1.UnidadeId, c.U2.UnidadeId])).ShouldBe(10, "tenant B rows must exist, or the test proves nothing");

        foreach (var tipo in Tipos)
        {
            var corpo = await CorpoOkAsync(c.ComView, tipo, string.Empty);
            corpo.ShouldNotContain(marca, Case.Insensitive, $"tipo {tipo}");
            corpo.ShouldBe(antes[tipo], $"tipo {tipo}: tenant B changed the result");
        }
    }

    [Fact]
    public async Task ConsultaChamadaNoContextoDeOutroTenant_ComAsUnidadesDeA_DevolveVazio_SqlBrutoDoTipo5Incluso()
    {
        var c = await MontarAsync("paf-e4-barreira");

        // Positive control in A's context with no current unit: the filter's unidades are read, the current unit is
        // not needed (ADR 0006), so the empty result below is the tenant barrier and not an empty database.
        using (var scopeA = factory.Services.CreateScope())
        {
            c.Cliente.Contexto(scopeA, unidadeCorrente: null);
            foreach (var consulta in Consultas(scopeA))
            {
                var resultado = await consulta.ConsultarAsync(Filtro(consulta.Tipo, [c.U1.UnidadeId, c.U2.UnidadeId]), CancellationToken.None);
                UnidadesDasLinhas(resultado).ShouldBe([c.U1.UnidadeId, c.U2.UnidadeId], ignoreOrder: true, $"tipo {consulta.Tipo}");
            }
        }

        // The scope check is bypassed on purpose: another tenant's context asks for A's unidade ids. Only the tenant
        // filter (LINQ types) and the explicit "TenantId" of type 5's raw statement stand between them.
        using var scopeB = Escopo(TenantFalso());
        foreach (var consulta in Consultas(scopeB))
        {
            var resultado = await consulta.ConsultarAsync(Filtro(consulta.Tipo, [c.U1.UnidadeId, c.U2.UnidadeId]), CancellationToken.None);
            var texto = JsonSerializer.Serialize(resultado);
            resultado.Linhas.ShouldBeEmpty($"tipo {consulta.Tipo}: {texto}");
            resultado.Totais.SelectMany(t => t.Celulas).ShouldAllBe(x => x.Numero == null || x.Numero == 0m, $"tipo {consulta.Tipo}: {texto}");
            texto.ShouldNotContain(c.Marca, Case.Insensitive, $"tipo {consulta.Tipo}");
        }
    }

    [Fact]
    public async Task EscopoSoU1_NaoVeU2_PedirU2OuUnidadeForaDoEscopo403_EmTodosOsTipos()
    {
        var c = await MontarAsync("paf-e4-soU1");
        var soU1 = await c.Cliente.CriarUsuarioAsync(c.U1.UnidadeId);
        await c.Cliente.DarPapelAsync(soU1.UserId, PafPermissions.Estatisticas.View, PafPermissions.Estatisticas.ViewPorAtendente);
        using var cliente = c.Cliente.Cliente((await c.Cliente.EntrarAsync(soU1)).AccessToken);

        foreach (var tipo in Tipos)
        {
            var corpo = await CorpoOkAsync(cliente, tipo, string.Empty);
            UnidadesDasLinhas(Ler(corpo)).ShouldBe([c.U1.UnidadeId], $"tipo {tipo}: {corpo}");
            corpo.ShouldNotContain(c.U2.UnidadeId.ToString(), Case.Insensitive, $"tipo {tipo}");

            await Recusado403Async(cliente, tipo, $"&unidades={c.U2.UnidadeId}");
            await Recusado403Async(cliente, tipo, $"&unidades={c.U1.UnidadeId}&unidades={c.U2.UnidadeId}");
            await Recusado403Async(c.ComView, tipo, $"&unidades={c.U3.UnidadeId}");
        }

        Numeros(TotalGeral(await ConsultarAsync(cliente, TipoEstatistica.AtendimentosPorTempoDeEspera, string.Empty)))[2].ShouldBe(RealizadosU1);
    }

    [Fact]
    public async Task UsuarioTiradoDeUmaUnidade_DeixaDeVerNaRequisicaoSeguinte_MesmaSessao()
    {
        var c = await MontarAsync("paf-e4-removido");
        Numeros(TotalGeral(await ConsultarAsync(c.ComView, TipoEstatistica.AtendimentosPorTempoDeEspera, string.Empty)))[2]
            .ShouldBe(RealizadosU1 + RealizadosU2);

        // U1 is the session's current unit and stays in the scope, so the session survives the edit.
        var usuarioId = Guid.Parse(c.Usuario.UserId);
        using (var editar = await PafApi.EnviarAsync(c.Cliente.Admin, HttpMethod.Put, $"/usuarios/{usuarioId}/unidades", null,
            new SetUsuarioUnidadesCommand(usuarioId, [c.U1.UnidadeId], false)))
        {
            editar.StatusCode.ShouldBe(HttpStatusCode.OK, await editar.Content.ReadAsStringAsync());
        }

        foreach (var tipo in Tipos)
        {
            var corpo = await CorpoOkAsync(c.ComView, tipo, string.Empty);
            UnidadesDasLinhas(Ler(corpo)).ShouldBe([c.U1.UnidadeId], $"tipo {tipo}: {corpo}");
            await Recusado403Async(c.ComView, tipo, $"&unidades={c.U2.UnidadeId}");
        }

        Numeros(TotalGeral(await ConsultarAsync(c.ComView, TipoEstatistica.AtendimentosPorTempoDeEspera, string.Empty)))[2].ShouldBe(RealizadosU1);
    }

    [Fact]
    public async Task OperadorRaizSemEscopo_NuncaRecebeOTenantInteiro_ComEscopoESemUnidadeCorrenteRecebeOEscopo()
    {
        var c = await MontarAsync("paf-e4-raiz");
        using var raiz = await RaizNoTenantAsync(c.Cliente.TenantId);

        foreach (var tipo in Tipos)
        {
            using var resposta = await GetAsync(raiz, tipo, string.Empty);
            var corpo = await resposta.Content.ReadAsStringAsync();
            resposta.StatusCode.ShouldBeOneOf([HttpStatusCode.OK, HttpStatusCode.Forbidden], $"tipo {tipo}: {corpo}");
            foreach (var unidade in new[] { c.U1, c.U2, c.U3 })
            {
                corpo.ShouldNotContain(unidade.UnidadeId.ToString(), Case.Insensitive, $"tipo {tipo}");
            }

            corpo.ShouldNotContain(c.Marca, Case.Insensitive, $"tipo {tipo}");
            if (resposta.StatusCode == HttpStatusCode.OK)
            {
                var resultado = Ler(corpo);
                resultado.Linhas.ShouldBeEmpty($"tipo {tipo}: {corpo}");
                resultado.Totais.SelectMany(t => t.Celulas).ShouldAllBe(x => x.Numero == null || x.Numero == 0m, $"tipo {tipo}: {corpo}");
            }

            await Recusado403Async(raiz, tipo, $"&unidades={c.U1.UnidadeId}");
        }

        // Scope rows for the root operator in this tenant; no X-Paf-Unidade header, so the current unit is empty.
        var raizId = await _api.RootAdminIdAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = c.Cliente.Contexto(scope, unidadeCorrente: null);
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(raizId, c.U1.UnidadeId, 0));
            db.UsuariosUnidade.Add(UsuarioUnidade.Criar(raizId, c.U2.UnidadeId, 1));
            await db.SaveChangesAsync();
        }

        foreach (var tipo in Tipos)
        {
            var corpo = await CorpoOkAsync(raiz, tipo, string.Empty);
            UnidadesDasLinhas(Ler(corpo)).ShouldBe([c.U1.UnidadeId, c.U2.UnidadeId], ignoreOrder: true, $"tipo {tipo}: {corpo}");
        }

        Numeros(TotalGeral(await ConsultarAsync(raiz, TipoEstatistica.AtendimentosPorTempoDeEspera, string.Empty)))[2].ShouldBe(RealizadosU1 + RealizadosU2);
    }

    private sealed record Cenario(
        PafTenantCliente Cliente,
        UnidadeTeste U1,
        UnidadeTeste U2,
        UnidadeTeste U3,
        Integration.Tests.Tests.Sessions.SeededUser Usuario,
        HttpClient ComView,
        string Marca);

    /// <summary>
    /// Tenant A with U1 and U2 in the user's scope (U1 principal, so the session's current unit) and U3 outside it,
    /// each with encerrados, one cancelado and one aguardando on <see cref="Dia"/>. Every name carries a marker.
    /// </summary>
    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var cliente = new PafTenantCliente(factory, prefixo);
        await cliente.InicializarAsync();
        var u1 = await cliente.CriarUnidadeAsync(limiteSessoes: 5);
        var u2 = await cliente.CriarUnidadeAsync(limiteSessoes: 5);
        var u3 = await cliente.CriarUnidadeAsync(limiteSessoes: 5);
        var usuario = await cliente.CriarUsuarioAsync(u1.UnidadeId, u2.UnidadeId);
        await cliente.DarPapelAsync(usuario.UserId, PafPermissions.Estatisticas.View, PafPermissions.Estatisticas.ViewPorAtendente);

        var marca = $"DE-A-{Guid.NewGuid():N}";
        var usuarioId = Guid.Parse(usuario.UserId);
        foreach (var (unidade, realizados) in new[] { (u1, RealizadosU1), (u2, RealizadosU2), (u3, RealizadosU3) })
        {
            using var scope = factory.Services.CreateScope();
            cliente.Contexto(scope, unidadeCorrente: null);
            await PlantarAsync(scope, unidade.UnidadeId, marca, usuarioId, realizados);
        }

        var token = await cliente.EntrarAsync(usuario);
        return new Cenario(cliente, u1, u2, u3, usuario, cliente.Cliente(token.AccessToken), marca);
    }

    /// <summary>
    /// Fila, prioridade and guichê of <paramref name="unidadeId"/> in the scope's tenant, then encerrados, one
    /// cancelado and one aguardando of <see cref="Dia"/>, each with its ficha. The scope's tenant context must be set.
    /// </summary>
    private static async Task PlantarAsync(IServiceScope scope, Guid unidadeId, string marca, Guid usuarioId, int realizados)
    {
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();
        var fila = Fila.Criar(unidadeId, $"Fila {marca}", $"Fila {marca}", setorId: null);
        var prioridade = Prioridade.Criar(unidadeId, $"Prioridade {marca}", valor: 0);
        var guiche = Estacao.Criar(unidadeId, $"Guiche {marca}", TipoEstacao.Guiche);
        db.Filas.Add(fila);
        db.Prioridades.Add(prioridade);
        db.Estacoes.Add(guiche);
        await db.SaveChangesAsync();

        var estados = Enumerable.Repeat(EstadoAtendimento.Encerrado, realizados)
            .Append(EstadoAtendimento.Cancelado)
            .Append(EstadoAtendimento.Aguardando);
        foreach (var estado in estados)
        {
            await AtendimentoAsync(db, unidadeId, fila.Id, prioridade.Id, guiche.Id, usuarioId, estado);
        }
    }

    private static async Task AtendimentoAsync(
        PafDbContext db, Guid unidadeId, Guid fila, Guid prioridade, Guid guiche, Guid usuarioId, EstadoAtendimento estado)
    {
        var numero = Interlocked.Increment(ref _numero);
        var ficha = Ficha.Criar(unidadeId, fila, prioridade, "A", numero, CanalEmissao.Totem, guiche, Guid.NewGuid().ToString("N"));
        var atendimento = Atendimento.Criar(unidadeId, ficha.Id, fila, prioridade, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        await db.SaveChangesAsync();

        // Npgsql writes only UTC offsets to timestamptz; 10:00 in São Paulo (March has no DST).
        var criado = new DateTimeOffset(Dia.Year, Dia.Month, Dia.Day, 10, 0, 0, TimeSpan.FromHours(-3)).ToUniversalTime();
        DateTimeOffset? chamado = estado is EstadoAtendimento.Aguardando ? null : criado.AddMinutes(5);
        DateTimeOffset? iniciado = estado is EstadoAtendimento.Encerrado or EstadoAtendimento.Cancelado ? criado.AddMinutes(6) : null;
        DateTimeOffset? encerrado = estado is EstadoAtendimento.Encerrado ? criado.AddMinutes(10) : null;
        DateTimeOffset? cancelado = estado is EstadoAtendimento.Cancelado ? criado.AddMinutes(10) : null;
        Guid? estacao = estado is EstadoAtendimento.Aguardando ? null : guiche;
        Guid? usuario = estado is EstadoAtendimento.Aguardando ? null : usuarioId;
        var nomeEstado = estado.ToString();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Fichas" SET "CriadoEm" = {criado} WHERE "Id" = {ficha.Id}
            """);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos"
               SET "Estado" = {nomeEstado}, "CriadoEm" = {criado}, "ElegivelDesde" = {criado},
                   "ChamadoEm" = {chamado}, "IniciadoEm" = {iniciado}, "EncerradoEm" = {encerrado},
                   "CanceladoEm" = {cancelado}, "EstacaoId" = {estacao}, "UsuarioId" = {usuario}
             WHERE "Id" = {atendimento.Id}
            """);
    }

    private static int _numero;

    /// <summary>A tenant that is not in the store: only its id on the rows matters to the filters.</summary>
    private static AppTenantInfo TenantFalso() =>
        new($"paf-e4b-{Guid.NewGuid():N}"[..20], "PAF E4 tenant B", string.Empty, "b@paf.test", "paf.e4b");

    private IServiceScope Escopo(AppTenantInfo tenant)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);
        return scope;
    }

    private async Task<int> ContarAtendimentosAsync(AppTenantInfo tenant, Guid[] unidades)
    {
        using var scope = Escopo(tenant);
        var db = scope.ServiceProvider.GetRequiredService<PafDbContext>();
        return await db.Atendimentos.IgnoreQueryFilters([PafQueryFilters.Unidade]).CountAsync(a => unidades.Contains(a.UnidadeId));
    }

    private static List<IConsultaEstatistica> Consultas(IServiceScope scope)
    {
        var consultas = scope.ServiceProvider.GetServices<IConsultaEstatistica>().OrderBy(x => x.Tipo).ToList();
        consultas.Select(x => x.Tipo).ShouldBe(Tipos, "every RF07 type must be registered, or the loop skips one");
        return consultas;
    }

    private static FiltroEstatistica Filtro(TipoEstatistica tipo, IReadOnlyList<Guid> unidades) =>
        new(tipo, Dia, Dia, ComAgrupamento(tipo) ? AgrupamentoEstatistica.Setor : null, unidades, [], [], false);

    private static bool ComAgrupamento(TipoEstatistica tipo) =>
        tipo is TipoEstatistica.TotaisPorTipoDeAtendimento or TipoEstatistica.TotalDeAtendimentos;

    private async Task<HttpClient> RaizNoTenantAsync(string tenantId)
    {
        var token = await new AuthHelper(factory).GetRootAdminTokenAsync();
        var cliente = factory.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        cliente.DefaultRequestHeaders.Add("tenant", tenantId);
        return cliente;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient cliente, TipoEstatistica tipo, string extra)
    {
        var agrupamento = ComAgrupamento(tipo) ? "&agrupamento=Setor" : string.Empty;
        return cliente.GetAsync($"{PafApi.Base}/estatistica?tipo={(int)tipo}&dataInicial={Dia:yyyy-MM-dd}&dataFinal={Dia:yyyy-MM-dd}{agrupamento}{extra}");
    }

    private static async Task<string> CorpoOkAsync(HttpClient cliente, TipoEstatistica tipo, string extra)
    {
        using var resposta = await GetAsync(cliente, tipo, extra);
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, $"tipo {tipo}: {corpo}");
        return corpo;
    }

    private static async Task<ResultadoEstatisticaDto> ConsultarAsync(HttpClient cliente, TipoEstatistica tipo, string extra) =>
        Ler(await CorpoOkAsync(cliente, tipo, extra));

    private static async Task Recusado403Async(HttpClient cliente, TipoEstatistica tipo, string extra)
    {
        using var resposta = await GetAsync(cliente, tipo, extra);
        var corpo = await resposta.Content.ReadAsStringAsync();
        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"tipo {tipo}{extra}: {corpo}");
        corpo.ShouldNotContain("linhas", Case.Insensitive);
    }

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static ResultadoEstatisticaDto Ler(string corpo) => JsonSerializer.Deserialize<ResultadoEstatisticaDto>(corpo, JsonWeb)!;

    private static Guid[] UnidadesDasLinhas(ResultadoEstatisticaDto resultado) =>
        [.. resultado.Linhas.Select(l => l.UnidadeId).OfType<Guid>().Distinct()];

    private static decimal?[] Numeros(LinhaEstatisticaDto linha) => [.. linha.Celulas.Skip(2).Select(x => x.Numero)];

    private static LinhaEstatisticaDto TotalGeral(ResultadoEstatisticaDto resultado) => resultado.Totais.Single(t => t.UnidadeId is null);
}
