using System.Diagnostics;
using FSH.Modules.PAF.Sessao;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace PAF.Tests;

/// <summary>
/// Conjunto de licenças contra um Valkey real (seção 9, RF22): ocupação atômica pela lista de candidatas,
/// idempotência por sessão, expiração por membro, isolamento por tenant, reconstrução e falha rápida.
/// </summary>
[Collection(ValkeyPafColecao.Nome)]
public sealed class ConjuntoLicencasTests(ValkeyPaf valkey) : IAsyncLifetime
{
    private static readonly TimeSpan Prazo = TimeSpan.FromMinutes(10);
    private readonly string _tenant = $"t-{Guid.NewGuid():N}";

    public async Task InitializeAsync() =>
        (await Conjunto(Prazo).MarcarReconstruidoAsync(CancellationToken.None)).ShouldBeTrue();

    public Task DisposeAsync() => Task.CompletedTask;

    private ConjuntoLicencas Conjunto(TimeSpan prazo) => new([valkey.Multiplexer], prazo, NullLogger<ConjuntoLicencas>.Instance);

    private Task<long> TamanhoAsync(string tenant, Guid unidadeId) =>
        valkey.Multiplexer.GetDatabase().SortedSetLengthAsync(ConjuntoLicencas.ChaveConjunto(tenant, unidadeId));

    private Task<Ocupacao> OcuparAsync(ConjuntoLicencas conjunto, string membro, params CandidataLicenca[] candidatas) =>
        conjunto.OcuparAsync(_tenant, membro, candidatas, [], [], Prazo, CancellationToken.None);

    [Fact]
    public async Task UltimaVaga_VinteOcupacoesSimultaneas_SoUmaAceita()
    {
        var conjunto = Conjunto(Prazo);
        var unidade = new CandidataLicenca(Guid.NewGuid(), 1, false);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => OcuparAsync(conjunto, ConjuntoLicencas.Membro(Guid.NewGuid()), unidade))));

        resultados.Count(r => r.Desfecho == DesfechoOcupacao.Ocupada).ShouldBe(1);
        resultados.Count(r => r.Desfecho == DesfechoOcupacao.SemSaldo).ShouldBe(19);
        (await TamanhoAsync(_tenant, unidade.UnidadeId)).ShouldBe(1);
    }

    [Fact]
    public async Task MesmaSessaoRepetida_DevolveAUnidadeSemAumentarOConjunto()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var b = new CandidataLicenca(Guid.NewGuid(), 5, false);
        var membro = ConjuntoLicencas.Membro(Guid.NewGuid());

        var resultados = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => OcuparAsync(conjunto, membro, a, b))));

        // The repeated attempts find A full by the session's own seat, and must not fall to B (seção 9).
        resultados.ShouldAllBe(r => r.UnidadeId == a.UnidadeId);
        resultados.Count(r => r.Desfecho == DesfechoOcupacao.Ocupada).ShouldBe(1);
        resultados.Count(r => r.Desfecho == DesfechoOcupacao.JaOcupava).ShouldBe(9);
        (await TamanhoAsync(_tenant, a.UnidadeId)).ShouldBe(1);
        (await TamanhoAsync(_tenant, b.UnidadeId)).ShouldBe(0);
    }

    [Fact]
    public async Task Cascata_PrimeiraCheia_OcupaASegundaSemMarcaNaPrimeira()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var b = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var ocupante = ConjuntoLicencas.Membro(Guid.NewGuid());
        (await OcuparAsync(conjunto, ocupante, a)).Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
        var membro = ConjuntoLicencas.Membro(Guid.NewGuid());

        var resultado = await OcuparAsync(conjunto, membro, a, b);

        resultado.ShouldBe(new Ocupacao(DesfechoOcupacao.Ocupada, b.UnidadeId, 1));
        var db = valkey.Multiplexer.GetDatabase();
        (await db.SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenant, a.UnidadeId)))
            .Select(v => v.ToString()).ShouldBe([ocupante]);
        (await db.SortedSetScoreAsync(ConjuntoLicencas.ChaveConjunto(_tenant, b.UnidadeId), membro)).ShouldNotBeNull();
    }

    [Fact]
    public async Task TodasCheias_SemSaldoENadaGravado()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 0, false);
        var b = new CandidataLicenca(Guid.NewGuid(), 0, false);
        var membro = ConjuntoLicencas.Membro(Guid.NewGuid());

        (await OcuparAsync(conjunto, membro, a, b)).Desfecho.ShouldBe(DesfechoOcupacao.SemSaldo);

        (await TamanhoAsync(_tenant, a.UnidadeId)).ShouldBe(0);
        (await TamanhoAsync(_tenant, b.UnidadeId)).ShouldBe(0);
        (await valkey.Multiplexer.GetDatabase().KeyExistsAsync($"paf:{_tenant}:sessao:{membro}:licenca")).ShouldBeFalse();
    }

    [Fact]
    public async Task Ultrapassagem_OcupaAcimaDoLimiteEInformaAContagem()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 1, true);
        await OcuparAsync(conjunto, ConjuntoLicencas.Membro(Guid.NewGuid()), a);

        var resultado = await OcuparAsync(conjunto, ConjuntoLicencas.Membro(Guid.NewGuid()), a);

        resultado.ShouldBe(new Ocupacao(DesfechoOcupacao.Ocupada, a.UnidadeId, 2));
    }

    [Fact]
    public async Task MembroSemRenovacao_ExpiraELiberaAVaga_ComRenovacaoFica()
    {
        var curto = Conjunto(TimeSpan.FromSeconds(1));
        var a = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var b = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var renovado = ConjuntoLicencas.Membro(Guid.NewGuid());
        var abandonado = ConjuntoLicencas.Membro(Guid.NewGuid());
        var ct = CancellationToken.None;
        (await curto.OcuparAsync(_tenant, renovado, [a], [], [], curto.PrazoMembro, ct)).Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
        (await curto.OcuparAsync(_tenant, abandonado, [b], [], [], curto.PrazoMembro, ct)).Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);

        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (await curto.RestaurarAsync(_tenant, renovado, a.UnidadeId, curto.PrazoMembro, ct)).ShouldBeTrue();
        }

        (await OcuparAsync(curto, ConjuntoLicencas.Membro(Guid.NewGuid()), a)).Desfecho.ShouldBe(DesfechoOcupacao.SemSaldo);
        (await OcuparAsync(curto, ConjuntoLicencas.Membro(Guid.NewGuid()), b)).Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
    }

    [Fact]
    public async Task TenantsDiferentes_MesmaUnidade_NaoSeSomam()
    {
        var conjunto = Conjunto(Prazo);
        var unidade = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var outroTenant = $"t-{Guid.NewGuid():N}";

        (await OcuparAsync(conjunto, ConjuntoLicencas.Membro(Guid.NewGuid()), unidade)).Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
        var outro = await conjunto.OcuparAsync(
            outroTenant, ConjuntoLicencas.Membro(Guid.NewGuid()), [unidade], [], [], Prazo, CancellationToken.None);

        outro.Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
        (await TamanhoAsync(_tenant, unidade.UnidadeId)).ShouldBe(1);
        (await TamanhoAsync(outroTenant, unidade.UnidadeId)).ShouldBe(1);
    }

    [Fact]
    public async Task SessaoAnteriorIgnorada_ContaComoLivre_SemSairDoConjunto()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var anterior = ConjuntoLicencas.Membro(Guid.NewGuid());
        await OcuparAsync(conjunto, anterior, a);
        var reserva = ConjuntoLicencas.NovaReserva();

        var resultado = await conjunto.OcuparAsync(_tenant, reserva, [a], [anterior], [], Prazo, CancellationToken.None);

        resultado.Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
        (await TamanhoAsync(_tenant, a.UnidadeId)).ShouldBe(2);
    }

    [Fact]
    public async Task Soltar_NoMesmoScript_PassaAVagaDaReservaParaASessao()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var anterior = ConjuntoLicencas.Membro(Guid.NewGuid());
        await OcuparAsync(conjunto, anterior, a);
        var reserva = ConjuntoLicencas.NovaReserva();
        await conjunto.OcuparAsync(_tenant, reserva, [a], [anterior], [], Prazo, CancellationToken.None);
        var sessao = ConjuntoLicencas.Membro(Guid.NewGuid());

        var resultado = await conjunto.OcuparAsync(_tenant, sessao, [a], [], [reserva, anterior], Prazo, CancellationToken.None);

        resultado.ShouldBe(new Ocupacao(DesfechoOcupacao.Ocupada, a.UnidadeId, 1));
        (await valkey.Multiplexer.GetDatabase().SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenant, a.UnidadeId)))
            .Select(v => v.ToString()).ShouldBe([sessao]);
    }

    [Fact]
    public async Task Liberar_TiraOMembroEAbreAVaga()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 1, false);
        var membro = ConjuntoLicencas.Membro(Guid.NewGuid());
        await OcuparAsync(conjunto, membro, a);

        (await conjunto.LiberarAsync(_tenant, membro, CancellationToken.None)).ShouldBeTrue();

        (await TamanhoAsync(_tenant, a.UnidadeId)).ShouldBe(0);
        (await OcuparAsync(conjunto, ConjuntoLicencas.Membro(Guid.NewGuid()), a)).Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
    }

    [Fact]
    public async Task SemMarcaDeReconstrucao_NaoOcupaNada()
    {
        var conjunto = Conjunto(Prazo);
        var a = new CandidataLicenca(Guid.NewGuid(), 5, false);
        await valkey.EsvaziarAsync();

        (await OcuparAsync(conjunto, ConjuntoLicencas.Membro(Guid.NewGuid()), a)).Desfecho.ShouldBe(DesfechoOcupacao.NaoReconstruido);

        (await TamanhoAsync(_tenant, a.UnidadeId)).ShouldBe(0);
        (await conjunto.ReconstruidoAsync(CancellationToken.None)).ShouldBe(false);
    }

    [Fact]
    public async Task Reconstrucao_TrocaOsMembrosVelhosPelosDoBanco_PreservaOsNovosEAsReservas()
    {
        var conjunto = Conjunto(Prazo);
        var ct = CancellationToken.None;
        var a = new CandidataLicenca(Guid.NewGuid(), 10, false);
        var velho = ConjuntoLicencas.Membro(Guid.NewGuid());
        var reserva = ConjuntoLicencas.NovaReserva();
        await OcuparAsync(conjunto, velho, a);
        await conjunto.OcuparAsync(_tenant, reserva, [a], [], [], TimeSpan.FromMinutes(1), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        var marco = (await conjunto.MarcoAsync(ct))!.Value;
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        var novo = ConjuntoLicencas.Membro(Guid.NewGuid());
        await OcuparAsync(conjunto, novo, a);
        var doBanco = Guid.NewGuid();

        (await conjunto.ReconstruirTenantAsync(_tenant, [new MembroVivo(doBanco, a.UnidadeId, TimeSpan.FromMinutes(5))], marco, ct))
            .ShouldBeTrue();

        var membros = (await valkey.Multiplexer.GetDatabase().SortedSetRangeByRankAsync(ConjuntoLicencas.ChaveConjunto(_tenant, a.UnidadeId)))
            .Select(v => v.ToString()).ToList();
        membros.ShouldBe([reserva, ConjuntoLicencas.Membro(doBanco), novo], ignoreOrder: true);
        // The rebuilt member is a regular member: occupying it again is idempotent.
        (await OcuparAsync(conjunto, ConjuntoLicencas.Membro(doBanco), a)).Desfecho.ShouldBe(DesfechoOcupacao.JaOcupava);
    }

    [Fact]
    public async Task SemValkeyRegistrado_Indisponivel()
    {
        var conjunto = new ConjuntoLicencas([], Prazo, NullLogger<ConjuntoLicencas>.Instance);

        (await OcuparAsync(conjunto, "s", new CandidataLicenca(Guid.NewGuid(), 1, false))).Desfecho
            .ShouldBe(DesfechoOcupacao.Indisponivel);
        (await conjunto.ReconstruidoAsync(CancellationToken.None)).ShouldBeNull();
        (await conjunto.RestaurarAsync(_tenant, "s", Guid.NewGuid(), Prazo, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task ValkeyPausado_FalhaDentroDoPrazoCurto_EVoltaAoRetomar()
    {
        await using var multiplexer = await valkey.NovoMultiplexerAsync();
        var conjunto = new ConjuntoLicencas([multiplexer], Prazo, NullLogger<ConjuntoLicencas>.Instance);
        var a = new CandidataLicenca(Guid.NewGuid(), 1, false);
        await valkey.PausarAsync(CancellationToken.None);
        Ocupacao pausado;
        var relogio = Stopwatch.StartNew();
        try
        {
            pausado = await OcuparAsync(conjunto, ConjuntoLicencas.Membro(Guid.NewGuid()), a);
            relogio.Stop();
        }
        finally
        {
            await valkey.RetomarAsync(CancellationToken.None);
        }

        pausado.Desfecho.ShouldBe(DesfechoOcupacao.Indisponivel);
        relogio.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5));
        // The command sent during the pause may still run on resume, since the deadline does not cancel it on the
        // server. What matters is that the call came back in time and the set works again.
        await Task.Delay(TimeSpan.FromSeconds(1));
        (await OcuparAsync(Conjunto(Prazo), ConjuntoLicencas.Membro(Guid.NewGuid()), new CandidataLicenca(Guid.NewGuid(), 1, false)))
            .Desfecho.ShouldBe(DesfechoOcupacao.Ocupada);
    }
}
