using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Estacoes;

/// <summary>Janela de horário de uma fila da unidade, com o indicador de ativa, como o aviso do RNF20 a lê.</summary>
public readonly record struct JanelaDaFila(bool Ativa, TimeOnly? HoraInicial, TimeOnly? HoraFinal);

/// <summary>
/// Horário de operação da unidade para o aviso de estação sem conexão (RNF20, D24 aceita em 17/10/2026): a união das
/// janelas das filas ativas. Cada janela segue a regra da emissão (<see cref="Fila.JanelaPermiteEmissao"/>: pontas
/// inclusivas, fila sem janela = sem restrição), então a unidade "opera" exatamente quando alguma fila ativa emite.
/// </summary>
public static class HorarioDeOperacao
{
    /// <summary>
    /// <paramref name="hora"/> é a hora de America/Sao_Paulo pelo relógio do PostgreSQL. Unidade sem fila ativa nunca
    /// está em operação: sem fila não há o que emitir, e o aviso seria falso.
    /// </summary>
    public static bool EmOperacao(IEnumerable<JanelaDaFila> filas, TimeOnly hora)
    {
        ArgumentNullException.ThrowIfNull(filas);
        return filas.Any(f => f.Ativa && Fila.JanelaPermiteEmissao(f.HoraInicial, f.HoraFinal, hora));
    }
}
