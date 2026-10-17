using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using Integration.Tests.Infrastructure;
using Npgsql;

namespace Integration.Tests.Tests.PAF;

/// <summary>
/// The ficha lock without a guichê (RF12, requisitos seção 4, ADR 0005) and the release of a deactivated
/// station's link when the guichê closes or cancels its atendimento (L18), against the real PostgreSQL.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class PafTravaFichaTests(FshWebApplicationFactory factory) : IAsyncLifetime
{
    // Ceiling for any single wait in these tests: a regression that hangs must fail, not stall the suite.
    private static readonly TimeSpan Teto = TimeSpan.FromSeconds(20);

    private readonly PafTenantCliente _tenant = new(factory, "paf-trava");
    private PafOperacao _op = default!;

    public async Task InitializeAsync()
    {
        await _tenant.InicializarAsync();
        _op = new PafOperacao(factory, _tenant);
    }

    public Task DisposeAsync() => _op.DrenarCaixaDeSaidaAsync();

    // ─── soltura do vínculo de estação inativa ──────────────────────

    [Fact]
    public async Task EncerrarECancelar_NaEstacaoDesativada_SoltamOVinculoNaMesmaTransacao_EAtivaMantem()
    {
        var c = await _op.CriarAsync();
        using var totem = await _op.TotemAsync(c);
        using var guicheA = await _op.AtendenteAsync(c, c.GuicheA);
        using var guicheB = await _op.AtendenteAsync(c, c.GuicheB);
        for (var i = 0; i < 4; i++)
        {
            await PafOperacao.EmitirOkAsync(totem, c.Caixa, c.Normal);
        }

        await _op.EnvelhecerFichasAsync(c.UnidadeId);

        // Active station: closing and cancelling keep the link.
        var ativa = await PafOperacao.ChamarOkAsync(guicheA);
        await PafOperacao.AgirOkAsync(guicheA, ativa.AtendimentoId, "iniciar");
        await PafOperacao.AgirOkAsync(guicheA, ativa.AtendimentoId, "encerrar");
        var cancelavel = await PafOperacao.ChamarOkAsync(guicheA);
        await PafOperacao.AgirOkAsync(guicheA, cancelavel.AtendimentoId, "cancelar");
        (await VinculoAsync(c.GuicheA)).LiberadoEm.ShouldBeNull("estação ativa mantém o vínculo");

        // Deactivated with an atendimento in progress: closing still works and releases the link.
        var emAtendimento = await PafOperacao.ChamarOkAsync(guicheB);
        await PafOperacao.AgirOkAsync(guicheB, emAtendimento.AtendimentoId, "iniciar");
        await DesativarAsync(c.GuicheB);
        await PafOperacao.AgirOkAsync(guicheB, emAtendimento.AtendimentoId, "encerrar");
        var encerrado = await _op.AtendimentoAsync(c, emAtendimento.AtendimentoId);
        var vinculoB = await VinculoAsync(c.GuicheB);
        vinculoB.LiberadoEm.ShouldNotBeNull("encerrar em estação desativada solta o vínculo");
        vinculoB.LiberadoEm.ShouldBe(encerrado.EncerradoEm, "now() é o início da transação: mesmo instante = mesma transação");

        // Deactivated with a called atendimento: cancelling releases the link too.
        var chamado = await PafOperacao.ChamarOkAsync(guicheA);
        await DesativarAsync(c.GuicheA);
        await PafOperacao.AgirOkAsync(guicheA, chamado.AtendimentoId, "cancelar");
        var cancelado = await _op.AtendimentoAsync(c, chamado.AtendimentoId);
        var vinculoA = await VinculoAsync(c.GuicheA);
        vinculoA.LiberadoEm.ShouldNotBeNull("cancelar em estação desativada solta o vínculo");
        vinculoA.LiberadoEm.ShouldBe(cancelado.CanceladoEm, "mesma transação do cancelamento");

        using var semGuiche = await PafOperacao.ChamarAsync(guicheA);
        semGuiche.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        PafSessaoLicencaTests.Detalhe(await semGuiche.Content.ReadAsStringAsync()).ShouldBe(MensagensGuiche.MensagemSemGuiche);
    }

    [Fact]
    public async Task Soltura_DesfeitaComARolagem_ESemTransacaoLanca()
    {
        var c = await _op.CriarAsync();
        using var guiche = await _op.AtendenteAsync(c, c.GuicheA);
        await DesativarAsync(c.GuicheA);

        using (var scope = factory.Services.CreateScope())
        {
            var db = _tenant.Contexto(scope, c.UnidadeId);
            await Should.ThrowAsync<InvalidOperationException>(
                () => OperacaoGuiche.SoltarVinculoDeEstacaoInativaAsync(db, _tenant.TenantId, c.GuicheA, CancellationToken.None));

            await using var tx = await db.Database.BeginTransactionAsync();
            (await OperacaoGuiche.SoltarVinculoDeEstacaoInativaAsync(db, _tenant.TenantId, c.GuicheA, CancellationToken.None))
                .ShouldNotBeNull("o vínculo é de uma sessão");
            await tx.RollbackAsync();
        }

        (await VinculoAsync(c.GuicheA)).LiberadoEm.ShouldBeNull("a soltura desfaz junto com a transação do chamador");
    }

    // ─── trava da ficha sem guichê ──────────────────────────────────

    [Fact]
    public async Task TravarFicha_SemTransacao_Lanca_EOutraUnidade404()
    {
        var c = await _op.CriarAsync();
        var outra = await _op.CriarAsync();
        var (_, atendimentos) = await FichaComAtendimentosAsync(c, 1, 1);

        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        await Should.ThrowAsync<InvalidOperationException>(
            () => OperacaoGuiche.TravarFichaAsync(db, _tenant.TenantId, atendimentos[0], CancellationToken.None));

        using var scopeOutra = factory.Services.CreateScope();
        var dbOutra = _tenant.Contexto(scopeOutra, outra.UnidadeId);
        await using var tx = await dbOutra.Database.BeginTransactionAsync();
        await Should.ThrowAsync<NotFoundException>(
            () => OperacaoGuiche.TravarFichaAsync(dbOutra, _tenant.TenantId, atendimentos[0], CancellationToken.None));
    }

    [Fact]
    public async Task TravarFicha_DevolveOsDadosDoAtendimento()
    {
        var c = await _op.CriarAsync();
        var (fichaId, atendimentos) = await FichaComAtendimentosAsync(c, 1, 2);

        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        await using var tx = await db.Database.BeginTransactionAsync();
        var travado = await OperacaoGuiche.TravarFichaAsync(db, _tenant.TenantId, atendimentos[1], CancellationToken.None);
        var gravado = await db.Atendimentos.AsNoTracking().SingleAsync(a => a.Id == atendimentos[1]);

        travado.ShouldBe(new AtendimentoTravado(
            gravado.Id, c.UnidadeId, fichaId, c.Caixa, c.Normal, null, EstadoAtendimento.Aguardando, "A", 1,
            gravado.CriadoEm, gravado.DataElegibilidade));
    }

    [Fact]
    public async Task TravarFicha_SegundaTransacaoEspera_ERevalidaDepoisDoCommit()
    {
        var c = await _op.CriarAsync();
        var (_, atendimentos) = await FichaComAtendimentosAsync(c, 1, 2);

        using var scope1 = factory.Services.CreateScope();
        var db1 = _tenant.Contexto(scope1, c.UnidadeId);
        await using var tx1 = await db1.Database.BeginTransactionAsync();
        await OperacaoGuiche.TravarFichaAsync(db1, _tenant.TenantId, atendimentos[0], CancellationToken.None);

        // Enters by the OTHER atendimento of the ficha: the lock is on the ficha, not on the row it entered by.
        var segunda = TravarEmTransacaoNovaAsync(c, atendimentos[1]);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        segunda.IsCompleted.ShouldBeFalse("a segunda operação espera a trava da ficha");

        await db1.Database.ExecuteSqlAsync($"""
            UPDATE paf."Atendimentos" SET "Estado" = 'Cancelado', "CanceladoEm" = now(), "Versao" = "Versao" + 1
             WHERE "Id" = {atendimentos[1]}
            """);
        await tx1.CommitAsync();

        (await segunda).Estado.ShouldBe(EstadoAtendimento.Cancelado, "a leitura sob a trava vê o que a primeira confirmou");
    }

    [Fact]
    public async Task TravarFicha_TresEntradasDiferentesConcorrentes_SemImpasseEm50Rodadas()
    {
        var c = await _op.CriarAsync();
        var inicio = DateTime.UtcNow;
        for (var rodada = 0; rodada < 50; rodada++)
        {
            var (_, atendimentos) = await FichaComAtendimentosAsync(c, rodada + 1, 3);
            var largada = new Largada(3);

            // Each transaction enters by a different atendimento, in an order that differs from the ids'.
            int[] entradas = [2, 0, 1];
            var erros = await Task.WhenAll(entradas.Select(i => TravarEEscreverAsync(c, atendimentos[i], largada)));

            erros.ShouldAllBe(e => e == null, $"rodada {rodada}: {string.Join(", ", erros.Where(e => e != null).Select(e => e!.SqlState))}");
            var versoes = await VersoesAsync(c, atendimentos);
            versoes.ShouldAllBe(v => v == 1, "cada transação gravou o seu atendimento uma vez");
        }

        (DateTime.UtcNow - inicio).ShouldBeLessThan(TimeSpan.FromSeconds(90), "50 rodadas serializadas sem espera de impasse");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<AtendimentoTravado> TravarEmTransacaoNovaAsync(CenarioOperacao c, Guid atendimentoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        await using var tx = await db.Database.BeginTransactionAsync();
        using var teto = new CancellationTokenSource(Teto);
        return await OperacaoGuiche.TravarFichaAsync(db, _tenant.TenantId, atendimentoId, teto.Token);
    }

    private async Task<PostgresException?> TravarEEscreverAsync(CenarioOperacao c, Guid atendimentoId, Largada largada)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        await using var tx = await db.Database.BeginTransactionAsync();
        using var teto = new CancellationTokenSource(Teto);
        try
        {
            await largada.ChegarAsync();
            var travado = await OperacaoGuiche.TravarFichaAsync(db, _tenant.TenantId, atendimentoId, teto.Token);
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE paf."Atendimentos" SET "Versao" = "Versao" + 1 WHERE "Id" = {travado.Id}""", teto.Token);

            // Holds the lock long enough for the rivals to queue on it.
            await Task.Delay(TimeSpan.FromMilliseconds(20), teto.Token);
            await tx.CommitAsync(teto.Token);
            return null;
        }
        catch (PostgresException pg)
        {
            return pg;
        }
    }

    private async Task<(Guid FichaId, List<Guid> Atendimentos)> FichaComAtendimentosAsync(CenarioOperacao c, int numero, int quantidade)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        var ficha = Ficha.Criar(c.UnidadeId, c.Caixa, c.Normal, "A", numero, CanalEmissao.Totem, c.Totem, Guid.NewGuid().ToString("N"));
        var atendimentos = Enumerable.Range(0, quantidade)
            .Select(_ => Atendimento.Criar(c.UnidadeId, ficha.Id, c.Caixa, c.Normal, atendimentoOrigemId: null))
            .ToList();
        db.Fichas.Add(ficha);
        db.Atendimentos.AddRange(atendimentos);
        await db.SaveChangesAsync();
        return (ficha.Id, atendimentos.Select(a => a.Id).ToList());
    }

    private async Task<List<int>> VersoesAsync(CenarioOperacao c, List<Guid> atendimentos)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, c.UnidadeId);
        return await db.Atendimentos.AsNoTracking().Where(a => atendimentos.Contains(a.Id)).Select(a => a.Versao).ToListAsync();
    }

    private async Task<SessaoEstacao> VinculoAsync(Guid estacaoId)
    {
        using var scope = factory.Services.CreateScope();
        var db = _tenant.Contexto(scope, unidadeCorrente: null);
        return await db.SessoesEstacao.AsNoTracking().SingleAsync(v => v.EstacaoId == estacaoId);
    }

    private Task<int> DesativarAsync(Guid estacaoId) =>
        _tenant.ExecutarAsync($"""UPDATE paf."Estacoes" SET "Ativa" = false WHERE "Id" = {estacaoId}""");
}
