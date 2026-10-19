/// <reference types="node" />
// Run: node --test src/features/paf/sessao/aviso-licenca.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { avisoAMostrar, textoAvisoLicenca, type AvisoLicencaDto } from "./aviso-licenca.ts";

const AVISO: AvisoLicencaDto = {
  registroId: "r2",
  unidadeId: "u1",
  unidade: "Loja Centro",
  ocupadas: 6,
  limite: 5,
  registradoEm: "2026-10-19T12:00:00Z",
};

test("sem excedente (204 vira null ou undefined) não mostra nada", () => {
  assert.equal(avisoAMostrar(null, null), null);
  assert.equal(avisoAMostrar(undefined, "r1"), null);
});

test("excedente não fechado aparece", () => {
  assert.equal(avisoAMostrar(AVISO, null), AVISO);
});

test("o registro fechado some, e um excedente novo da mesma sessão volta a aparecer", () => {
  assert.equal(avisoAMostrar(AVISO, "r2"), null);
  assert.equal(avisoAMostrar(AVISO, "r1"), AVISO);
});

test("o texto traz unidade, ocupadas e limite", () => {
  const texto = textoAvisoLicenca(AVISO);
  assert.match(texto, /Loja Centro/);
  assert.match(texto, /6 sessões para 5 contratadas/);
});
