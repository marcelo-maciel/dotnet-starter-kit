import { textoNaTela, type LinhaEstatistica, type ResultadoEstatistica } from "@/features/paf/estatistica/estatistica-exportacao";

// One table for the ten types: columns come from the API (ResultadoEstatisticaDto), so a new type needs no screen change.
function Linha({ linha, resultado, total }: { linha: LinhaEstatistica; resultado: ResultadoEstatistica; total: boolean }) {
  return (
    <tr className={total ? "border-t border-[var(--color-border)] font-semibold" : "border-t border-[var(--color-border)]"} data-total={total ? (linha.unidadeId === null ? "escopo" : "unidade") : undefined}>
      {resultado.colunas.map((coluna, i) => (
        <td key={coluna.chave + i} data-coluna={coluna.chave} className={coluna.tipo === "Texto" ? "px-3 py-2" : "px-3 py-2 text-right tabular-nums"}>
          {textoNaTela(coluna, linha.celulas[i] ?? { texto: null, numero: null })}
        </td>
      ))}
    </tr>
  );
}

export function TabelaEstatistica({ resultado }: { resultado: ResultadoEstatistica }) {
  if (resultado.linhas.length === 0) {
    return <p data-sem-dados>Nenhum dado no período e nos filtros escolhidos.</p>;
  }
  return (
    <div className="overflow-x-auto rounded-xl border border-[var(--color-border)]">
      <table className="w-full text-sm" data-tabela-estatistica>
        <thead className="bg-[var(--color-card)] text-left">
          <tr>
            {resultado.colunas.map((coluna, i) => (
              <th key={coluna.chave + i} scope="col" className={coluna.tipo === "Texto" ? "px-3 py-2" : "px-3 py-2 text-right"}>
                {coluna.titulo}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {resultado.linhas.map((linha, i) => (
            <Linha key={i} linha={linha} resultado={resultado} total={false} />
          ))}
        </tbody>
        <tfoot>
          {resultado.totais.map((linha, i) => (
            <Linha key={i} linha={linha} resultado={resultado} total />
          ))}
        </tfoot>
      </table>
    </div>
  );
}
