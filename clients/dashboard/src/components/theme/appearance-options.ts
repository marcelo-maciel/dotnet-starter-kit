/**
 * Selectable appearance options — fonts and density.
 *
 * Fonts are applied by setting `--font-sans` on :root.
 */

export type FontOption = {
  id: string;
  label: string;
  description: string;
  /** CSS font-family value applied to --font-sans. */
  family: string;
  /** Sample shown on the swatch card. */
  sample?: string;
};

const SHARED_FALLBACKS =
  "ui-sans-serif, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif";

export const fonts: FontOption[] = [
  {
    id: "geist",
    label: "Geist",
    description: "Feita para telas. Padrão.",
    family: `'Geist', ${SHARED_FALLBACKS}`,
  },
  {
    id: "inter-tight",
    label: "Inter Tight",
    description: "Inter moderna e mais compacta.",
    family: `'Inter Tight', ${SHARED_FALLBACKS}`,
  },
  {
    id: "dm-sans",
    label: "DM Sans",
    description: "Geométrica e amigável.",
    family: `'DM Sans', ${SHARED_FALLBACKS}`,
  },
  {
    id: "ibm-plex",
    label: "IBM Plex Sans",
    description: "Grotesca editorial.",
    family: `'IBM Plex Sans', ${SHARED_FALLBACKS}`,
  },
  {
    id: "manrope",
    label: "Manrope",
    description: "Calorosa e geométrica.",
    family: `'Manrope', ${SHARED_FALLBACKS}`,
  },
  {
    id: "plus-jakarta",
    label: "Plus Jakarta Sans",
    description: "Moderna, levemente geométrica.",
    family: `'Plus Jakarta Sans', ${SHARED_FALLBACKS}`,
  },
  {
    id: "outfit",
    label: "Outfit",
    description: "Sem serifa geométrica e firme.",
    family: `'Outfit', ${SHARED_FALLBACKS}`,
  },
  {
    id: "sora",
    label: "Sora",
    description: "Marcante e contemporânea.",
    family: `'Sora', ${SHARED_FALLBACKS}`,
  },
  {
    id: "lexend",
    label: "Lexend",
    description: "Ajustada para leitura rápida.",
    family: `'Lexend', ${SHARED_FALLBACKS}`,
  },
  {
    id: "figtree",
    label: "Figtree",
    description: "Amigável e acessível.",
    family: `'Figtree', ${SHARED_FALLBACKS}`,
  },
  {
    id: "onest",
    label: "Onest",
    description: "Grotesca limpa e neutra.",
    family: `'Onest', ${SHARED_FALLBACKS}`,
  },
  {
    id: "roboto-flex",
    label: "Roboto Flex",
    description: "A variável principal do Google.",
    family: `'Roboto Flex', ${SHARED_FALLBACKS}`,
  },
];

export const DEFAULT_FONT = "figtree";

export const FONT_STORAGE_KEY = "fsh.font";
export type DensityMode = "comfortable" | "compact";
export const DENSITY_STORAGE_KEY = "fsh.density";
export const DEFAULT_DENSITY: DensityMode = "comfortable";
