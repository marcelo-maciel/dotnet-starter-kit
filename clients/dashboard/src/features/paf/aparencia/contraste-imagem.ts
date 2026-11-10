// Contrast of a text color against a background image (C17, REQ 8.1 l.990, D16): the worst point decides.
// The cadastro measures the image here (the API has no image decoder) and sends the sample; the server applies the same
// rule to it (src/Modules/PAF/Modules.PAF/Aparencia/ContrasteImagem.cs). Pure module (node --test, D21).
import { RAZAO_MINIMA, formatoValido } from "./contraste.ts";

export const FAIXAS = 256;
export const DIGITOS_DA_AMOSTRA = FAIXAS / 4;

function linear(c8: number): number {
  const c = c8 / 255;
  return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

function luminanciaRgb(r: number, g: number, b: number): number {
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

function rgbDe(cor: string): [number, number, number] {
  if (!formatoValido(cor)) throw new Error(`Cor fora do formato #RRGGBB: ${cor}`);
  return [1, 3, 5].map((i) => parseInt(cor.slice(i, i + 2), 16)) as [number, number, number];
}

/**
 * Luminance occupancy of RGBA pixels (canvas `ImageData.data`), each pixel composed over `fundo` the way the browser
 * paints a translucent image on the screen background: 64 hex digits, band i = luminance in [i/256, (i+1)/256], most
 * significant bit of each digit first. A fully transparent pixel shows the background itself.
 */
export function amostrarLuminancias(rgba: ArrayLike<number>, fundo: string): string {
  if (rgba.length === 0 || rgba.length % 4 !== 0) throw new Error("Pixels RGBA vazios ou incompletos.");
  const [fr, fg, fb] = rgbDe(fundo);
  const faixas = new Uint8Array(DIGITOS_DA_AMOSTRA);
  for (let i = 0; i < rgba.length; i += 4) {
    const a = rgba[i + 3] / 255;
    const r = Math.round(rgba[i] * a + fr * (1 - a));
    const g = Math.round(rgba[i + 1] * a + fg * (1 - a));
    const b = Math.round(rgba[i + 2] * a + fb * (1 - a));
    const faixa = Math.min(FAIXAS - 1, Math.floor(luminanciaRgb(r, g, b) * FAIXAS));
    faixas[faixa >> 2] |= 8 >> (faixa & 3);
  }
  return Array.from(faixas, (d) => d.toString(16).toUpperCase()).join("");
}

export function amostraValida(amostra: string | null | undefined): amostra is string {
  return typeof amostra === "string" && /^[0-9a-fA-F]{64}$/.test(amostra) && /[1-9a-fA-F]/.test(amostra);
}

/** Lowest ratio between `cor` and any point of the sampled image; 1 when the color falls inside an occupied band. */
export function piorRazaoContraImagem(cor: string, amostra: string): number {
  if (!amostraValida(amostra)) throw new Error("Amostra de luminância fora do formato.");
  const [r, g, b] = rgbDe(cor);
  const texto = luminanciaRgb(r, g, b);
  let pior = Number.POSITIVE_INFINITY;
  for (let faixa = 0; faixa < FAIXAS; faixa++) {
    const digito = parseInt(amostra[faixa >> 2], 16);
    if ((digito & (8 >> (faixa & 3))) === 0) continue;
    const proximo = Math.min(Math.max(texto, faixa / FAIXAS), (faixa + 1) / FAIXAS);
    pior = Math.min(pior, (Math.max(texto, proximo) + 0.05) / (Math.min(texto, proximo) + 0.05));
  }
  return pior;
}

export function suficienteContraImagem(cor: string, amostra: string): boolean {
  return piorRazaoContraImagem(cor, amostra) >= RAZAO_MINIMA;
}

/** Preview of the server's rule for the texts on the image; colors not yet in `#RRGGBB` are left to `avisosDeCor`. */
export function avisosContraImagem(amostra: string, textos: [string, string][]): string[] {
  if (!amostraValida(amostra)) return [];
  return textos
    .filter(([, cor]) => formatoValido(cor.trim()))
    .map(([nome, cor]) => [nome, piorRazaoContraImagem(cor.trim(), amostra)] as const)
    .filter(([, r]) => r < RAZAO_MINIMA)
    .map(([nome, r]) => `${nome} sobre a imagem de fundo: contraste ${(Math.floor(r * 100) / 100).toFixed(2)}:1 no pior ponto, mínimo ${RAZAO_MINIMA}:1.`);
}
