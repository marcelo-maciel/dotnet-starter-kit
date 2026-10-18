/// <reference types="node" />
// Run: node --test src/features/paf/guiche/guiche-sugestao.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { serializarUltimoGuiche, sugerirGuiche } from "./guiche-sugestao.ts";

const ATIVOS = [{ id: "g1" }, { id: "g2" }, { id: "g3" }];
const SALVO_G2 = serializarUltimoGuiche("loja-x", "u-a", "g2");

test("guichê salvo para o mesmo tenant e unidade e ainda ativo é sugerido", () => {
  assert.equal(sugerirGuiche(SALVO_G2, "loja-x", "u-a", ATIVOS), "g2");
});

test("guichê salvo em outra unidade não é sugerido", () => {
  assert.equal(sugerirGuiche(SALVO_G2, "loja-x", "u-b", ATIVOS), "");
});

test("guichê salvo em outro tenant não é sugerido, mesmo com a mesma unidade e o mesmo id", () => {
  assert.equal(sugerirGuiche(SALVO_G2, "loja-y", "u-a", ATIVOS), "");
});

test("guichê desativado ou ausente da lista de ativos não é sugerido", () => {
  assert.equal(sugerirGuiche(SALVO_G2, "loja-x", "u-a", [{ id: "g1" }, { id: "g3" }]), "");
  assert.equal(sugerirGuiche(SALVO_G2, "loja-x", "u-a", []), "");
});

test("nada salvo não sugere", () => {
  assert.equal(sugerirGuiche(null, "loja-x", "u-a", ATIVOS), "");
});

test("valor corrompido ou de outro formato não sugere e não lança", () => {
  const corrompidos = [
    "",
    "{",
    "g2",
    "null",
    "42",
    '"g2"',
    "[]",
    "{}",
    '{"tenant":"loja-x","unidadeId":"u-a"}',
    '{"tenant":"loja-x","unidadeId":"u-a","estacaoId":2}',
    '{"tenant":null,"unidadeId":"u-a","estacaoId":"g2"}',
  ];
  for (const bruto of corrompidos) {
    assert.equal(sugerirGuiche(bruto, "loja-x", "u-a", ATIVOS), "", `valor: ${bruto}`);
  }
});

test("a gravação seguinte substitui a anterior", () => {
  const salvo = serializarUltimoGuiche("loja-x", "u-a", "g3");
  assert.equal(sugerirGuiche(salvo, "loja-x", "u-a", ATIVOS), "g3");
});

test("tenant, unidade e id com aspas e acentos sobrevivem à ida e volta", () => {
  const salvo = serializarUltimoGuiche('lo"ja', "unidade-ç", 'g"2');
  assert.equal(sugerirGuiche(salvo, 'lo"ja', "unidade-ç", [{ id: 'g"2' }]), 'g"2');
});
