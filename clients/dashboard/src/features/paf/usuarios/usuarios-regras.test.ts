/// <reference types="node" />
// Run: node --test src/features/paf/usuarios/usuarios-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { dominioDoEmail, moverUnidade, sugestaoDeEmail, unidadesRetiradas } from "./usuarios-regras.ts";

test("subir e descer trocam só as duas posições vizinhas", () => {
  assert.deepEqual(moverUnidade(["a", "b", "c"], 2, -1), ["a", "c", "b"]);
  assert.deepEqual(moverUnidade(["a", "b", "c"], 0, 1), ["b", "a", "c"]);
});

test("subir a primeira e descer a última não mudam nada, nem índice fora da lista", () => {
  assert.deepEqual(moverUnidade(["a", "b"], 0, -1), ["a", "b"]);
  assert.deepEqual(moverUnidade(["a", "b"], 1, 1), ["a", "b"]);
  assert.deepEqual(moverUnidade(["a", "b"], 5, -1), ["a", "b"]);
  assert.deepEqual(moverUnidade([], 0, 1), []);
});

test("mover devolve lista nova, sem alterar a original", () => {
  const original = ["a", "b"];
  moverUnidade(original, 0, 1);
  assert.deepEqual(original, ["a", "b"]);
});

test("retiradas são as que saem; reordenar não retira nada", () => {
  assert.deepEqual(unidadesRetiradas(["a", "b", "c"], ["c", "a"]), ["b"]);
  assert.deepEqual(unidadesRetiradas(["a", "b"], ["b", "a"]), []);
  assert.deepEqual(unidadesRetiradas([], ["a"]), []);
});

test("domínio só de e-mail bem formado", () => {
  assert.equal(dominioDoEmail("Admin@Loja.COM.br"), "loja.com.br");
  assert.equal(dominioDoEmail("sem-arroba"), null);
  assert.equal(dominioDoEmail("a@b@c.com"), null);
  assert.equal(dominioDoEmail("@loja.com"), null);
  assert.equal(dominioDoEmail("a@localhost"), null);
  assert.equal(dominioDoEmail(undefined), null);
});

test("sugestão de e-mail sem acento, espaço nem símbolo (D14)", () => {
  assert.equal(sugestaoDeEmail("José", "da Conceição", "loja.com.br"), "jose.da.conceicao@loja.com.br");
  assert.equal(sugestaoDeEmail("  Ana ", "O'Neil", "loja.com.br"), "ana.o.neil@loja.com.br");
  assert.equal(sugestaoDeEmail("Ana", "", "loja.com.br"), "ana@loja.com.br");
});

test("sem nome ou sem domínio não há sugestão", () => {
  assert.equal(sugestaoDeEmail("", "  ", "loja.com.br"), null);
  assert.equal(sugestaoDeEmail("!!!", "", "loja.com.br"), null);
  assert.equal(sugestaoDeEmail("Ana", "Silva", null), null);
});

test("definir senha só libera com senha preenchida e confirmação idêntica", async () => {
  const { senhaProntaParaDefinir } = await import("./usuarios-regras.ts");
  assert.equal(senhaProntaParaDefinir("Nova@5678!", "Nova@5678!"), true);
  assert.equal(senhaProntaParaDefinir("", ""), false);
  assert.equal(senhaProntaParaDefinir("   ", "   "), false);
  assert.equal(senhaProntaParaDefinir("Nova@5678!", "nova@5678!"), false, "maiúscula conta");
  assert.equal(senhaProntaParaDefinir("Nova@5678!", "Nova@5678! "), false, "espaço no fim conta");
});
