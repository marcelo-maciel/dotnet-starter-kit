// Lets `node --test` load a module that imports through the "@/..." alias of tsconfig, so a test runs the real module
// (format.ts reaches the kit's i18n.ts, which imports its en-US catalogs as plain JSON, as Vite allows). Same hook as the
// dashboard's navegacao/resolver-alias.mjs, plus the JSON attribute Node requires (D21: node --test, no new dependency).
// Run: node --import ./src/features/paf/resolver-alias.mjs --test "src/lib/format.test.ts" "src/features/paf/**/*.test.ts"
import { statSync } from "node:fs";
import { registerHooks } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";

const SRC = fileURLToPath(new URL("../../", import.meta.url));

registerHooks({
  resolve(especificador, contexto, proximo) {
    if (!especificador.startsWith("@/")) return proximo(especificador, contexto);
    const base = SRC + especificador.slice(2);
    const arquivo = ["", ".ts", ".tsx", "/index.ts"].map((ext) => base + ext).find((caminho) => statSync(caminho, { throwIfNoEntry: false })?.isFile());
    if (!arquivo) throw new Error(`resolver-alias: ${especificador} não encontrado em ${SRC}`);
    const atributos = arquivo.endsWith(".json") ? { type: "json" } : contexto.importAttributes;
    return { url: pathToFileURL(arquivo).href, importAttributes: atributos, shortCircuit: true };
  },
});
