// Painel display list (RF04 painel block, M06 contract): an ordered list of mídia ids, repeats allowed, replaced
// whole by PUT estacoes/{id}/midias. The position is the order the painel loops through.

export const MAXIMO_ITENS = 100; // SubstituirMidiasDaEstacaoCommand.MaximoItens

export type ListaAlterada = { ok: true; itens: string[] } | { ok: false; mensagem: string };

export function adicionar(itens: readonly string[], midiaId: string): ListaAlterada {
  if (midiaId === "") return { ok: false, mensagem: "Escolha a mídia a acrescentar." };
  if (itens.length >= MAXIMO_ITENS) return { ok: false, mensagem: `A lista de exibição tem no máximo ${MAXIMO_ITENS} itens.` };
  return { ok: true, itens: [...itens, midiaId] };
}

export function remover(itens: readonly string[], indice: number): string[] {
  return itens.filter((_, i) => i !== indice);
}

/** Swaps the item with its neighbour; a move past either end leaves the list as it is. */
export function mover(itens: readonly string[], indice: number, deslocamento: -1 | 1): string[] {
  const destino = indice + deslocamento;
  if (indice < 0 || indice >= itens.length || destino < 0 || destino >= itens.length) return [...itens];
  const copia = [...itens];
  [copia[indice], copia[destino]] = [copia[destino], copia[indice]];
  return copia;
}

export type MidiaDaLista = { id: string; duracaoSegundos: number; ativa: boolean };

/** One pass of the loop: the painel skips inactive and unknown mídias, so they add nothing. */
export function duracaoDoLaco(itens: readonly string[], midias: readonly MidiaDaLista[]): number {
  return itens.reduce((total, id) => {
    const midia = midias.find((m) => m.id === id);
    return midia?.ativa ? total + midia.duracaoSegundos : total;
  }, 0);
}

export function iguais(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((id, i) => id === b[i]);
}
