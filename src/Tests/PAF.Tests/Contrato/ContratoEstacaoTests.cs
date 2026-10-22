namespace PAF.Tests.Contrato;

public sealed class ContratoEstacaoTests
{
    private static readonly string ArquivoSnapshot = Path.Combine(AppContext.BaseDirectory, "Contrato", "contrato-estacao.snapshot.txt");

    private static IReadOnlyList<string> FormaAtual() =>
        ContratoEstacao.Forma(ContratoEstacao.Respostas, ContratoEstacao.Pedidos, ContratoEstacao.Constantes);

    private static List<string> Snapshot() =>
        File.ReadAllLines(ArquivoSnapshot).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    [Fact]
    public void ContratoDeEstacaoEHub_MantemTudoQueOSnapshotRegistra()
    {
        var atual = FormaAtual();
        var quebras = ContratoEstacao.Quebras(Snapshot(), atual);

        // The current shape always goes next to the snapshot: after an addition, copying it over protects the new member too.
        File.WriteAllLines(Path.ChangeExtension(ArquivoSnapshot, ".atual.txt"), atual);

        quebras.ShouldBeEmpty(
            "Totem, painel e telas do PafHub rodam bundle antigo por dias (RNF16): campo de DTO de estação ou de evento do hub "
            + "não some nem muda de tipo, nome de método do hub não muda, e campo novo em pedido é anulável. Expanda agora e "
            + "contraia numa versão seguinte, depois que nenhum bundle antigo usar o campo.");
    }

    [Fact]
    public void Snapshot_CobreCadaTipoDoContrato()
    {
        // A type left out of the snapshot would accept any removal; every listed type must have at least one line.
        var snapshot = Snapshot();
        foreach (var tipo in ContratoEstacao.Respostas.Concat(ContratoEstacao.Pedidos).Concat(ContratoEstacao.Constantes))
        {
            snapshot.ShouldContain(l => l.Contains($" {tipo.Name}.", StringComparison.Ordinal), $"tipo {tipo.Name} fora do snapshot");
        }
    }

    [Fact]
    public void RemoverCampoDeFichaEmitidaDto_Reprova()
    {
        var quebras = QuebrasTrocando(typeof(FSH.Modules.PAF.Contracts.v1.Fichas.FichaEmitidaDto), typeof(SemSenha.FichaEmitidaDto));

        quebras.ShouldBe(["removido ou alterado: resposta FichaEmitidaDto.Senha: String"]);
    }

    [Fact]
    public void TornarAnulavelCampoDeResposta_Reprova()
    {
        var quebras = QuebrasTrocando(typeof(FSH.Modules.PAF.Contracts.v1.Fichas.FichaEmitidaDto), typeof(SenhaAnulavel.FichaEmitidaDto));

        quebras.ShouldBe(["removido ou alterado: resposta FichaEmitidaDto.Senha: String"]);
    }

    [Fact]
    public void AcrescentarCampoEmResposta_Passa()
    {
        QuebrasTrocando(typeof(FSH.Modules.PAF.Contracts.v1.Fichas.FichaEmitidaDto), typeof(ComCampoNovo.FichaEmitidaDto))
            .ShouldBeEmpty();
    }

    [Fact]
    public void AcrescentarCampoObrigatorioEmPedido_Reprova()
    {
        var quebras = QuebrasTrocando(typeof(FSH.Modules.PAF.Contracts.v1.Fichas.EmitirFichaRequest), typeof(PedidoObrigatorio.EmitirFichaRequest));

        quebras.ShouldBe(["campo obrigatório novo em pedido (bundle antigo não envia): pedido EmitirFichaRequest.Canal: String"]);
    }

    [Fact]
    public void AcrescentarCampoAnulavelEmPedido_Passa()
    {
        QuebrasTrocando(typeof(FSH.Modules.PAF.Contracts.v1.Fichas.EmitirFichaRequest), typeof(PedidoAnulavel.EmitirFichaRequest))
            .ShouldBeEmpty();
    }

    [Fact]
    public void RenomearMetodoDoHub_Reprova()
    {
        var quebras = QuebrasTrocando(typeof(FSH.Modules.PAF.Contracts.v1.TempoReal.PafHubMetodos), typeof(MetodoRenomeado.PafHubMetodos));

        quebras.ShouldBe(["removido ou alterado: const PafHubMetodos.SenhaChamada = \"SenhaChamada\""]);
    }

    private static IReadOnlyList<string> QuebrasTrocando(Type real, Type sintetico)
    {
        Type[] Trocar(Type[] tipos) => tipos.Select(t => t == real ? sintetico : t).ToArray();
        var trocada = ContratoEstacao.Forma(
            Trocar(ContratoEstacao.Respostas), Trocar(ContratoEstacao.Pedidos), Trocar(ContratoEstacao.Constantes));
        return ContratoEstacao.Quebras(FormaAtual(), trocada);
    }
}
