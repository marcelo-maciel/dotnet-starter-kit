import { apiFetch } from "@/lib/api-client";
import type { FilaDto } from "@/features/paf/filas/filas-api";
import { consultaDaApi, type FiltrosEstatistica } from "@/features/paf/estatistica/estatistica-filtros";
import type { ResultadoEstatistica } from "@/features/paf/estatistica/estatistica-exportacao";

// RF07 reads the caller's effective scope (C01), never the current unidade, so the fila filter lists the filas of
// every unidade in that scope.

// Each type runs under a 15 s command timeout on the server (EstatisticaAtendimentos.PrazoConsulta); the client
// waits a little longer so the server's own 5xx reaches the screen instead of a client abort.
const PRAZO_CONSULTA_MS = 20_000;

export function consultarEstatistica(filtros: FiltrosEstatistica) {
  return apiFetch<ResultadoEstatistica>(`/api/v1/paf/estatistica?${consultaDaApi(filtros)}`, { timeoutMs: PRAZO_CONSULTA_MS });
}

export const listarFilasDoEscopo = () => apiFetch<FilaDto[]>("/api/v1/paf/filas/consolidadas");
