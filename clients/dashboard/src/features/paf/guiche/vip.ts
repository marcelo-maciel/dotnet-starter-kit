// RF19 VIP (D02): the attendant types the printed senha, its date and picks the fila. Pure, tested with node --test (D21).

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

export function podeChamarVip(permissoes: readonly string[]): boolean {
  return permissoes.includes(PERMISSAO_VIP);
}
