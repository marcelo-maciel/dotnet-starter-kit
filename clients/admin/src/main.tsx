import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "@/App";
import { loadRuntimeConfig } from "@/env";
import { initI18n } from "@/i18n";
import "@/styles/globals.css";

// Runtime config must be in-memory BEFORE any module that reads env.*
// runs in a render or hook. Top-level await is supported in Vite's ESM
// output and is the simplest shape that guarantees ordering.
await loadRuntimeConfig();

// i18n boots after config, before the first render (src/i18n.ts pins pt-BR).
await initI18n();

const rootElement = document.getElementById("root");
if (!rootElement) {
  throw new Error("Root element '#root' not found");
}

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
