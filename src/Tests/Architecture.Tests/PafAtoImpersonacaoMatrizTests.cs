using FSH.Modules.PAF;
using FSH.Modules.PAF.Contracts.v1;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using Mediator;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Matriz de <c>AtoImpersonacao</c> (D28, regra 6 do plano da fatia 2): cada comando de escrita do PAF ou leva a marca
/// <see cref="IEscritaDeCadastro"/>, e o comportamento de pipeline grava o ato da impersonação da Codefi na transação
/// da escrita, ou está em <see cref="Exclusoes"/> com o motivo escrito. Um comando novo sem nenhum dos dois reprova
/// aqui, antes de chegar a produção uma escrita do suporte sem registro do operador.
/// </summary>
public sealed class PafAtoImpersonacaoMatrizTests
{
    private const string GuicheChegaEmS11 = "guichê sob impersonação chega em S11";

    private const string CredencialDeEstacao =
        "credencial de estação: o token traz paf_papel e nunca act_sub, então não há impersonação a registrar";

    // Only S11 (wave 3) removes the guichê entries; any new entry needs its reason approved in the PR.
    private static readonly IReadOnlyDictionary<Type, string> Exclusoes = new Dictionary<Type, string>
    {
        [typeof(ChamarProximaFichaCommand)] = GuicheChegaEmS11,
        [typeof(RechamarAtendimentoCommand)] = GuicheChegaEmS11,
        [typeof(IniciarAtendimentoCommand)] = GuicheChegaEmS11,
        [typeof(EncerrarAtendimentoCommand)] = GuicheChegaEmS11,
        [typeof(CancelarAtendimentoCommand)] = GuicheChegaEmS11,
        [typeof(SetGuicheCommand)] = GuicheChegaEmS11,
        [typeof(EncaminharAtendimentoCommand)] = GuicheChegaEmS11,
        [typeof(ChamarFichaEscolhidaCommand)] = GuicheChegaEmS11,
        [typeof(EmitirFichaCommand)] = CredencialDeEstacao,
        [typeof(RenovarCredencialEstacaoCommand)] = "renovação anônima pelo segredo da estação: sem token, não há act_sub",
        [typeof(ResgatarVinculoEstacaoCommand)] = "resgate anônimo do link da estação: sem token, não há act_sub",
        [typeof(EndSessaoCommand)] =
            "logout da sessão do kit: a impersonação não tem sessão própria (seção 9) e não grava nada do PAF por aqui",
        [typeof(SetUnidadeCorrenteCommand)] =
            "troca a unidade corrente da sessão, estado no Valkey e marca de licença; sob impersonação (S10B) troca só a chave da concessão no Valkey e solta o vínculo de guichê da própria concessão, sem escrita de atendimento; o ato do guichê chega em S11",
    };

    // Slice 1 plus wave 0 already have about three dozen write commands; far fewer means the scan is not seeing them.
    private const int MinimoDeComandosEsperado = 30;

    [Fact]
    public void ComandosDeEscritaDoPaf_TodosMarcadosOuExcluidosComMotivo()
    {
        var comandos = ComandosDeEscrita(typeof(IEscritaDeCadastro).Assembly, typeof(PafModule).Assembly);

        comandos.Count.ShouldBeGreaterThanOrEqualTo(MinimoDeComandosEsperado,
            $"the scan found only {comandos.Count} PAF write commands; it is not reading the assemblies");
        var falhas = Avaliar(comandos, Exclusoes);
        falhas.ShouldBeEmpty(
            "Every PAF write command implements IEscritaDeCadastro or is excluded with a reason " +
            $"(D28):{Environment.NewLine}{string.Join(Environment.NewLine, falhas)}");
    }

    [Fact]
    public void Matriz_ComandoSinteticoSemMarcaForaDaLista_Reprova()
    {
        var falhas = Avaliar([typeof(ComandoSinteticoSemMarca), typeof(ComandoSinteticoMarcado)], new Dictionary<Type, string>());

        falhas.ShouldHaveSingleItem().ShouldContain(nameof(ComandoSinteticoSemMarca));
    }

    [Fact]
    public void Matriz_ExclusaoSemMotivo_Reprova()
    {
        var exclusoes = new Dictionary<Type, string> { [typeof(ComandoSinteticoSemMarca)] = "  " };

        Avaliar([typeof(ComandoSinteticoSemMarca)], exclusoes).ShouldHaveSingleItem().ShouldContain("sem motivo");
    }

    [Fact]
    public void Matriz_ExclusaoDeComandoMarcadoOuInexistente_Reprova()
    {
        // A stale entry would keep excusing a command that S11 or a refactor already moved: the list must stay exact.
        var exclusoes = new Dictionary<Type, string>
        {
            [typeof(ComandoSinteticoMarcado)] = "motivo",
            [typeof(ComandoSinteticoSemMarca)] = "motivo",
        };

        var falhas = Avaliar([typeof(ComandoSinteticoMarcado)], exclusoes);

        falhas.Count.ShouldBe(2);
        falhas.ShouldContain(f => f.Contains(nameof(ComandoSinteticoMarcado), StringComparison.Ordinal) && f.Contains("marcado", StringComparison.Ordinal));
        falhas.ShouldContain(f => f.Contains(nameof(ComandoSinteticoSemMarca), StringComparison.Ordinal) && f.Contains("não é comando", StringComparison.Ordinal));
    }

    private static List<Type> ComandosDeEscrita(params System.Reflection.Assembly[] assemblies) =>
        assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IBaseCommand).IsAssignableFrom(t))
            .Distinct()
            .ToList();

    private static List<string> Avaliar(IReadOnlyCollection<Type> comandos, IReadOnlyDictionary<Type, string> exclusoes)
    {
        var falhas = new List<string>();
        foreach (var comando in comandos)
        {
            bool marcado = typeof(IEscritaDeCadastro).IsAssignableFrom(comando);
            bool excluido = exclusoes.TryGetValue(comando, out var motivo);
            if (!marcado && !excluido)
            {
                falhas.Add($"{comando.FullName}: sem IEscritaDeCadastro e fora da lista de exclusões");
            }
            else if (marcado && excluido)
            {
                falhas.Add($"{comando.FullName}: marcado e também excluído");
            }
            else if (excluido && string.IsNullOrWhiteSpace(motivo))
            {
                falhas.Add($"{comando.FullName}: excluído sem motivo");
            }
        }

        falhas.AddRange(exclusoes.Keys.Where(t => !comandos.Contains(t)).Select(t => $"{t.FullName}: excluído mas não é comando do PAF"));
        return falhas;
    }

    public sealed record ComandoSinteticoSemMarca : ICommand;

    public sealed record ComandoSinteticoMarcado : ICommand<Guid>, IEscritaDeCadastro;
}
