import { apiFetch } from "@/lib/api-client";
import type { DestinoEncaminhamento } from "@/features/paf/guiche/encaminhamento";

// RF17 calls (F08), both under Atendimentos.Encaminhar. Kept out of guiche-api.ts so the wave-2 tasks that touch
// the guichê do not edit the same file.

/** The atendimento started on the session's guichê and the active filas of the unit other than its own. */
export type DestinosEncaminhamentoDto = {
  atendimentoId: string;
  senha: string;
  filaOrigemId: string;
  filaOrigem: string;
  prioridadeOriginalId: string;
  destinos: DestinoEncaminhamento[];
};

export type AtendimentoEncaminhadoDto = {
  atendimentoId: string;
  versao: number;
  destinos: { atendimentoId: string; filaId: string }[];
};

export function listarDestinosEncaminhamento() {
  return apiFetch<DestinosEncaminhamentoDto>("/api/v1/paf/guiche/encaminhamento/destinos");
}

export function encaminharAtendimento(atendimentoId: string, filaIds: string[], prioridadeId: string) {
  return apiFetch<AtendimentoEncaminhadoDto>(`/api/v1/paf/guiche/atendimentos/${atendimentoId}/encaminhar`, {
    method: "POST",
    body: JSON.stringify({ filaIds, prioridadeId }),
  });
}
