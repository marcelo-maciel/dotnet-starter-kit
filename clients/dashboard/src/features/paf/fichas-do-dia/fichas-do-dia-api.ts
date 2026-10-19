import { apiFetch } from "@/lib/api-client";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import type { AtendimentoAlteradoDto } from "@/features/paf/guiche/guiche-api";
import type { ChamadaDto } from "@/features/paf/guiche/guiche-api";
import { consultaDosFiltros, type FiltroFichasDoDia } from "@/features/paf/fichas-do-dia/fichas-do-dia-regras";

// RF12 fichas do dia (F06): list and reprint under FichasDoDia.View; cancel (F04) and reabilitar (F05) under
// their own permissions. The unidade is always the session's current one.

export type AtendimentoDoDiaDto = {
  atendimentoId: string;
  fichaId: string;
  senha: string;
  filaId: string;
  fila: string;
  prioridadeId: string;
  prioridade: string;
  estado: string;
  versao: number;
  chegadaEm: string;
  elegivelDesde: string;
  esperaSegundos: number;
  estacaoId: string | null;
  guiche: string | null;
  numeroGuiche: number | null;
  nomeExibicaoGuiche: string | null;
  encaminhado: boolean;
};

export type FilaDasFichasDto = { id: string; nomeExibicao: string; ativa: boolean };

/** `data` is the day listed (the server's today when the filter had none); `truncada` = more lines than the cap. */
export type FichasDoDiaDto = {
  data: string;
  totalAguardando: number;
  truncada: boolean;
  atendimentos: AtendimentoDoDiaDto[];
  filas: FilaDasFichasDto[];
};

const BASE = "/api/v1/paf/fichas";

export const listarFichasDoDia = (filtro: FiltroFichasDoDia) =>
  apiFetch<FichasDoDiaDto>(`${BASE}/atendimentos${consultaDosFiltros(filtro)}`);

export const obterImpressao = (fichaId: string) => apiFetch<FichaEmitidaDto>(`${BASE}/${fichaId}/impressao`);

export const cancelarAtendimento = (atendimentoId: string) =>
  apiFetch<AtendimentoAlteradoDto>(`${BASE}/atendimentos/${atendimentoId}/cancelar`, { method: "POST" });

export const reabilitarAtendimento = (atendimentoId: string) =>
  apiFetch<AtendimentoAlteradoDto>(`${BASE}/atendimentos/${atendimentoId}/reabilitar`, { method: "POST" });

// F21 (RF04 selecionar ficha): what the session guichê could call, in call order, and the call of the chosen one.
// Both 409 when the guichê has the option off; the guichê comes from the session, never from the screen.
export type FichaEmEsperaDto = {
  atendimentoId: string;
  fichaId: string;
  senha: string;
  filaId: string;
  prioridadeId: string;
  elegivelDesde: string;
};

export const listarFichasEmEspera = () => apiFetch<FichaEmEsperaDto[]>("/api/v1/paf/guiche/fichas-em-espera");

export const chamarFichaEscolhida = (atendimentoId: string) =>
  apiFetch<ChamadaDto>(`/api/v1/paf/guiche/atendimentos/${atendimentoId}/chamar`, { method: "POST" });
