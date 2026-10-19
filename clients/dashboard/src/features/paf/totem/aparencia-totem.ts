// Totem appearance (RF14, RF04 bloco Emissão de Fichas, RNF19). Pure module (node --test, D21).
// The station block is stored by the cadastro (C04) nested under `emissao`; the per-fila colors come on each button
// (C09). A missing or malformed value keeps the screen default, so a station never saved through the cadastro looks
// exactly as before.
import { formatoValido } from "../aparencia/contraste.ts";

export type AparenciaEmissao = {
  titulo: string | null;
  tamanhoFontePx: number | null;
  tamanhoBotaoPx: number | null;
  /** All three or none: the cadastro checked each text color against this background only as a set. */
  cores: { fundo: string; fonte: string; descricao: string } | null;
  /** C17: title and messages, drawn straight on the screen background (white or the image); null = black. */
  corTexto: string | null;
};

export type AparenciaDoBotao = {
  fonte: string | null;
  corFonte: string | null;
  corBotao: string | null;
  corBotaoSelecionado: string | null;
};

/** Inline values for one button; null = keep the default class. */
export type EstiloBotaoTotem = {
  fundo: string | null;
  corFila: string | null;
  corDescricao: string | null;
  familiaFonte: string | null;
  tamanhoFontePx: number | null;
  tamanhoMinimoPx: number | null;
};

export const APARENCIA_EMISSAO_PADRAO: AparenciaEmissao = {
  titulo: null,
  tamanhoFontePx: null,
  tamanhoBotaoPx: null,
  cores: null,
  corTexto: null,
};

function objeto(valor: unknown): Record<string, unknown> | null {
  return typeof valor === "object" && valor !== null && !Array.isArray(valor) ? (valor as Record<string, unknown>) : null;
}

function numeroPositivo(valor: unknown): number | null {
  return typeof valor === "number" && Number.isFinite(valor) && valor > 0 ? valor : null;
}

function cor(valor: unknown): string | null {
  return typeof valor === "string" && formatoValido(valor) ? valor : null;
}

function textoNaoVazio(valor: unknown): string | null {
  return typeof valor === "string" && valor.trim() !== "" ? valor : null;
}

export function lerAparenciaEmissao(configuracao: string | null): AparenciaEmissao {
  if (configuracao === null) return APARENCIA_EMISSAO_PADRAO;
  let raiz: Record<string, unknown> | null;
  try {
    raiz = objeto(JSON.parse(configuracao));
  } catch {
    // A malformed block must not take the totem down; the default appearance keeps it emitting.
    return APARENCIA_EMISSAO_PADRAO;
  }
  const bloco = objeto(raiz?.emissao);
  if (bloco === null) return APARENCIA_EMISSAO_PADRAO;
  const fundo = cor(bloco.corFundoBotao);
  const fonte = cor(bloco.corFonteBotao);
  const descricao = cor(bloco.corDescricaoBotao);
  return {
    titulo: textoNaoVazio(bloco.titulo),
    tamanhoFontePx: numeroPositivo(bloco.tamanhoFontePx),
    tamanhoBotaoPx: numeroPositivo(bloco.tamanhoBotaoPx),
    cores: fundo !== null && fonte !== null && descricao !== null ? { fundo, fonte, descricao } : null,
    corTexto: cor(bloco.corTexto),
  };
}

// Contrast (RNF19) is only guaranteed for pairs the cadastro validated together: the fila's CorFonte against each
// of its button colors (C09), or the station's text colors against the station's button background (C04). Mixing
// one side of each would put on screen a combination nobody checked, so a pair is used whole or not at all.
export function estiloDoBotao(
  aparencia: AparenciaEmissao,
  botao: AparenciaDoBotao,
  selecionado: boolean,
): EstiloBotaoTotem {
  const base = {
    familiaFonte: textoNaoVazio(botao.fonte),
    tamanhoFontePx: aparencia.tamanhoFontePx,
    tamanhoMinimoPx: aparencia.tamanhoBotaoPx,
  };
  // The touched button shows the fila's "selecionada" color while its emission runs; without one it keeps its own.
  const candidatas = selecionado ? [botao.corBotaoSelecionado, botao.corBotao] : [botao.corBotao];
  const corDaFila = candidatas.find((c): c is string => formatoValido(c));
  if (corDaFila !== undefined && formatoValido(botao.corFonte)) {
    return { ...base, fundo: corDaFila, corFila: botao.corFonte, corDescricao: botao.corFonte };
  }
  if (aparencia.cores !== null) {
    return { ...base, fundo: aparencia.cores.fundo, corFila: aparencia.cores.fonte, corDescricao: aparencia.cores.descricao };
  }
  return { ...base, fundo: null, corFila: null, corDescricao: null };
}
