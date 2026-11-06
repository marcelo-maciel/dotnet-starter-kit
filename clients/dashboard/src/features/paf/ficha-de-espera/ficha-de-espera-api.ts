import { apiFetch } from "@/lib/api-client";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import { consultaDaReimpressao, type PedidoReimpressao } from "@/features/paf/ficha-de-espera/ficha-de-espera-regras";

/** RF20 (F15): the stored fichas of the current unit matching senha, day and fila; 404 when there is none. */
export const buscarFichaDeEspera = (pedido: PedidoReimpressao) =>
  apiFetch<FichaEmitidaDto[]>(`/api/v1/paf/fichas/reimpressao${consultaDaReimpressao(pedido)}`);
