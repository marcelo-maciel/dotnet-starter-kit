using System.Reflection;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.Painel;
using FSH.Modules.PAF.Contracts.v1.TempoReal;

namespace PAF.Tests.Contrato;

/// <summary>
/// Wire contract of the screens that keep an old bundle open for days (RNF16, O07): totem and painel (station DTOs and
/// requests) and every PafHub client (method names and event payloads). One line per member; the committed snapshot is
/// the floor every later build must keep.
/// </summary>
internal static class ContratoEstacao
{
    /// <summary>Server to client: an old bundle reads these, so a member may never disappear nor change type.</summary>
    public static readonly Type[] Respostas =
    [
        typeof(FichaEmitidaDto),
        typeof(ConfiguracaoEstacaoDto),
        typeof(BotaoEmissaoDto),
        typeof(EstadoPainelDto),
        typeof(ChamadaPainelDto),
        typeof(CredenciaisEstacaoDto),
        typeof(CredencialEstacaoDto),
        typeof(AcessoEstacaoDto),
        typeof(SenhaChamada),
        typeof(SenhaRechamada),
        typeof(AtendimentoAtualizado),
        typeof(FilaAtualizada),
        typeof(MidiasPainelDto),
        typeof(ItemMidiaPainelDto),
        typeof(ProgramacaoPainelDto),
        typeof(ProgramacaoAlterada),
    ];

    /// <summary>Client to server bodies: an old bundle does not send a member added later, so a new one must be nullable.</summary>
    public static readonly Type[] Pedidos =
    [
        typeof(EmitirFichaRequest),
        typeof(ResgatarVinculoEstacaoCommand),
        typeof(RenovarCredencialEstacaoCommand),
    ];

    /// <summary>Names an old bundle connects to or subscribes by: renaming one silently drops the events.</summary>
    public static readonly Type[] Constantes = [typeof(PafHubRota), typeof(PafHubMetodos), typeof(PafHubChamadas), typeof(ProgramacaoAlterada)];

    public const string PrefixoPedido = "pedido ";

    public static IReadOnlyList<string> Forma(IEnumerable<Type> respostas, IEnumerable<Type> pedidos, IEnumerable<Type> constantes)
    {
        var linhas = respostas.SelectMany(t => Membros("resposta ", t))
            .Concat(pedidos.SelectMany(t => Membros(PrefixoPedido, t)))
            .Concat(constantes.SelectMany(ValoresConstantes));
        return linhas.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Breaks of <paramref name="atual"/> against <paramref name="snapshot"/>: a snapshot line missing (member removed,
    /// renamed, retyped or made nullable; constant renamed or revalued), or a request member added as required.
    /// </summary>
    public static IReadOnlyList<string> Quebras(IReadOnlyCollection<string> snapshot, IReadOnlyCollection<string> atual)
    {
        var atuais = atual.ToHashSet(StringComparer.Ordinal);
        var registradas = snapshot.ToHashSet(StringComparer.Ordinal);
        var removidas = snapshot.Where(l => !atuais.Contains(l)).Select(l => $"removido ou alterado: {l}");
        var pedidosObrigatorios = atual
            .Where(l => l.StartsWith(PrefixoPedido, StringComparison.Ordinal) && !registradas.Contains(l) && !l.EndsWith('?'))
            .Select(l => $"campo obrigatório novo em pedido (bundle antigo não envia): {l}");
        return removidas.Concat(pedidosObrigatorios).ToList();
    }

    private static IEnumerable<string> Membros(string prefixo, Type tipo)
    {
        var nulidade = new NullabilityInfoContext();
        return tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => $"{prefixo}{tipo.Name}.{p.Name}: {NomeDoTipo(nulidade.Create(p))}");
    }

    private static IEnumerable<string> ValoresConstantes(Type tipo) =>
        tipo.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => $"const {tipo.Name}.{f.Name} = \"{f.GetRawConstantValue()}\"");

    private static string NomeDoTipo(NullabilityInfo info)
    {
        var tipo = Nullable.GetUnderlyingType(info.Type) ?? info.Type;
        var nome = tipo.IsGenericType
            ? $"{tipo.Name[..tipo.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", info.GenericTypeArguments.Select(NomeDoTipo))}>"
            : tipo.Name;
        var anulavel = Nullable.GetUnderlyingType(info.Type) is not null || info.ReadState == NullabilityState.Nullable;
        return anulavel ? nome + "?" : nome;
    }
}
