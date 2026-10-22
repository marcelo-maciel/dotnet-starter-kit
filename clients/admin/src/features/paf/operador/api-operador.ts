import { apiFetch } from "@/lib/api-client";
import type { PapelEstacaoMonitorado } from "./monitoramento";

// The Codefi operator works on one client tenant per request (arquitetura seção 6): every call names that tenant
// in the header explicitly, never by swapping the token store's tenant, which the refresh must keep as root.

export const PermissoesOperadorPaf = {
  UnidadesOperar: "Permissions.PAF.Unidades.Operar",
  ContratosManage: "Permissions.PAF.Contratos.Manage",
} as const;

export type PoliticaLimite = "Recusar" | "UltrapassarComAviso";

export type ContratoUnidadeDto = {
  id: string;
  unidadeId: string;
  inicioVigencia: string;
  fimVigencia: string | null;
  plano: string;
  limiteSessoes: number;
  politica: PoliticaLimite;
};

export type UnidadeOperadorDto = {
  id: string;
  nome: string;
  documentoFiscal: string | null;
  ativa: boolean;
  inicial: boolean;
  criadoEm: string;
  contratos: ContratoUnidadeDto[];
};

export type ContratoInput = {
  inicioVigencia: string;
  fimVigencia: string | null;
  plano: string;
  limiteSessoes: number;
  politica: PoliticaLimite;
};

const BASE = "/api/v1/paf";

function noTenant(tenantId: string): HeadersInit {
  return { tenant: tenantId };
}

export function listarUnidadesDoTenant(tenantId: string): Promise<UnidadeOperadorDto[]> {
  return apiFetch<UnidadeOperadorDto[]>(`${BASE}/operador/unidades`, { headers: noTenant(tenantId) });
}

export function criarContrato(tenantId: string, unidadeId: string, contrato: ContratoInput): Promise<string> {
  return apiFetch<string>(`${BASE}/contratos`, {
    method: "POST",
    headers: noTenant(tenantId),
    body: JSON.stringify({ unidadeId, ...contrato }),
  });
}

export function alterarContrato(tenantId: string, contratoId: string, contrato: ContratoInput): Promise<string> {
  return apiFetch<string>(`${BASE}/contratos/${encodeURIComponent(contratoId)}`, {
    method: "PUT",
    headers: noTenant(tenantId),
    body: JSON.stringify({ id: contratoId, ...contrato }),
  });
}

// O13 (RNF20): station monitoring of the client tenant, one tenant per request like the units.
export type MonitoramentoEstacoesDto = {
  agoraEmSaoPaulo: string;
  estadoPeloRegistro: boolean;
  papeis: PapelEstacaoMonitorado[];
};

export function listarEstacoesDoTenant(tenantId: string): Promise<MonitoramentoEstacoesDto> {
  return apiFetch<MonitoramentoEstacoesDto>(`${BASE}/operador/estacoes`, { headers: noTenant(tenantId) });
}
