import { apiFetch } from "@/lib/api-client";

/** Plan and limit are the Codefi's to write (RF23): the screen only shows them. `fimVigencia` is a calendar day. */
export type ContratoVigenteDto = { plano: string; limiteSessoes: number; fimVigencia: string | null };

/** Unidades of the caller's effective scope, principal first; `contrato` is null when none is in force today. */
export type UnidadeCadastroDto = {
  id: string;
  nome: string;
  documentoFiscal: string | null;
  ativa: boolean;
  criadoEm: string;
  contrato: ContratoVigenteDto | null;
};

export type AdministracaoUnidadesDto = { administraUnidades: boolean };

const BASE = "/api/v1/paf/unidades";

export function listarUnidadesDoCadastro() {
  return apiFetch<UnidadeCadastroDto[]>(BASE);
}

export function obterAdministracaoUnidades() {
  return apiFetch<AdministracaoUnidadesDto>(`${BASE}/administracao`);
}

export function criarUnidade(nome: string, documentoFiscal: string | null) {
  return apiFetch<string>(BASE, { method: "POST", body: JSON.stringify({ nome, documentoFiscal }) });
}

export function atualizarUnidade(id: string, nome: string, documentoFiscal: string | null) {
  return apiFetch<string>(`${BASE}/${id}`, { method: "PUT", body: JSON.stringify({ id, nome, documentoFiscal }) });
}

export function desativarUnidade(id: string) {
  return apiFetch<string>(`${BASE}/${id}/disable`, { method: "POST" });
}

export function reativarUnidade(id: string) {
  return apiFetch<string>(`${BASE}/${id}/enable`, { method: "POST" });
}
