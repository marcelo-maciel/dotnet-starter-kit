/// <reference types="node" />
// Run: node --test src/features/paf/estacoes/lista-midias-painel-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { adicionar, bloqueioDoSalvar, duracaoDoLaco, iguais, MAXIMO_ITENS, mover, remover } from "./lista-midias-painel-regras.ts";

test("acrescentar: repete mídia, recusa vazio e o 101º item", () => {
  assert.deepEqual(adicionar(["a"], "a"), { ok: true, itens: ["a", "a"] });
  assert.equal(adicionar(["a"], "").ok, false);
  const cheia = Array.from({ length: MAXIMO_ITENS }, () => "a");
  const recusa = adicionar(cheia, "b");
  assert.ok(!recusa.ok && /100/.test(recusa.mensagem));
  assert.equal(adicionar(cheia.slice(1), "b").ok, true, "o 100º entra");
});

test("mover troca com o vizinho e não sai das pontas", () => {
  assert.deepEqual(mover(["a", "b", "c"], 0, 1), ["b", "a", "c"]);
  assert.deepEqual(mover(["a", "b", "c"], 2, -1), ["a", "c", "b"]);
  assert.deepEqual(mover(["a", "b", "c"], 0, -1), ["a", "b", "c"]);
  assert.deepEqual(mover(["a", "b", "c"], 2, 1), ["a", "b", "c"]);
  assert.deepEqual(mover([], 0, 1), []);
});

test("tirar remove só a posição, não toda repetição da mídia", () => {
  assert.deepEqual(remover(["a", "b", "a"], 2), ["a", "b"]);
  assert.deepEqual(remover(["a"], 5), ["a"]);
});

test("duração do laço soma só as ativas, repetições contadas", () => {
  const midias = [
    { id: "a", duracaoSegundos: 30, ativa: true },
    { id: "b", duracaoSegundos: 10, ativa: false },
  ];
  assert.equal(duracaoDoLaco(["a", "b", "a", "x"], midias), 60);
  assert.equal(duracaoDoLaco([], midias), 0);
});

test("iguais compara ordem e tamanho", () => {
  assert.equal(iguais(["a", "b"], ["a", "b"]), true);
  assert.equal(iguais(["a", "b"], ["b", "a"]), false);
  assert.equal(iguais(["a"], ["a", "a"]), false);
});

test("salvar: bloqueado com mídia inativa na lista, apontando as posições (o PUT do M06 só aceita mídias ativas)", () => {
  const midias = [
    { id: "a", duracaoSegundos: 10, ativa: true },
    { id: "b", duracaoSegundos: 20, ativa: false },
  ];
  assert.equal(bloqueioDoSalvar(["a", "a"], midias), null);
  assert.equal(bloqueioDoSalvar([], midias), null);
  const bloqueio = bloqueioDoSalvar(["a", "b", "a", "b"], midias);
  assert.ok(bloqueio !== null && /posições 2 e 4/.test(bloqueio), String(bloqueio));
  assert.match(bloqueioDoSalvar(["b"], midias) ?? "", /\(posição 1\)/);
  // Unknown status (mídia not described) is not guessed inactive: the server decides.
  assert.equal(bloqueioDoSalvar(["c"], midias), null);
});
