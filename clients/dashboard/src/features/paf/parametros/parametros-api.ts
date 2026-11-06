import { apiFetch } from "@/lib/api-client";

/**
 * One RF06 key in the current unidade. `origem`: Fabrica, Empresa or Unidade; `valorEmpresa` and `valorUnidade` are
 * the stored rows (null = none), so the screen shows what an override hides.
 */
export type ParametroDto = {
  chave: string;
  grupo: string;
  tipo: "Booleano" | "Inteiro" | "Texto" | "Opcao" | "Tecla" | "Imagem";
  valor: string;
  origem: "Fabrica" | "Empresa" | "Unidade";
  padrao: string;
  valorEmpresa: string | null;
  valorUnidade: string | null;
  opcoes: string[];
};

/** `alteraEmpresa`: the caller is the company admin (RF23); `alteraUnidade`: he may override in this unidade. */
export type ParametrosDaUnidadeDto = {
  unidadeId: string;
  alteraEmpresa: boolean;
  alteraUnidade: boolean;
  itens: ParametroDto[];
};

const BASE = "/api/v1/paf/parametros";

export function listarParametros() {
  return apiFetch<ParametrosDaUnidadeDto>(BASE);
}

export function gravarPadraoDaEmpresa(chave: string, valor: string) {
  return apiFetch<void>(`${BASE}/empresa/${encodeURIComponent(chave)}`, { method: "PUT", body: JSON.stringify({ valor }) });
}

export function sobrescreverNaUnidade(chave: string, valor: string) {
  return apiFetch<void>(`${BASE}/unidade/${encodeURIComponent(chave)}`, { method: "PUT", body: JSON.stringify({ valor }) });
}

/** C19: a mídia of the current unidade as `GET midias` lists it (only what the logo picker needs). */
export type MidiaDaUnidade = { id: string; descricao: string; tipo: string; ativa: boolean };

// ponytail: the logo picker lists the mídias itself; upload and the full list live in the mídia screens (M10, wave 5).
export function listarMidiasDaUnidade() {
  return apiFetch<MidiaDaUnidade[]>("/api/v1/paf/midias");
}

export function voltarAoHerdado(chave: string) {
  return apiFetch<void>(`${BASE}/unidade/${encodeURIComponent(chave)}`, { method: "DELETE" });
}
