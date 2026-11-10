/// <reference types="node" />
// Run: node --test src/features/paf/aparencia/contraste.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { formatarRazao, formatoValido, razao, suficiente } from "./contraste.ts";

test("o piso é 4,5:1, com os mesmos casos do servidor", () => {
  assert.equal(suficiente("#777777", "#FFFFFF"), false);
  assert.equal(suficiente("#767676", "#FFFFFF"), true);
  assert.equal(suficiente("#FFFFFF", "#FFFF00"), false, "branco sobre amarelo");
  assert.equal(suficiente("#000000", "#FFFF00"), true);
});

test("razão vai de 1 a 21 e é simétrica", () => {
  assert.equal(Math.round(razao("#000000", "#ffffff") * 1000) / 1000, 21);
  assert.equal(Math.round(razao("#FFFFFF", "#000000") * 1000) / 1000, 21);
  assert.equal(razao("#123456", "#123456"), 1);
  assert.equal(formatarRazao(razao("#FFFFFF", "#FFFF00")), "1,07:1");
});

test("só #RRGGBB passa no formato, e razão recusa o resto", () => {
  for (const cor of ["#GGGGGG", "#FFF", "#FFFFFF80", "FFFFFF", " #FFFFFF", "", "#FFFFF١"]) {
    assert.equal(formatoValido(cor), false, cor);
    assert.throws(() => razao(cor, "#000000"));
  }
  assert.equal(formatoValido(null), false);
  assert.equal(formatoValido(undefined), false);
  assert.equal(formatoValido("#a1B2c3"), true);
});
