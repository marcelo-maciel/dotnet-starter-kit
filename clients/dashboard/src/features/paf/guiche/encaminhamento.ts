// RF17 forwarding screen rules that do not need React. No imports on purpose: `node --test` runs this file as is
// (D21). The server re-validates all of it (F08): the screen only avoids offering what it would refuse.

export const MENSAGEM_SEM_FILAS = "Selecione a(s) fila(s) para qual deseja encaminhar a ficha";

export const MENSAGEM_SEM_PRIORIDADE_COMUM =
  "As filas marcadas não têm prioridade em comum com guichê que atenda. Ajuste a seleção de filas.";

export type PrioridadeDestino = { prioridadeId: string; nome: string };

export type DestinoEncaminhamento = {
  filaId: string;
  nome: string;
  foraDoHorario: boolean;
  prioridades: PrioridadeDestino[];
};

export function alternarFila(marcadas: readonly string[], filaId: string): string[] {
  return marcadas.includes(filaId) ? marcadas.filter((id) => id !== filaId) : [...marcadas, filaId];
}

/**
 * Prioridades covered in EVERY marked fila (REQ RF17 item 3), in the order of the first marked fila of the list, so
 * the order does not depend on the click order. No fila marked = empty: there is nothing to intersect.
 */
export function intersecaoPrioridades(
  destinos: readonly DestinoEncaminhamento[],
  marcadas: readonly string[],
): PrioridadeDestino[] {
  const selecionados = destinos.filter((d) => marcadas.includes(d.filaId));
  const [base, ...demais] = selecionados;
  if (base === undefined) return [];
  return base.prioridades.filter((p) =>
    demais.every((d) => d.prioridades.some((outra) => outra.prioridadeId === p.prioridadeId)),
  );
}

/**
 * The operator's own pick while it stays in the intersection; otherwise the original prioridade when it is in it
 * (RF17 item 3); otherwise none, so a prioridade the operator never saw is never sent. `escolhida` null = untouched.
 */
export function prioridadeSelecionada(
  intersecao: readonly PrioridadeDestino[],
  escolhida: string | null,
  originalId: string,
): string {
  const contem = (id: string) => intersecao.some((p) => p.prioridadeId === id);
  if (escolhida !== null && contem(escolhida)) return escolhida;
  return contem(originalId) ? originalId : "";
}

/** Why confirming is blocked, or null when it is allowed. */
export function bloqueioConfirmacao(
  marcadas: readonly string[],
  intersecao: readonly PrioridadeDestino[],
  prioridadeId: string,
): string | null {
  if (marcadas.length === 0) return MENSAGEM_SEM_FILAS;
  if (intersecao.length === 0) return MENSAGEM_SEM_PRIORIDADE_COMUM;
  return prioridadeId === "" ? "Selecione a prioridade." : null;
}
