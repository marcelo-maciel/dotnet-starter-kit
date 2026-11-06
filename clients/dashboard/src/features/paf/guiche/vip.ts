// RF19 VIP (D02): the attendant types the printed senha, its date and picks the fila; the search lists every ficha
// printed with that senha (owner decision 19/10/2026) and the call takes the one picked. Pure, node --test (D21).

export const PERMISSAO_VIP = "Permissions.PAF.Atendimentos.Vip";

// Letter up to 5 characters plus an int of at most ten digits; the server validates the same ceiling.
export const SENHA_MAX = 15;

export type PedidoVip = { senha: string; data: string; filaId: string };

export type ResultadoPedidoVip = { ok: true; pedido: PedidoVip } | { ok: false; erro: string };

const DATA_ISO = /^\d{4}-\d{2}-\d{2}$/;

/** Same normalization as the server and the letter cadastro: the senha is printed upper case. */
export function montarPedidoVip(senha: string, data: string, filaId: string): ResultadoPedidoVip {
  const normalizada = senha.trim().toUpperCase();
  if (normalizada === "") return { ok: false, erro: "Informe a senha impressa na ficha." };
  if (normalizada.length > SENHA_MAX) return { ok: false, erro: `A senha tem no máximo ${SENHA_MAX} caracteres.` };
  if (!DATA_ISO.test(data)) return { ok: false, erro: "Informe a data da ficha." };
  if (filaId === "") return { ok: false, erro: "Selecione a fila." };
  return { ok: true, pedido: { senha: normalizada, data, filaId } };
}

/** The ficha's day is the America/Sao_Paulo calendar day, never the browser's nor UTC's. */
export function hojeEmSaoPaulo(agora: Date): string {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: "America/Sao_Paulo",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(agora);
}

export const MENSAGEM_NENHUMA_FICHA = "Nenhuma ficha com esta senha nesta data nesta unidade.";

/** The search answer the form acts on; the order is the server's (emission order). */
export type DecisaoVip<T> = { tipo: "nenhuma" } | { tipo: "unica"; ficha: T } | { tipo: "escolher"; fichas: T[] };

/** One match is called at once, as before the list existed; two or more wait for the attendant's pick. */
export function decidirVip<T>(fichas: readonly T[]): DecisaoVip<T> {
  if (fichas.length === 0) return { tipo: "nenhuma" };
  if (fichas.length === 1) return { tipo: "unica", ficha: fichas[0] };
  return { tipo: "escolher", fichas: [...fichas] };
}

export function podeChamarVip(permissoes: readonly string[]): boolean {
  return permissoes.includes(PERMISSAO_VIP);
}
