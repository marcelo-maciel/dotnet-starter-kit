import { useState, type CSSProperties } from "react";

/**
 * RF06 "logotipo padrão do sistema". Inline and monochrome (currentColor), so it prints on the thermal ticket and takes
 * the painel's text color without a network read.
 */
// ponytail: placeholder mark drawn here; swap for the product's brand asset when one exists (same props, same box).
function LogotipoPadrao({ estilo }: { estilo: CSSProperties }) {
  return (
    <svg data-logotipo="padrao" role="img" aria-label="Logotipo" viewBox="0 0 120 40" style={{ ...estilo, aspectRatio: "3 / 1" }}>
      <rect x="1.5" y="1.5" width="37" height="37" rx="8" fill="none" stroke="currentColor" strokeWidth="3" />
      <path d="M11 29V11h9a6 6 0 0 1 0 12h-9" fill="none" stroke="currentColor" strokeWidth="3.5" strokeLinejoin="round" />
      <text x="48" y="29" fill="currentColor" fontFamily="Arial, Helvetica, sans-serif" fontSize="24" fontWeight="bold">
        PAF
      </text>
    </svg>
  );
}

type Props = {
  /** Signed URL from `lerLogotipo`; null = the system logo. */
  url: string | null;
  /** Box the logo fits in, keeping its proportion; the box never changes size when the image arrives. */
  altura: string;
  larguraMaxima: string;
};

/** The custom logo, or the system one when there is none or it fails to load (expired URL, store down). */
export function LogotipoImagem({ url, altura, larguraMaxima }: Props) {
  const [falhou, setFalhou] = useState<string | null>(null);
  const estilo: CSSProperties = { height: altura, maxWidth: larguraMaxima, width: "auto", display: "block", objectFit: "contain" };
  if (url === null || falhou === url) return <LogotipoPadrao estilo={estilo} />;
  return <img data-logotipo="personalizado" src={url} alt="Logotipo" style={estilo} onError={() => setFalhou(url)} />;
}
