import { normalizarSenha, senhaValida } from "../consultar-ficha/consultar-ficha-regras.ts";

export type PedidoReimpressao = { senha: string; data: string; filaId: string };

/** RF20 needs all three (D02): the printed senha, its day and the fila printed on the ficha. */
export function pedidoCompleto(pedido: PedidoReimpressao): boolean {
  return senhaValida(pedido.senha) && pedido.data !== "" && pedido.filaId !== "";
}

/** Query string of `GET fichas/reimpressao`, with the senha folded as the server folds it. */
export function consultaDaReimpressao(pedido: PedidoReimpressao): string {
  const parametros = new URLSearchParams({ senha: normalizarSenha(pedido.senha), data: pedido.data, filaId: pedido.filaId });
  return `?${parametros.toString()}`;
}
