// RF06 "Logotipo" (C19) on the totem, the painel and the printed ficha: the API sends the signed URL of the custom
// logo, or null for the system logo. Pure (node --test per D21); the component lives in logotipo-imagem.tsx.

/** Height of the logo box on the 80 mm ticket. Fixed, so the page height measured before the image loads stays right. */
export const ALTURA_LOGOTIPO_FICHA_MM = 16;

/** How long a print waits for the logo to decode; past it the ficha prints without it, never later (RF11). */
export const PRAZO_LOGOTIPO_IMPRESSAO_MS = 3_000;

/**
 * The `logotipo` of the station configuration or of a printed ficha; absent, malformed (an older API) or not an
 * http(s) URL = null, the system logo. Anything else would end up in an `<img src>`.
 */
export function lerLogotipo(dado: unknown): string | null {
  if (typeof dado !== "object" || dado === null) return null;
  const { url } = dado as Record<string, unknown>;
  if (typeof url !== "string") return null;
  try {
    const protocolo = new URL(url).protocol;
    return protocolo === "https:" || protocolo === "http:" ? url : null;
  } catch {
    // Not a URL at all: the system logo is the designed fallback.
    return null;
  }
}

type Decodificavel = { decode(): Promise<void> };

/**
 * Resolves once every image is decoded or failed, or after `prazoMs`, whichever comes first. A failed image is not an
 * error here: the component swaps it for the system logo, and the ficha prints either way.
 */
export function aguardarImagens(imagens: readonly Decodificavel[], prazoMs: number): Promise<void> {
  if (imagens.length === 0) return Promise.resolve();
  const todas = Promise.allSettled(imagens.map((imagem) => imagem.decode())).then(() => undefined);
  let timer: ReturnType<typeof setTimeout> | undefined;
  const prazo = new Promise<void>((resolve) => {
    timer = setTimeout(resolve, prazoMs);
  });
  return Promise.race([todas, prazo]).finally(() => clearTimeout(timer));
}
