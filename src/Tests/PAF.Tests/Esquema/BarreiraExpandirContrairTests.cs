using FSH.Starter.Migrations.PostgreSQL.PAF;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PAF.Tests.Esquema;

public sealed class BarreiraExpandirContrairTests
{
    private static readonly IReadOnlyList<Migration> Migracoes =
        BarreiraExpandirContrair.MigracoesDoPaf(typeof(InitialPaf).Assembly);

    // Target model of the newest migration: the synthetic migrations (MigracoesSinteticas.cs) run on top of it.
    private static IModel ModeloAtual() => Migracoes[^1].TargetModel;

    private static readonly Dictionary<string, Func<Migration>> Sinteticas = new(StringComparer.Ordinal)
    {
        [nameof(SinteticaDropColumn)] = () => new SinteticaDropColumn(),
        [nameof(SinteticaRenameColumn)] = () => new SinteticaRenameColumn(),
        [nameof(SinteticaDropTable)] = () => new SinteticaDropTable(),
        [nameof(SinteticaRenameTable)] = () => new SinteticaRenameTable(),
        [nameof(SinteticaNuloParaObrigatorio)] = () => new SinteticaNuloParaObrigatorio(),
        [nameof(SinteticaObrigatorioParaNulo)] = () => new SinteticaObrigatorioParaNulo(),
        [nameof(SinteticaAddColumnNula)] = () => new SinteticaAddColumnNula(),
        [nameof(SinteticaAddColumnObrigatoriaSemPadrao)] = () => new SinteticaAddColumnObrigatoriaSemPadrao(),
        [nameof(SinteticaAddColumnObrigatoriaComPadrao)] = () => new SinteticaAddColumnObrigatoriaComPadrao(),
        [nameof(SinteticaCreateTableEIndice)] = () => new SinteticaCreateTableEIndice(),
        [nameof(SinteticaDropIndiceUnico)] = () => new SinteticaDropIndiceUnico(),
        [nameof(SinteticaDropIndiceNaoUnico)] = () => new SinteticaDropIndiceNaoUnico(),
        [nameof(SinteticaDropIndiceDesconhecido)] = () => new SinteticaDropIndiceDesconhecido(),
        [nameof(SinteticaDropUniqueConstraint)] = () => new SinteticaDropUniqueConstraint(),
        [nameof(SinteticaRenameIndex)] = () => new SinteticaRenameIndex(),
        [nameof(SinteticaSqlDropColumn)] = () => new SinteticaSqlDropColumn(),
        [nameof(SinteticaSqlSetNotNull)] = () => new SinteticaSqlSetNotNull(),
        [nameof(SinteticaSqlDropNotNull)] = () => new SinteticaSqlDropNotNull(),
        [nameof(SinteticaContracaoMarcada)] = () => new SinteticaContracaoMarcada(),
    };

    private static Migration Criar(string sintetica) => Sinteticas[sintetica]();

    [Fact]
    public void Enumeracao_EncontraAsMigracoesDoPafEmOrdem()
    {
        // Guards against a vacuous pass: a broken enumeration would judge nothing and stay green.
        Migracoes.Count.ShouldBeGreaterThanOrEqualTo(5);
        Migracoes.Select(BarreiraExpandirContrair.IdDe).ShouldContain("20261019083417_ConsumoLicenca");
        Migracoes.Select(BarreiraExpandirContrair.IdDe).ShouldBeInOrder(SortDirection.Ascending, StringComparer.Ordinal);
    }

    [Fact]
    public void MigracoesDoPaf_SoExpandemOuSaoMarcadasComoContracao()
    {
        // Plan rule 3 makes the barrier valid from wave 3 on; every earlier migration already complies (the two DropIndex
        // of MidiaUnidadeInicialRamalSetor are of non-unique indexes), so all are judged and none is grandfathered.
        var violacoes = new List<string>();
        for (var i = 1; i < Migracoes.Count; i++)
        {
            violacoes.AddRange(BarreiraExpandirContrair.Violacoes(Migracoes[i], Migracoes[i - 1].TargetModel));
        }

        violacoes.ShouldBeEmpty(
            "Migração de expandir não pode remover, renomear, tornar obrigatório nem tirar índice único: a imagem anterior "
            + "e os bundles de totem e painel ainda usam o esquema antigo. Separe em expandir agora e contrair numa versão "
            + "seguinte, marcada com [MigracaoDeContracao(\"motivo\")].");
    }

    [Theory]
    [InlineData(nameof(SinteticaDropColumn), "DropColumn Fichas.Letra")]
    [InlineData(nameof(SinteticaRenameColumn), "RenameColumn Fichas.Letra -> Prefixo")]
    [InlineData(nameof(SinteticaDropTable), "DropTable Fichas")]
    [InlineData(nameof(SinteticaRenameTable), "RenameTable Fichas -> FichasEmitidas")]
    [InlineData(nameof(SinteticaNuloParaObrigatorio), "AlterColumn Atendimentos.EstacaoId de nulo para obrigatório")]
    [InlineData(nameof(SinteticaAddColumnObrigatoriaSemPadrao), "AddColumn Fichas.Origem obrigatória sem valor padrão")]
    [InlineData(nameof(SinteticaDropIndiceUnico), "DropIndex único UX_Atendimentos_FichaEmCurso")]
    [InlineData(nameof(SinteticaDropIndiceDesconhecido), "DropIndex único IX_NaoExisteNoModelo")]
    [InlineData(nameof(SinteticaDropUniqueConstraint), "DropUniqueConstraint AK_Estacoes_UnidadeId_Id")]
    [InlineData(nameof(SinteticaRenameIndex), "RenameIndex UX_Atendimentos_FichaEmCurso -> UX_Novo")]
    [InlineData(nameof(SinteticaSqlDropColumn), "Sql de contração")]
    [InlineData(nameof(SinteticaSqlSetNotNull), "Sql de contração")]
    public void MigracaoDeExpandir_ComOperacaoDeContracao_Reprova(string sintetica, string esperado)
    {
        var violacoes = BarreiraExpandirContrair.Violacoes(Criar(sintetica), ModeloAtual());

        violacoes.Count.ShouldBe(1);
        violacoes[0].ShouldContain(esperado);
    }

    [Theory]
    [InlineData(nameof(SinteticaAddColumnNula))]
    [InlineData(nameof(SinteticaAddColumnObrigatoriaComPadrao))]
    [InlineData(nameof(SinteticaCreateTableEIndice))]
    [InlineData(nameof(SinteticaObrigatorioParaNulo))]
    [InlineData(nameof(SinteticaDropIndiceNaoUnico))]
    [InlineData(nameof(SinteticaSqlDropNotNull))]
    [InlineData(nameof(SinteticaContracaoMarcada))]
    public void MigracaoQueSoExpandeOuMarcadaComoContracao_Passa(string sintetica)
    {
        BarreiraExpandirContrair.Violacoes(Criar(sintetica), ModeloAtual()).ShouldBeEmpty();
    }

    [Fact]
    public void MigracaoComVariasContracoes_ListaTodas()
    {
        var violacoes = BarreiraExpandirContrair.Violacoes(new SinteticaDuasContracoes(), ModeloAtual());

        violacoes.Count.ShouldBe(2);
        violacoes.ShouldAllBe(v => v.StartsWith(nameof(SinteticaDuasContracoes) + ":", StringComparison.Ordinal));
    }
}
