/// <reference types="node" />
// Run: node --test src/features/paf/guiche/selo-impersonacao.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { textoSeloImpersonacao } from "./selo-impersonacao.ts";

test("fora da impersonação não há selo", () => {
  assert.equal(textoSeloImpersonacao(null, "Ana"), null);
});

test("impersonação nomeia o operador e o usuário impersonado", () => {
  const texto = textoSeloImpersonacao({ actorName: "Marcos (Codefi)" }, "Ana");
  assert.ok(texto?.startsWith("Marcos (Codefi) opera este guichê como Ana"));
});

test("sem nome do operador ou do usuário, o selo aparece com o texto genérico", () => {
  assert.ok(textoSeloImpersonacao({}, undefined)?.startsWith("Suporte da Codefi opera este guichê como o usuário"));
  assert.ok(textoSeloImpersonacao({ actorName: "   " }, "  ")?.startsWith("Suporte da Codefi opera este guichê como o usuário"));
});
