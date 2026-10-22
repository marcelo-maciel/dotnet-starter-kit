// Pure rules of the station cadastro screen (RF04, C04). The server is the judge (ConfiguracaoDaEstacao); these
// mirror it only to show the problem before the round trip, so a value that passes here can still get a 400.

export type Papel = "Emissao" | "Guiche" | "Painel";

export const TIPOS_ESTACAO = [
  "Administracao",
  "Emissao",
  "EmissaoGuiche",
  "EmissaoPainel",
  "Guiche",
  "GuichePainel",
  "Painel",
] as const;

export type TipoEstacao = (typeof TIPOS_ESTACAO)[number];

export const ROTULO_TIPO: Record<TipoEstacao, string> = {
  Administracao: "Administração",
  Emissao: "Emissão de Fichas (auto atendimento)",
  EmissaoGuiche: "Emissão de Fichas e Guichê",
  EmissaoPainel: "Emissão de Fichas e Painel Chamador",
  Guiche: "Guichê",
  GuichePainel: "Guichê e Painel Chamador",
  Painel: "Painel Chamador",
};

const PAPEIS: Record<TipoEstacao, readonly Papel[]> = {
  Administracao: [],
  Emissao: ["Emissao"],
  EmissaoGuiche: ["Emissao", "Guiche"],
  EmissaoPainel: ["Emissao", "Painel"],
  Guiche: ["Guiche"],
  GuichePainel: ["Guiche", "Painel"],
  Painel: ["Painel"],
};

export function papeisDoTipo(tipo: TipoEstacao): readonly Papel[] {
  return PAPEIS[tipo];
}

export const RAZAO_MINIMA = 4.5;
export const ALVO_MINIMO_BOTAO_PX = 44;
export const TEMPO_MINIMO_S = 1;
export const TEMPO_MAXIMO_S = 600;

const COR = /^#[0-9A-Fa-f]{6}$/;

export function corValida(cor: string): boolean {
  return COR.test(cor);
}

function canal(cor: string, inicio: number): number {
  const c = Number.parseInt(cor.slice(inicio, inicio + 2), 16) / 255;
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function luminancia(cor: string): number {
  return 0.2126 * canal(cor, 1) + 0.7152 * canal(cor, 3) + 0.0722 * canal(cor, 5);
}

/** WCAG 2.2 contrast ratio, lighter over darker; both colors must pass `corValida`. */
export function razaoContraste(cor1: string, cor2: string): number {
  const l1 = luminancia(cor1);
  const l2 = luminancia(cor2);
  return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
}

/** Selected ids of one list, in the order the operator gave them. */
export function mover(lista: readonly string[], id: string, delta: -1 | 1): string[] {
  const i = lista.indexOf(id);
  const j = i + delta;
  if (i < 0 || j < 0 || j >= lista.length) return [...lista];
  const copia = [...lista];
  [copia[i], copia[j]] = [copia[j], copia[i]];
  return copia;
}

export function alternar(lista: readonly string[], id: string): string[] {
  return lista.includes(id) ? lista.filter((x) => x !== id) : [...lista, id];
}

export type EmissaoForm = {
  titulo: string;
  tamanhoFontePx: string;
  tamanhoBotaoPx: string;
  corFundoBotao: string;
  corFonteBotao: string;
  corDescricaoBotao: string;
  prazoCicloEmissaoSegundos: string;
  /** C17: title and messages on the screen background; empty = black. */
  corTexto: string;
  imagemFundo: ImagemFundoDto | null;
};

export type GuicheForm = {
  numero: string;
  nomeExibicao: string;
  ladoSeta: "" | "Esquerda" | "Direita";
  respeitarOrdemFilas: boolean;
  selecionarFicha: boolean;
};

export type PainelForm = {
  tempoExibicaoChamadaSegundos: string;
  mostrarDataHora: boolean;
  tamanhoFontePx: string;
  corFundo: string;
  corFonteFilas: string;
  corFonteChamada: string;
  corFonteAlternada: string;
  modo: "" | "Normal" | "Informativo";
  imagemFundo: ImagemFundoDto | null;
};

/**
 * C17: an image mídia as block background, with the luminance sample measured in the browser (`contraste-imagem.ts`)
 * on the mídia at `versaoMidia`. The server refuses a sample of an older version.
 */
export type ImagemFundoDto = { midiaId: string; versaoMidia: number; luminancias: string };

export type BlocoEmissaoDto = {
  titulo: string | null;
  tamanhoFontePx: number | null;
  tamanhoBotaoPx: number | null;
  corFundoBotao: string | null;
  corFonteBotao: string | null;
  corDescricaoBotao: string | null;
  prazoCicloEmissaoSegundos: number | null;
  corTexto?: string | null;
  imagemFundo?: ImagemFundoDto | null;
};

export type BlocoGuicheDto = {
  numero: number;
  nomeExibicao: string | null;
  ladoSeta: string | null;
  respeitarOrdemFilas: boolean;
  selecionarFicha: boolean;
};

export type BlocoPainelDto = {
  tempoExibicaoChamadaSegundos: number | null;
  mostrarDataHora: boolean | null;
  tamanhoFontePx: number | null;
  corFundo: string | null;
  corFonteFilas: string | null;
  corFonteChamada: string | null;
  corFonteAlternada: string | null;
  modo: string | null;
  imagemFundo?: ImagemFundoDto | null;
};

export type BlocosEstacaoDto = {
  emissao: BlocoEmissaoDto | null;
  guiche: BlocoGuicheDto | null;
  painel: BlocoPainelDto | null;
};

export const EMISSAO_VAZIA: EmissaoForm = {
  titulo: "",
  tamanhoFontePx: "",
  tamanhoBotaoPx: "",
  corFundoBotao: "",
  corFonteBotao: "",
  corDescricaoBotao: "",
  prazoCicloEmissaoSegundos: "",
  corTexto: "",
  imagemFundo: null,
};

export const GUICHE_VAZIO: GuicheForm = {
  numero: "",
  nomeExibicao: "",
  ladoSeta: "",
  respeitarOrdemFilas: false,
  selecionarFicha: false,
};

export const PAINEL_VAZIO: PainelForm = {
  tempoExibicaoChamadaSegundos: "",
  mostrarDataHora: true,
  tamanhoFontePx: "",
  corFundo: "",
  corFonteFilas: "",
  corFonteChamada: "",
  corFonteAlternada: "",
  modo: "",
  imagemFundo: null,
};

function texto(valor: string): string | null {
  const limpo = valor.trim();
  return limpo === "" ? null : limpo;
}

// A non-integer stays as NaN so the server answers 400 with its message instead of the screen sending a default.
function inteiro(valor: string): number | null {
  const limpo = valor.trim();
  if (limpo === "") return null;
  return /^-?\d+$/.test(limpo) ? Number(limpo) : Number.NaN;
}

/** The request body blocks: one per papel of the type, never one the type does not have. */
export function montarBlocos(
  tipo: TipoEstacao,
  emissao: EmissaoForm,
  guiche: GuicheForm,
  painel: PainelForm,
): BlocosEstacaoDto {
  const papeis = papeisDoTipo(tipo);
  return {
    emissao: papeis.includes("Emissao")
      ? {
          titulo: texto(emissao.titulo),
          tamanhoFontePx: inteiro(emissao.tamanhoFontePx),
          tamanhoBotaoPx: inteiro(emissao.tamanhoBotaoPx),
          corFundoBotao: texto(emissao.corFundoBotao),
          corFonteBotao: texto(emissao.corFonteBotao),
          corDescricaoBotao: texto(emissao.corDescricaoBotao),
          prazoCicloEmissaoSegundos: inteiro(emissao.prazoCicloEmissaoSegundos),
          corTexto: texto(emissao.corTexto),
          imagemFundo: emissao.imagemFundo,
        }
      : null,
    guiche: papeis.includes("Guiche")
      ? {
          numero: inteiro(guiche.numero) ?? Number.NaN,
          nomeExibicao: texto(guiche.nomeExibicao),
          ladoSeta: guiche.ladoSeta === "" ? null : guiche.ladoSeta,
          respeitarOrdemFilas: guiche.respeitarOrdemFilas,
          selecionarFicha: guiche.selecionarFicha,
        }
      : null,
    painel: papeis.includes("Painel")
      ? {
          tempoExibicaoChamadaSegundos: inteiro(painel.tempoExibicaoChamadaSegundos),
          mostrarDataHora: painel.mostrarDataHora,
          tamanhoFontePx: inteiro(painel.tamanhoFontePx),
          corFundo: texto(painel.corFundo),
          corFonteFilas: texto(painel.corFonteFilas),
          corFonteChamada: texto(painel.corFonteChamada),
          corFonteAlternada: texto(painel.corFonteAlternada),
          modo: painel.modo === "" ? null : painel.modo,
          imagemFundo: painel.imagemFundo,
        }
      : null,
  };
}

function textoDe(valor: number | string | null | undefined): string {
  return valor === null || valor === undefined ? "" : String(valor);
}

/** Form state from the stored blocks; an absent block gives the empty form. */
export function formularios(blocos: BlocosEstacaoDto | null): {
  emissao: EmissaoForm;
  guiche: GuicheForm;
  painel: PainelForm;
} {
  const e = blocos?.emissao;
  const g = blocos?.guiche;
  const p = blocos?.painel;
  return {
    emissao: e
      ? {
          titulo: textoDe(e.titulo),
          tamanhoFontePx: textoDe(e.tamanhoFontePx),
          tamanhoBotaoPx: textoDe(e.tamanhoBotaoPx),
          corFundoBotao: textoDe(e.corFundoBotao),
          corFonteBotao: textoDe(e.corFonteBotao),
          corDescricaoBotao: textoDe(e.corDescricaoBotao),
          prazoCicloEmissaoSegundos: textoDe(e.prazoCicloEmissaoSegundos),
          corTexto: textoDe(e.corTexto),
          imagemFundo: e.imagemFundo ?? null,
        }
      : EMISSAO_VAZIA,
    guiche: g
      ? {
          numero: textoDe(g.numero),
          nomeExibicao: textoDe(g.nomeExibicao),
          ladoSeta: g.ladoSeta === "Esquerda" || g.ladoSeta === "Direita" ? g.ladoSeta : "",
          respeitarOrdemFilas: g.respeitarOrdemFilas,
          selecionarFicha: g.selecionarFicha,
        }
      : GUICHE_VAZIO,
    painel: p
      ? {
          tempoExibicaoChamadaSegundos: textoDe(p.tempoExibicaoChamadaSegundos),
          mostrarDataHora: p.mostrarDataHora ?? true,
          tamanhoFontePx: textoDe(p.tamanhoFontePx),
          corFundo: textoDe(p.corFundo),
          corFonteFilas: textoDe(p.corFonteFilas),
          corFonteChamada: textoDe(p.corFonteChamada),
          corFonteAlternada: textoDe(p.corFonteAlternada),
          modo: p.modo === "Normal" || p.modo === "Informativo" ? p.modo : "",
          imagemFundo: p.imagemFundo ?? null,
        }
      : PAINEL_VAZIO,
  };
}

/**
 * Preview of the server's color rule for one block: all colors or none, each `#RRGGBB`, each text against the
 * background at least 4.5:1. Empty list = nothing to warn about.
 */
export function avisosDeCor(fundo: [string, string], textos: [string, string][]): string[] {
  const todas = [fundo, ...textos];
  const preenchidas = todas.filter(([, cor]) => cor.trim() !== "");
  if (preenchidas.length === 0) return [];
  if (preenchidas.length < todas.length) return ["Informe todas as cores do bloco ou nenhuma."];
  const invalidas = todas.filter(([, cor]) => !corValida(cor.trim()));
  if (invalidas.length > 0) return invalidas.map(([nome]) => `${nome}: use o formato #RRGGBB.`);
  return textos
    .map(([nome, cor]) => [nome, razaoContraste(cor.trim(), fundo[1].trim())] as const)
    .filter(([, razao]) => razao < RAZAO_MINIMA)
    .map(([nome, razao]) => `${nome} sobre ${fundo[0]}: contraste ${(Math.floor(razao * 100) / 100).toFixed(2)}:1, mínimo ${RAZAO_MINIMA}:1.`);
}
