// Contrast floor of the appearance the client picks (RNF19, REQ 8.1): text over background at least 4.5:1 by the
// WCAG 2.x relative-luminance formula. Mirror of src/Modules/PAF/Modules.PAF/Aparencia/ContrasteWcag.cs, used for
// the live preview; the server check is the guarantee. Pure module (node --test, D21).

export const RAZAO_MINIMA = 4.5;

// Only #RRGGBB, like the server: a short form or an alpha channel would make the stored color differ from the checked one.
export function formatoValido(cor: string | null | undefined): cor is string {
  return typeof cor === "string" && /^#[0-9a-fA-F]{6}$/.test(cor);
}

function canal(cor: string, inicio: number): number {
  const c = parseInt(cor.slice(inicio, inicio + 2), 16) / 255;
  return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

function luminancia(cor: string): number {
  if (!formatoValido(cor)) throw new Error(`Cor fora do formato #RRGGBB: ${cor}`);
  return 0.2126 * canal(cor, 1) + 0.7152 * canal(cor, 3) + 0.0722 * canal(cor, 5);
}

/** From 1 (same luminance) to 21 (black on white); symmetric. */
export function razao(corTexto: string, corFundo: string): number {
  const l1 = luminancia(corTexto);
  const l2 = luminancia(corFundo);
  return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
}

export function suficiente(corTexto: string, corFundo: string): boolean {
  return razao(corTexto, corFundo) >= RAZAO_MINIMA;
}

/** "4,54:1", the way the server message prints it. */
export function formatarRazao(valor: number): string {
  return `${valor.toFixed(2).replace(".", ",")}:1`;
}
