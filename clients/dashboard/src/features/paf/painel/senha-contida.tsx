import { useEffect, useLayoutEffect, useRef } from "react";

// Only bounds the loop: the first step already lands within a pixel or two of the size that fits.
const FONTE_MINIMA_PX = 8;

/**
 * Shrinks the senha below its CSS size only when it overflows its own box (F20: up to 5 letters plus an unbounded
 * number, 12 characters in the column). A short senha keeps the full size; a fixed cap would shrink every senha to
 * fit the longest one.
 */
function conterNaLargura(elemento: HTMLElement): void {
  elemento.style.fontSize = "";
  const disponivel = elemento.clientWidth;
  if (elemento.scrollWidth <= disponivel) return;
  let px = Math.floor((parseFloat(getComputedStyle(elemento).fontSize) * disponivel) / elemento.scrollWidth);
  elemento.style.fontSize = `${px}px`;
  while (elemento.scrollWidth > elemento.clientWidth && px > FONTE_MINIMA_PX) {
    px -= 1;
    elemento.style.fontSize = `${px}px`;
  }
}

type Props = { senha: string; className: string };

/** The painel's main senha on a single line that never runs past the highlight box (`max-w-full` on the box). */
export function SenhaContida({ senha, className }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  // Every render, not only on a new senha: the cadastro font, the colored border and the call message change the room
  // the senha has, and they all arrive through the parent's state.
  useLayoutEffect(() => {
    if (ref.current !== null) conterNaLargura(ref.current);
  });
  useEffect(() => {
    const aoRedimensionar = () => {
      if (ref.current !== null) conterNaLargura(ref.current);
    };
    window.addEventListener("resize", aoRedimensionar);
    return () => window.removeEventListener("resize", aoRedimensionar);
  }, []);
  return (
    <div ref={ref} data-senha-atual className={`${className} max-w-full whitespace-nowrap`}>
      {senha}
    </div>
  );
}
