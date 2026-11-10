// RF06 "exibir informação detalhada de fila" on the guichê screen (C20, D30): on, the waiting count of each fila of the
// station; off, only the total. The server decides which one comes (`porFila` null when off), the screen only words it.

export type AguardandoFilaDto = { filaId: string; fila: string; quantidade: number };

/** GET api/v1/paf/guiche/aguardando. */
export type AguardandoGuicheDto = { total: number; porFila: AguardandoFilaDto[] | null };

export function textoAguardando(quantidade: number): string {
  if (quantidade === 0) return "Nenhuma ficha aguardando";
  return quantidade === 1 ? "1 ficha aguardando" : `${quantidade} fichas aguardando`;
}
