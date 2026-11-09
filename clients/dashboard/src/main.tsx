import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "@/App";
import { installImpersonationFromHash } from "@/auth/impersonation-handoff";
import { loadRuntimeConfig } from "@/env";
import { initI18n } from "@/i18n";
import "@/styles/globals.css";

// Runtime config must resolve before React mounts so env.apiBase reads
// inside components see the right value on first paint.
await loadRuntimeConfig();

// i18n boots after config, before the first render (src/i18n.ts pins pt-BR).
await initI18n();

const rootElement = document.getElementById("root");
if (!rootElement) {
  throw new Error("Root element '#root' not found");
}

// Cross-app impersonation handoff — must run BEFORE createRoot so the
// installed token and the operator's language are visible to AuthProvider on
// first paint. See the helper docstring for the why.
await installImpersonationFromHash();

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
