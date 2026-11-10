/// <reference types="node" />
// Run: node --test src/features/paf/guiche/aguardando.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { textoAguardando } from "./aguardando.ts";

test("zero, um e vários", () => {
  assert.equal(textoAguardando(0), "Nenhuma ficha aguardando");
  assert.equal(textoAguardando(1), "1 ficha aguardando");
  assert.equal(textoAguardando(2), "2 fichas aguardando");
  assert.equal(textoAguardando(150), "150 fichas aguardando");
});
