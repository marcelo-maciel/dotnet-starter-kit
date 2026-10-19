import { apiFetch } from "@/lib/api-client";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import type { FilaEmissaoGuicheDto } from "@/features/paf/guiche/emissao-guiche-regras";

/** The guichê emission panel; `ultimaFicha` is this guichê's last ficha of today as printed now, null when none. */
export type EmissaoGuicheDto = { filas: FilaEmissaoGuicheDto[]; ultimaFicha: FichaEmitidaDto | null };

// Both need only Fichas.Emitir (basic) and a selected guichê; the server takes the station from the session.
export function obterEmissaoGuiche() {
  return apiFetch<EmissaoGuicheDto>("/api/v1/paf/guiche/emissao");
}

export function emitirFichaNoGuiche(filaId: string, prioridadeId: string, chaveIdempotencia: string) {
  return apiFetch<FichaEmitidaDto>("/api/v1/paf/guiche/fichas", {
    method: "POST",
    headers: { "Idempotency-Key": chaveIdempotencia },
    body: JSON.stringify({ filaId, prioridadeId }),
  });
}
