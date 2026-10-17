// Ticket printing (RF11, spikes/impressao). Two findings of the spike shape this file:
// - `@page { size: 80mm auto }` is invalid CSS and Chrome drops the whole `size`: the page height is
//   computed from the rendered ticket and written as an explicit length;
// - the longest senha (5 letters + 4 digits) does not fit 55 px on 80 mm: the font shrinks until it
//   fits (about 39 px for "WWWWW9999").

export const LARGURA_BOBINA_MM = 80;
export const TAMANHO_SENHA_PX = 55;
const TAMANHO_SENHA_MINIMO_PX = 12;
const PX_POR_MM = 96 / 25.4;
// Spare paper below the ticket so the cutter never cuts through the footer.
const FOLGA_MM = 2;
const ID_ESTILO_PAGINA = "paf-ficha-pagina";

/** Shrinks the senha one pixel at a time until it fits its box; returns the size used. */
export function ajustarFonteSenha(elemento: HTMLElement): number {
  let px = TAMANHO_SENHA_PX;
  elemento.style.fontSize = `${px}px`;
  while (elemento.scrollWidth > elemento.clientWidth && px > TAMANHO_SENHA_MINIMO_PX) {
    px -= 1;
    elemento.style.fontSize = `${px}px`;
  }
  return px;
}

/** Writes `@page` with the roll width and the ticket's measured height; returns that height in mm. */
export function definirPagina(ficha: HTMLElement): number {
  const alturaMm = Math.ceil(ficha.getBoundingClientRect().height / PX_POR_MM) + FOLGA_MM;
  let estilo = document.getElementById(ID_ESTILO_PAGINA);
  if (estilo === null) {
    estilo = document.createElement("style");
    estilo.id = ID_ESTILO_PAGINA;
    document.head.appendChild(estilo);
  }
  estilo.textContent = `@page { size: ${LARGURA_BOBINA_MM}mm ${alturaMm}mm; margin: 0 }`;
  return alturaMm;
}

export function removerPagina(): void {
  document.getElementById(ID_ESTILO_PAGINA)?.remove();
}

/**
 * Prints and resolves on `afterprint` or after `limiteMs`. With `--kiosk-printing` print() returns
 * without a dialog; without it the dialog blocks, and the limit keeps the totem from hanging.
 */
export function imprimir(limiteMs: number): Promise<boolean> {
  return new Promise((resolve) => {
    const controle = new AbortController();
    const timer = window.setTimeout(() => {
      controle.abort();
      resolve(false);
    }, limiteMs);
    window.addEventListener(
      "afterprint",
      () => {
        window.clearTimeout(timer);
        controle.abort();
        resolve(true);
      },
      { signal: controle.signal },
    );
    window.print();
  });
}
