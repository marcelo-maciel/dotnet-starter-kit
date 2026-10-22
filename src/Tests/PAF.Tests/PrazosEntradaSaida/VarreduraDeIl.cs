using System.Reflection;
using System.Reflection.Emit;

namespace PAF.Tests.PrazosEntradaSaida;

internal sealed record ReferenciaNoIl(string Metodo, string Membro);

/// <summary>
/// Reads the compiled IL of the given types, so lambdas, async state machines, local functions and generated
/// code are covered as well, and returns every member referenced by a call or a static field load.
/// </summary>
internal static class VarreduraDeIl
{
    private const BindingFlags TodosOsMembros =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesPorValor = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    internal static List<ReferenciaNoIl> Referencias(IEnumerable<Type> tipos)
    {
        var referencias = new List<ReferenciaNoIl>();
        foreach (var tipo in tipos)
        {
            var metodos = tipo.GetMethods(TodosOsMembros).Cast<MethodBase>().Concat(tipo.GetConstructors(TodosOsMembros));
            foreach (var metodo in metodos)
            {
                referencias.AddRange(ReferenciasDoMetodo(metodo));
            }
        }

        return referencias;
    }

    private static IEnumerable<ReferenciaNoIl> ReferenciasDoMetodo(MethodBase metodo)
    {
        byte[]? il = metodo.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        string nomeDoMetodo = $"{metodo.DeclaringType?.FullName}.{metodo.Name}";
        // Generic context so tokens inside generic types and methods resolve to their open definitions.
        Type[]? argumentosDoTipo = metodo.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        Type[]? argumentosDoMetodo = metodo.IsGenericMethod ? metodo.GetGenericArguments() : null;

        int posicao = 0;
        while (posicao < il.Length)
        {
            var opCode = LerOpCode(il, ref posicao);
            if (opCode.OperandType == OperandType.InlineMethod || opCode.OperandType == OperandType.InlineField)
            {
                int token = BitConverter.ToInt32(il, posicao);
                var membro = metodo.Module.ResolveMember(token, argumentosDoTipo, argumentosDoMetodo);
                if (membro is not null)
                {
                    yield return new ReferenciaNoIl(nomeDoMetodo, $"{membro.DeclaringType?.FullName}.{membro.Name}");
                }
            }

            posicao += TamanhoDoOperando(opCode, il, posicao);
        }
    }

    private static OpCode LerOpCode(byte[] il, ref int posicao)
    {
        short valor = il[posicao++];
        // Two-byte opcodes are prefixed by 0xFE and stored as negative shorts in OpCodes.
        if (valor == 0xFE)
        {
            valor = unchecked((short)(0xFE00 | il[posicao++]));
        }

        return OpCodesPorValor[valor];
    }

    private static int TamanhoDoOperando(OpCode opCode, byte[] il, int posicao) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        // switch: a 4-byte count followed by that many 4-byte targets.
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, posicao) * 4),
        _ => 4,
    };
}
