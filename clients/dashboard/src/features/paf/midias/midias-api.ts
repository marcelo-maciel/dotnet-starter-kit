import { apiFetch } from "@/lib/api-client";
import type { EnvioAssinado } from "@/features/paf/midias/envio-midia";
import type { TipoMidia } from "@/features/paf/midias/midias-regras";

// Mídia cadastro (RF03) of the session's current unidade, over the M03 and M04 routes.

const BASE = "/api/v1/paf/midias";

/** MidiaDto. */
export type MidiaDto = {
  id: string;
  unidadeId: string;
  descricao: string;
  tipo: TipoMidia;
  duracaoSegundos: number;
  ativa: boolean;
  versao: number;
  criadoEm: string;
};

export type UrlLeituraDto = { url: string; expiraEm: string };

export function listarMidias() {
  return apiFetch<MidiaDto[]>(BASE);
}

/** The name only picks the format on the server; it is never stored. */
export function pedirEnvio(nomeArquivo: string, contentType: string) {
  return apiFetch<EnvioAssinado>(`${BASE}/envios`, { method: "POST", body: JSON.stringify({ nomeArquivo, contentType }) });
}

export function pedirEnvioDaTroca(id: string, nomeArquivo: string, contentType: string) {
  return apiFetch<EnvioAssinado>(`${BASE}/${encodeURIComponent(id)}/envios`, {
    method: "POST",
    body: JSON.stringify({ midiaId: id, nomeArquivo, contentType }),
  });
}

/** Confirms the envio against the store and creates the mídia (idempotent per envio). */
export function criarMidia(envioId: string, descricao: string, duracaoSegundos: number) {
  return apiFetch<MidiaDto>(BASE, { method: "POST", body: JSON.stringify({ envioId, descricao, duracaoSegundos }) });
}

export function atualizarMidia(id: string, descricao: string, duracaoSegundos: number, versao: number) {
  return apiFetch<MidiaDto>(`${BASE}/${encodeURIComponent(id)}`, {
    method: "PUT",
    body: JSON.stringify({ id, descricao, duracaoSegundos, versao }),
  });
}

export function trocarArquivo(id: string, envioId: string, versao: number) {
  return apiFetch<MidiaDto>(`${BASE}/${encodeURIComponent(id)}/arquivo`, {
    method: "PUT",
    body: JSON.stringify({ id, envioId, versao }),
  });
}

export function ativarMidia(id: string) {
  return apiFetch<MidiaDto>(`${BASE}/${encodeURIComponent(id)}/enable`, { method: "POST" });
}

export function desativarMidia(id: string) {
  return apiFetch<MidiaDto>(`${BASE}/${encodeURIComponent(id)}/disable`, { method: "POST" });
}

/** M04 preview URL; 409 for an inactive mídia. */
export function obterUrlDaMidia(id: string) {
  return apiFetch<UrlLeituraDto>(`${BASE}/${encodeURIComponent(id)}/url`);
}
