import i18n from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import { initReactI18next } from "react-i18next";
import enCommon from "@/locales/en-US/common.json";
import ptCommon from "@/locales/pt-BR/common.json";
import enAuth from "@/locales/en-US/auth.json";
import ptAuth from "@/locales/pt-BR/auth.json";

// Canonical tags: specific (what the switcher offers, User.Locale persists, the claim carries).
export const SUPPORTED = ["en-US", "pt-BR"] as const;

type Catalog = Record<string, string>;

// Translation catalogs keyed by namespace, then by locale. To add a namespace
// in a later wave: import its two JSON files and add one row here — `resources`,
// the namespace list, and parity tests all derive from this single map, so no
// other wiring changes.
const catalogs: Record<string, Record<(typeof SUPPORTED)[number], Catalog>> = {
  common: { "en-US": enCommon, "pt-BR": ptCommon },
  auth: { "en-US": enAuth, "pt-BR": ptAuth },
};

export const NAMESPACES = Object.keys(catalogs);

// Pivot the namespace-first map into i18next's locale-first `resources` shape:
// { "en-US": { common: {…} }, "pt-BR": { common: {…} } }.
const resources = Object.fromEntries(
  SUPPORTED.map((lng) => [
    lng,
    Object.fromEntries(Object.entries(catalogs).map(([ns, byLng]) => [ns, byLng[lng]])),
  ]),
);

// i18next's nonExplicitSupportedLngs does NOT rewrite pt-PT->pt-BR. Normalize explicitly via
// convertDetectedLanguage: map any variant onto a canonical tag by its language part.
const CANON: Record<string, string> = { pt: "pt-BR", en: "en-US" };
const toCanonical = (lng: string) =>
  (SUPPORTED as readonly string[]).includes(lng) ? lng : (CANON[lng.split("-")[0]] ?? lng);

// Called from main.tsx AFTER loadRuntimeConfig(), so fallbackLng reads the per-deployment
// default: the browser/persisted locale wins, the deployment default is only the fallback.
export function initI18n(deploymentDefault: string) {
  return i18n
    .use(LanguageDetector)
    .use(initReactI18next)
    .init({
      resources,
      fallbackLng: (SUPPORTED as readonly string[]).includes(deploymentDefault)
        ? deploymentDefault
        : "en-US",
      supportedLngs: [...SUPPORTED],
      ns: NAMESPACES,
      defaultNS: "common",
      interpolation: { escapeValue: false },
      detection: {
        // NO cookie — localStorage only (the library default; key i18nextLng).
        order: ["querystring", "localStorage", "navigator"],
        caches: ["localStorage"],
        lookupQuerystring: "culture",
        convertDetectedLanguage: toCanonical, // pt/pt-PT->pt-BR, en/en-GB->en-US
      },
    });
}

export default i18n;
