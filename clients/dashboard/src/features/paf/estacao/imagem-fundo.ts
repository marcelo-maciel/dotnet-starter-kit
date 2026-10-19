// Background image of the totem and the painel (C17). The URL is signed and short-lived (M04): each configuration
// re-read may bring a new one, so a new URL is preloaded and replaces the old one only once it loaded, and the screen
// never flashes empty. A URL that fails (expired, store down) asks for a re-read, which brings a fresh signature once
// the old one left the server cache, and is tried again later, since a network blip leaves the same URL valid.
import { useEffect, useState, type CSSProperties } from "react";

// A store that never answers must not keep the old image forever without anyone asking again.
const LIMITE_CARGA_MS = 15_000;
const NOVA_TENTATIVA_MS = 30_000;

/** The image to show: the last URL that loaded, or null once the configuration has no image. */
export function useImagemDeFundo(url: string | null, aoFalhar: () => void): string | null {
  const [carregada, setCarregada] = useState<string | null>(null);
  const [tentativa, setTentativa] = useState(0);

  useEffect(() => {
    if (url === null) return;
    let ativo = true;
    let novaTentativa: number | undefined;
    const imagem = new Image();
    const limite = window.setTimeout(() => falhar(), LIMITE_CARGA_MS);
    function soltar() {
      window.clearTimeout(limite);
      imagem.onload = null;
      imagem.onerror = null;
    }
    function falhar() {
      if (!ativo) return;
      ativo = false;
      soltar();
      console.warn("[paf-estacao] imagem de fundo não carregou; relendo a configuração");
      aoFalhar();
      novaTentativa = window.setTimeout(() => setTentativa((t) => t + 1), NOVA_TENTATIVA_MS);
    }
    imagem.onload = () => {
      if (!ativo) return;
      ativo = false;
      soltar();
      setCarregada(url);
    };
    imagem.onerror = falhar;
    imagem.src = url;
    return () => {
      ativo = false;
      soltar();
      window.clearTimeout(novaTentativa);
    };
  }, [url, tentativa, aoFalhar]);

  return url === null ? null : carregada;
}

/** Inline style of the screen root; empty without an image. */
export function estiloImagemDeFundo(url: string | null): CSSProperties {
  if (url === null) return {};
  return {
    backgroundImage: `url(${JSON.stringify(url)})`,
    backgroundSize: "cover",
    backgroundPosition: "center",
    backgroundRepeat: "no-repeat",
  };
}
