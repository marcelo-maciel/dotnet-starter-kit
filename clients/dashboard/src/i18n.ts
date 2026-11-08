import i18n from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import { initReactI18next } from "react-i18next";
import { fallbackChain, missingKeyFallback } from "@/lib/i18n-fallback";
import enCommon from "@/locales/en-US/common.json";
import enAuth from "@/locales/en-US/auth.json";
import enSettings from "@/locales/en-US/settings.json";
import enIdentity from "@/locales/en-US/identity.json";
import enOverview from "@/locales/en-US/overview.json";
import enSubscription from "@/locales/en-US/subscription.json";
import enActivity from "@/locales/en-US/activity.json";
import enCatalog from "@/locales/en-US/catalog.json";
import enTickets from "@/locales/en-US/tickets.json";
import enFiles from "@/locales/en-US/files.json";
import enAudits from "@/locales/en-US/audits.json";
import enCommandPalette from "@/locales/en-US/commandPalette.json";
import enNotifications from "@/locales/en-US/notifications.json";
import enSystem from "@/locales/en-US/system.json";
import enChat from "@/locales/en-US/chat.json";
import enHealth from "@/locales/en-US/health.json";

// Canonical tags: specific (what the switcher offers, User.Locale persists, the claim carries).
export const SUPPORTED = ["en-US", "pt-BR"] as const;

type Catalog = Record<string, string>;

// en-US translation catalogs keyed by namespace. To add a namespace in a later wave: import its
// en-US JSON file and add one row here — `resources` and the namespace list derive from this
// single map, so no other wiring changes. Its pt-BR file (optional) is picked up by
// src/locales/pt-BR.ts; a namespace or key pt-BR lacks renders in English through fallbackChain.
// Its type is also what src/i18next.d.ts checks every t() key against.
const catalogs = {
  common: enCommon,
  auth: enAuth,
  settings: enSettings,
  identity: enIdentity,
  overview: enOverview,
  subscription: enSubscription,
  activity: enActivity,
  catalog: enCatalog,
  tickets: enTickets,
  files: enFiles,
  audits: enAudits,
  commandPalette: enCommandPalette,
  notifications: enNotifications,
  system: enSystem,
  chat: enChat,
  health: enHealth,
} satisfies Record<string, Catalog>;

export type Catalogs = typeof catalogs;

export const NAMESPACES = Object.keys(catalogs);

// en-US ships in the main bundle: it is the terminal fallback and must resolve synchronously.
// Every other language is one lazy chunk, fetched before the UI switches to it.
const LAZY_LANGUAGES: Partial<
  Record<(typeof SUPPORTED)[number], () => Promise<{ default: Record<string, Catalog> }>>
> = {
  "pt-BR": () => import("@/locales/pt-BR"),
};
const loadedLanguages = new Set<string>(["en-US"]);

async function loadLanguage(lng: string) {
  const load = LAZY_LANGUAGES[lng as (typeof SUPPORTED)[number]];
  if (!load || loadedLanguages.has(lng)) return;
  const { default: bundle } = await load();
  for (const ns of NAMESPACES) {
    const catalog = bundle[ns];
    if (catalog) i18n.addResourceBundle(lng, ns, catalog, true, true);
  }
  loadedLanguages.add(lng);
}

let latestSwitch = 0;

// Every language switch goes through here so the catalog lands before the UI re-renders in it:
// react-i18next re-renders on languageChanged, not when a bundle is added. When the chunk fails
// to load this rejects before i18next sees the new language, so neither the UI nor the
// detector's persisted choice moves. Resolves false when a later switch landed while this
// catalog was loading: applying it now would undo the newer choice, and reporting its failure
// would contradict the language the user now sees.
export async function changeLanguage(lng: string): Promise<boolean> {
  const thisSwitch = ++latestSwitch;
  try {
    await loadLanguage(lng);
  } catch (error) {
    if (thisSwitch !== latestSwitch) return false;
    throw error;
  }
  if (thisSwitch !== latestSwitch) return false;
  await i18n.changeLanguage(lng);
  return true;
}

// The PAF is a PT-BR product: every user sees pt-BR whatever the browser, the deployment config or the profile says.
// The en-US catalogs stay as the terminal fallback and as the language the e2e suites run in.
export const PAF_LANGUAGE = "pt-BR";

// Keep the document's language attribute in step with the active locale. index.html ships a
// static lang="en"; without this, a Portuguese UI still declares itself English to screen
// readers, browser translation and hyphenation. Registered once, before init, so it also fires
// for the initial language. It reads resolvedLanguage, the language the text is actually in:
// when a catalog failed to load, `lng` is still the requested one while the UI shows English.
if (typeof document !== "undefined") {
  i18n.on("languageChanged", (lng) => {
    document.documentElement.lang = i18n.resolvedLanguage ?? lng;
  });
}

// Called from main.tsx. Nothing a user does picks another language: the detector reads only the `culture`
// querystring and this app's localStorage key, which no screen writes (the topbar has no language switcher in the
// PAF) and which the e2e suites seed with en-US; with neither, i18next lands on the first fallback, pt-BR. The browser
// language is never read.
export async function initI18n() {
  await i18n
    .use(LanguageDetector)
    .use(initReactI18next)
    .init({
      resources: { "en-US": catalogs },
      fallbackLng: fallbackChain(PAF_LANGUAGE, SUPPORTED),
      supportedLngs: [...SUPPORTED],
      ns: NAMESPACES,
      defaultNS: "common",
      interpolation: { escapeValue: false },
      // A key the catalog has not caught up with must not render as itself ("status.invoiced") or
      // swallow the caller's English fallback. Both rules live in missingKeyFallback, which the
      // suite exercises directly; this only adds the development warning.
      parseMissingKeyHandler: (key: string, defaultValue?: string | null) => {
        if (import.meta.env.DEV) {
          console.warn(`[i18n] missing key: ${key}`);
        }
        return missingKeyFallback(key, defaultValue);
      },
      detection: {
        // No cookie and no navigator: the browser language never moves a PAF user off pt-BR. The querystring and
        // this app's key are explicit overrides no screen produces (the suites use them); nothing is cached, so an
        // override lasts one page load and the key stays absent for every real user.
        order: ["querystring", "localStorage"],
        caches: [],
        lookupLocalStorage: "fsh.dashboard.lng",
        lookupQuerystring: "culture",
      },
    });

  // The detected (or deployment-default) language may be a lazy one: fetch it before the first
  // render so a pt-BR user never sees English first. If the chunk fails, the app still boots and
  // renders English through the fallback chain. i18next has already set (and the detector saved)
  // `language` to the requested tag; switching to en-US here would overwrite that saved choice,
  // so code that needs the language on screen reads resolvedLanguage, which stays en-US.
  try {
    await changeLanguage(i18n.language);
  } catch (error) {
    console.warn(`[i18n] could not load the ${i18n.language} catalog; showing English.`, error);
  }
}

export default i18n;
