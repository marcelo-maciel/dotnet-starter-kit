/// <reference types="node" />
// Run: node --test src/features/paf/ficha-de-espera/ficha-de-espera-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { consultaDaReimpressao, pedidoCompleto } from "./ficha-de-espera-regras.ts";

const FILA = "0199a000-0000-7000-8000-000000000001";

test("pedido só sai com senha válida, data e fila", () => {
  assert.equal(pedidoCompleto({ senha: "A12", data: "2026-10-19", filaId: FILA }), true);
  assert.equal(pedidoCompleto({ senha: "A12", data: "2026-10-19", filaId: "" }), false);
  assert.equal(pedidoCompleto({ senha: "A12", data: "", filaId: FILA }), false);
  assert.equal(pedidoCompleto({ senha: "  ", data: "2026-10-19", filaId: FILA }), false);
  // The printed senha always ends in its number; a bare letter would only earn a 400.
  assert.equal(pedidoCompleto({ senha: "AB", data: "2026-10-19", filaId: FILA }), false);
});

test("a consulta leva a senha como o servidor a compara, e escapa o resto", () => {
  assert.equal(consultaDaReimpressao({ senha: "  ap1\t", data: "2026-10-19", filaId: FILA }), `?senha=AP1&data=2026-10-19&filaId=${FILA}`);
  // A letter is free text in the cadastro: anything URL-special must not break the query string.
  assert.equal(consultaDaReimpressao({ senha: "A&B=1", data: "2026-10-19", filaId: FILA }), `?senha=A%26B%3D1&data=2026-10-19&filaId=${FILA}`);
});
