using FSH.Framework.Eventing.Persistence;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Midias;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.TempoReal;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// M06 against real PostgreSQL, the kit outbox and the PafHub: the painel station's ordered list (<c>PUT/GET
/// estacoes/{id}/midias</c>), what the painel plays (<c>GET estacao/painel/midias</c> by its credential, D12) and the
/// <c>ProgramacaoAlterada</c> notice of every mídia, programação and list write. Client tenant, its admin working in
/// the initial unidade (the session's unit), real station credentials from a redeemed link.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafPainelMidiasTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    private const string RotaPainel = "/api/v1/paf/estacao/painel/midias";
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilencioEsperado = TimeSpan.FromSeconds(1.5);

    private readonly PafTenantCliente _tenant = new(factory, "paf-m06");
    private readonly List<HubConnection> _conexoes = [];
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public async Task DisposeAsync()
    {
        foreach (var conexao in _conexoes)
        {
            await conexao.DisposeAsync();
        }

        await _op.DrenarCaixaDeSaidaAsync();
    }

    [Fact]
    public async Task PainelLeSoAPropriaLista_EmOrdemComUrl_VazioE200_MidiaDesativadaSome()
    {
        var u = _tenant.UnidadeInicial;
        var (painelA, painelB, _, _) = await EstacoesAsync(u);
        var video = await MidiaAsync(u, 30);
        var vinheta = await MidiaAsync(u, 5);
        using var leitorA = await PainelAsync(u, painelA);
        using var leitorB = await PainelAsync(u, painelB);

        var vazio = await LerPainelAsync(leitorA);
        (vazio.Lista.Count, vazio.Programacoes.Count).ShouldBe((0, 0), "nothing registered = only the calls, still 200");
        vazio.Agora.Offset.ShouldBe(TimeSpan.FromHours(-3), "São Paulo offset (no DST since 2019)");
        vazio.Agora.ShouldBeInRange(DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddSeconds(30));

        var salva = await SubstituirOkAsync(painelA, [video, vinheta, video]);
        salva.Itens.Select(i => (i.Ordem, i.MidiaId)).ShouldBe([(0, video), (1, vinheta), (2, video)], "the same mídia may come back in the loop");
        await SubstituirOkAsync(painelB, [vinheta]);

        var lidoA = await LerPainelAsync(leitorA);
        lidoA.Lista.Select(i => i.MidiaId).ShouldBe([video, vinheta, video]);
        lidoA.Lista.Select(i => i.ItemId).ShouldBe(salva.Itens.Select(i => i.Id));
        lidoA.Lista.ShouldAllBe(i => i.Url.IsAbsoluteUri && i.Tipo == "Video");
        lidoA.Lista[0].Url.ShouldBe(lidoA.Lista[2].Url, "one URL per mídia and exhibition: the browser caches the file across the loop");
        lidoA.Lista.ShouldAllBe(i => i.ExpiraEm > lidoA.Agora.AddMinutes(59), "at least the 1 h floor of M04");
        (await LerPainelAsync(leitorB)).Lista.Select(i => i.MidiaId).ShouldBe([vinheta], "painel B reads only its own list");

        await PafMidiaCadastroTests.MidiaOkAsync(_tenant.Admin, HttpMethod.Post, $"/midias/{vinheta}/disable", null, null);
        (await LerPainelAsync(leitorA)).Lista.Select(i => i.MidiaId).ShouldBe([video, video], "a disabled mídia leaves the painel");
        (await LerPainelAsync(leitorB)).Lista.ShouldBeEmpty();
        var cadastro = await PafApi.OkAsync<MidiasDaEstacaoDto>(_tenant.Admin, HttpMethod.Get, $"/estacoes/{painelA}/midias", null, null);
        cadastro.Itens.Select(i => (i.MidiaId, i.Ativa)).ShouldBe([(video, true), (vinheta, false), (video, true)], "the cadastro still shows it, inactive");

        (await SubstituirOkAsync(painelA, [])).Itens.ShouldBeEmpty("an empty list clears it");
        (await LerPainelAsync(leitorA)).Lista.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProgramacoesDeHoje_EmOrdemDePrecedencia_ComJanelaEUrl_SoAsAtivasDaUnidade()
    {
        var u = _tenant.UnidadeInicial;
        var outra = (await _tenant.CriarUnidadeAsync(limiteSessoes: null)).UnidadeId;
        var (painelA, _, _, _) = await EstacoesAsync(u);
        var manha = await MidiaAsync(u, 60);
        var tarde = await MidiaAsync(u, 600);
        var deOutraUnidade = await MidiaAsync(outra, 60);
        var hoje = await HojeAsync(u);
        var p8 = await ProgramarAsync(_tenant.Admin, null, manha, hoje, new TimeOnly(8, 0), 2);
        var p9 = await ProgramarAsync(_tenant.Admin, null, tarde, hoje, new TimeOnly(9, 0), 3);
        await ProgramarAsync(_tenant.Admin, null, manha, hoje.AddDays(1), new TimeOnly(7, 0), 1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, outra);
            db.ProgramacoesMidia.Add(ProgramacaoMidia.Criar(outra, deOutraUnidade, "Outra", hoje, hoje, new TimeOnly(8, 30), 1, DiasSemana.Todos));
            await db.SaveChangesAsync();
        }

        using var leitor = await PainelAsync(u, painelA);
        var lido = await LerPainelAsync(leitor);

        lido.Programacoes.Select(p => p.ProgramacaoId).ShouldBe([p9.Id, p8.Id], "latest start first (D13), tomorrow's and the other unidade's left out");
        var (nove, oito) = (lido.Programacoes[0], lido.Programacoes[1]);
        (nove.InicioSegundos, nove.FimSegundos, nove.Repeticoes, nove.DuracaoSegundos, nove.MidiaId).ShouldBe((9 * 3600, 9 * 3600 + 1800, 3, 600, tarde));
        (oito.InicioSegundos, oito.FimSegundos).ShouldBe((8 * 3600, 8 * 3600 + 120));
        lido.Programacoes.ShouldAllBe(p => p.Url.IsAbsoluteUri);
        lido.Lista.ShouldBeEmpty("no list: the programações alone");

        await ProgramacoesOkAsync(HttpMethod.Post, $"/programacoes-midia/{p8.Id}/disable");
        (await LerPainelAsync(leitor)).Programacoes.Select(p => p.ProgramacaoId).ShouldBe([p9.Id], "a disabled programação leaves the painel");
        await PafMidiaCadastroTests.MidiaOkAsync(_tenant.Admin, HttpMethod.Post, $"/midias/{tarde}/disable", null, null);
        (await LerPainelAsync(leitor)).Programacoes.ShouldBeEmpty("so does one whose mídia was disabled");
    }

    [Fact]
    public async Task Recusas_SemGravarNemAvisar_EFkComoSegundaBarreira()
    {
        var u = _tenant.UnidadeInicial;
        var outraUnidade = await _tenant.CriarUnidadeAsync(limiteSessoes: null);
        var (painelA, _, totem, guiche) = await EstacoesAsync(u);
        var boa = await MidiaAsync(u, 10);
        var inativa = await MidiaAsync(u, 10);
        await PafMidiaCadastroTests.MidiaOkAsync(_tenant.Admin, HttpMethod.Post, $"/midias/{inativa}/disable", null, null);
        var naoConfirmada = await PafProgramacoesMidiaTests.InserirMidiaAsync(factory, _tenant.Contexto, u, 10);
        var deOutraUnidade = await MidiaAsync(outraUnidade.UnidadeId, 10);
        await SubstituirOkAsync(painelA, [boa]);
        var avisosAntes = await AvisosGravadosAsync(u);

        (await SubstituirAsync(guiche, [boa])).ShouldBe(HttpStatusCode.BadRequest, "guichê station has no painel role");
        (await SubstituirAsync(totem, [])).ShouldBe(HttpStatusCode.BadRequest, "emission station has no painel role");
        foreach (var (midia, caso) in new[] { (inativa, "inativa"), (naoConfirmada, "não confirmada"), (deOutraUnidade, "de outra unidade"), (Guid.CreateVersion7(), "inexistente") })
        {
            (await SubstituirAsync(painelA, [boa, midia])).ShouldBe(HttpStatusCode.BadRequest, caso);
        }

        (await SubstituirAsync(outraUnidade.GuicheA, [boa])).ShouldBe(HttpStatusCode.NotFound, "another unidade's station");
        (await SubstituirAsync(painelA, [Guid.Empty])).ShouldBe(HttpStatusCode.BadRequest);
        (await SubstituirAsync(painelA, Enumerable.Repeat(boa, SubstituirMidiasDaEstacaoCommand.MaximoItens + 1).ToList())).ShouldBe(HttpStatusCode.BadRequest);
        using (var stale = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Put, $"/midias/{boa}", null, new UpdateMidiaCommand(boa, "Velha", 10, 99)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, "an M03 write refused under its lock");
        }

        (await LerListaAsync(painelA)).ShouldBe([boa], "nothing written");
        (await AvisosGravadosAsync(u)).ShouldBe(avisosAntes, "no ProgramacaoAlterada left by a refused write");

        // The composite FK refuses another unidade's mídia even past the handler.
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, u);
        db.PainelMidias.Add(PainelMidia.Criar(u, painelA, deOutraUnidade, 7));
        var erro = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = erro.InnerException.ShouldBeOfType<PostgresException>();
        (postgres.SqlState, postgres.ConstraintName).ShouldBe((PostgresErrorCodes.ForeignKeyViolation, "FK_PainelMidias_Midias_MesmaUnidade"));
    }

    [Fact]
    public async Task Acesso_EmissaoRecebe403_VinculoRevogado401_SemPermissao403()
    {
        var u = _tenant.UnidadeInicial;
        var (painelA, _, totem, _) = await EstacoesAsync(u);
        using var emissao = _tenant.Cliente(await _op.CredencialAsync(u, totem, PafPapeisEstacao.Emissao));
        using (var resposta = await emissao.GetAsync(RotaPainel))
        {
            resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an emission credential is not a painel");
        }

        using var painel = await PainelAsync(u, painelA);
        (await LerPainelAsync(painel)).Lista.ShouldBeEmpty();
        using (var revogar = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Post, $"/estacoes/{painelA}/vinculo/revogar", null, null))
        {
            revogar.IsSuccessStatusCode.ShouldBeTrue(await revogar.Content.ReadAsStringAsync());
        }

        using (var revogado = await painel.GetAsync(RotaPainel))
        {
            revogado.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var usuario = await _tenant.CriarUsuarioAsync(u);
        using var semPermissao = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        (await PafApi.EnviarAsync(semPermissao, HttpMethod.Get, $"/estacoes/{painelA}/midias", null, null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PafApi.EnviarAsync(semPermissao, HttpMethod.Put, $"/estacoes/{painelA}/midias", null, new { midias = Array.Empty<Guid>() })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _tenant.DarPapelAsync(usuario.UserId, PafPermissions.Estacoes.View);
        using var soLeitura = _tenant.Cliente((await _tenant.EntrarAsync(usuario)).AccessToken);
        (await PafApi.EnviarAsync(soLeitura, HttpMethod.Get, $"/estacoes/{painelA}/midias", null, null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PafApi.EnviarAsync(soLeitura, HttpMethod.Put, $"/estacoes/{painelA}/midias", null, new { midias = Array.Empty<Guid>() })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DuasSubstituicoesConcorrentes_ListaFicaInteiraComoUmaDelas()
    {
        var u = _tenant.UnidadeInicial;
        var (painelA, _, _, _) = await EstacoesAsync(u);
        var a = await MidiaAsync(u, 10);
        var b = await MidiaAsync(u, 10);
        var c = await MidiaAsync(u, 10);
        List<Guid> primeira = [a, b, c, a];
        List<Guid> segunda = [c, b];

        for (var rodada = 0; rodada < 15; rodada++)
        {
            var status = await Task.WhenAll(SubstituirAsync(painelA, primeira), SubstituirAsync(painelA, segunda));
            status.ShouldAllBe(s => s == HttpStatusCode.OK, $"rodada {rodada}");
            var final = await LerListaAsync(painelA);
            (final.SequenceEqual(primeira) || final.SequenceEqual(segunda)).ShouldBeTrue($"rodada {rodada}: {string.Join(",", final)}");
        }
    }

    [Fact]
    public async Task Aviso_ChegaSoAUnidadeCerta_DeCadaEscrita_ECaixaDeSaidaEntregaUmaVez()
    {
        var u = _tenant.UnidadeInicial;
        var outra = (await _tenant.CriarUnidadeAsync(limiteSessoes: null)).UnidadeId;
        var (painelA, _, _, _) = await EstacoesAsync(u);
        var painelOutra = await PainelDaUnidadeAsync(outra);
        var midia = await MidiaAsync(u, 60);
        var conexaoA = await ConectarAsync(u, painelA);
        var conexaoOutra = await ConectarAsync(outra, painelOutra);
        using var caixaA = new Caixa<ProgramacaoAlterada>(conexaoA, ProgramacaoAlterada.Metodo);
        using var caixaOutra = new Caixa<ProgramacaoAlterada>(conexaoOutra, ProgramacaoAlterada.Metodo);

        await SubstituirOkAsync(painelA, [midia]);
        var daLista = await caixaA.EsperarAsync(a => a.UnidadeId == u, Espera);
        daLista.ShouldNotBeNull("the list write reaches the unidade's painel by the direct send");

        var programacao = await ProgramarAsync(_tenant.Admin, null, midia, await HojeAsync(u), new TimeOnly(8, 0), 1);
        (await caixaA.EsperarAsync(a => a.EventoId != daLista.EventoId, Espera)).ShouldNotBeNull("an M05 write notifies too");
        await PafMidiaCadastroTests.MidiaOkAsync(_tenant.Admin, HttpMethod.Put, $"/midias/{midia}", null, new UpdateMidiaCommand(midia, "Vídeo", 30, 0));
        (await caixaA.EsperarAsync(_ => caixaA.Recebidos.Count >= 3, Espera)).ShouldNotBeNull("an M03 write notifies too");
        using (var excluir = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Delete, $"/programacoes-midia/{programacao.Id}", null, null))
        {
            excluir.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await caixaA.EsperarAsync(_ => caixaA.Recebidos.Count >= 4, Espera)).ShouldNotBeNull("the delete notifies too");
        await Task.Delay(SilencioEsperado);
        caixaOutra.Recebidos.ShouldBeEmpty("another unidade's painel never gets this unidade's notice");
        caixaA.Recebidos.Select(a => a.EventoId).Distinct().Count().ShouldBe(4, "one notice per write");

        // A painel that connects after the write missed the direct send, as if it had failed: the outbox delivers the
        // same notice to it, once.
        await SubstituirOkAsync(painelA, []);
        var direto = await caixaA.EsperarAsync(a => caixaA.Recebidos.Count >= 5, Espera);
        direto.ShouldNotBeNull();
        var ultimo = caixaA.Recebidos[^1].EventoId;
        var atrasada = await ConectarAsync(u, painelA);
        using var caixaAtrasada = new Caixa<ProgramacaoAlterada>(atrasada, ProgramacaoAlterada.Metodo);
        await _op.DrenarCaixaDeSaidaAsync();
        (await caixaAtrasada.EsperarAsync(a => a.EventoId == ultimo, Espera)).ShouldNotBeNull("the outbox copy arrives");
        await _op.DrenarCaixaDeSaidaAsync();
        await Task.Delay(SilencioEsperado);
        caixaAtrasada.Recebidos.Count(a => a.EventoId == ultimo).ShouldBe(1, "a processed outbox row is not delivered again");
        caixaOutra.Recebidos.ShouldBeEmpty();
    }

    private async Task<(Guid PainelA, Guid PainelB, Guid Totem, Guid Guiche)> EstacoesAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidade);
        var painelA = Estacao.Criar(unidade, "Painel A", TipoEstacao.Painel);
        var painelB = Estacao.Criar(unidade, "Painel B", TipoEstacao.GuichePainel);
        var totem = Estacao.Criar(unidade, "Totem", TipoEstacao.Emissao);
        var guiche = Estacao.Criar(unidade, "Guichê", TipoEstacao.Guiche);
        db.Estacoes.AddRange(painelA, painelB, totem, guiche);
        await db.SaveChangesAsync();
        return (painelA.Id, painelB.Id, totem.Id, guiche.Id);
    }

    private async Task<Guid> PainelDaUnidadeAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidade);
        var painel = Estacao.Criar(unidade, "Painel", TipoEstacao.Painel);
        db.Estacoes.Add(painel);
        await db.SaveChangesAsync();
        return painel.Id;
    }

    private Task<Guid> MidiaAsync(Guid unidade, int duracaoSegundos) =>
        PafProgramacoesMidiaTests.InserirMidiaConfirmadaAsync(factory, _tenant.Contexto, unidade, duracaoSegundos);

    private async Task<HttpClient> PainelAsync(Guid unidade, Guid painel) =>
        _tenant.Cliente(await _op.CredencialAsync(unidade, painel, PafPapeisEstacao.Painel));

    private async Task<HubConnection> ConectarAsync(Guid unidade, Guid painel)
    {
        var conexao = await _op.ConectarAsync(await _op.CredencialAsync(unidade, painel, PafPapeisEstacao.Painel));
        _conexoes.Add(conexao);
        return conexao;
    }

    private static async Task<MidiasPainelDto> LerPainelAsync(HttpClient painel)
    {
        using var resposta = await painel.GetAsync(RotaPainel);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
        return await resposta.DeserializeAsync<MidiasPainelDto>();
    }

    private async Task<HttpStatusCode> SubstituirAsync(Guid estacao, IReadOnlyList<Guid> midias)
    {
        using var resposta = await PafApi.EnviarAsync(_tenant.Admin, HttpMethod.Put, $"/estacoes/{estacao}/midias", null, new { midias });
        return resposta.StatusCode;
    }

    private Task<MidiasDaEstacaoDto> SubstituirOkAsync(Guid estacao, IReadOnlyList<Guid> midias) =>
        PafApi.OkAsync<MidiasDaEstacaoDto>(_tenant.Admin, HttpMethod.Put, $"/estacoes/{estacao}/midias", null, new { midias });

    private async Task<List<Guid>> LerListaAsync(Guid estacao) =>
        (await PafApi.OkAsync<MidiasDaEstacaoDto>(_tenant.Admin, HttpMethod.Get, $"/estacoes/{estacao}/midias", null, null))
            .Itens.Select(i => i.MidiaId).ToList();

    private Task<ProgramacaoMidiaDto> ProgramacoesOkAsync(HttpMethod metodo, string rota) =>
        PafProgramacoesMidiaTests.OkAsync(_tenant.Admin, metodo, rota, null, null);

    private static Task<ProgramacaoMidiaDto> ProgramarAsync(HttpClient client, Guid? unidade, Guid midia, DateOnly dia, TimeOnly inicio, int repeticoes) =>
        PafProgramacoesMidiaTests.CriarAsync(client, unidade,
            new CreateProgramacaoMidiaCommand(midia, $"Prog {inicio:HHmm}", dia, dia, inicio, repeticoes, (int)DiasSemana.Todos));

    private async Task<DateOnly> HojeAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        return await PafRelogio.HojeAsync(_tenant.Contexto(scope, unidade), CancellationToken.None);
    }

    // Outbox rows of the notice for one unidade, processed or not.
    private async Task<int> AvisosGravadosAsync(Guid unidade)
    {
        using var scope = factory.Services.CreateScope();
        var eventing = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        var id = unidade.ToString();
        return await eventing.OutboxMessages.CountAsync(m => m.Type.Contains(nameof(ProgramacaoAlteradaPaf)) && m.Payload.Contains(id));
    }
}
