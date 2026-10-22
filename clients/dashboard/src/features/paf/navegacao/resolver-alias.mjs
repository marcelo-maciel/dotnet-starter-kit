// Lets `node --test` load a module that imports through the "@/..." alias of tsconfig, so the menu test runs the
// kit's real nav filter instead of a copy of it (D21: node --test, no new dependency).
// Run: node --import ./src/features/paf/navegacao/resolver-alias.mjs --test src/features/paf/navegacao/menu-paf.test.ts
import { existsSync } from "node:fs";
import { registerHooks } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";

const SRC = fileURLToPath(new URL("../../../", import.meta.url));

registerHooks({
  resolve(especificador, contexto, proximo) {
    if (!especificador.startsWith("@/")) return proximo(especificador, contexto);
    const base = SRC + especificador.slice(2);
    const arquivo = [".ts", ".tsx", "/index.ts"].map((ext) => base + ext).find((caminho) => existsSync(caminho));
    if (!arquivo) throw new Error(`resolver-alias: ${especificador} não encontrado em ${SRC}`);
    return { url: pathToFileURL(arquivo).href, shortCircuit: true };
  },
});
