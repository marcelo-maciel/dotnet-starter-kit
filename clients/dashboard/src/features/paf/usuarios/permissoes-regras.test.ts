/// <reference types="node" />
// Run: node --test src/features/paf/usuarios/permissoes-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  alterado,
  alternar,
  estadoDaPermissao,
  individuaisParaSalvar,
  MOTIVO_DO_PAPEL,
  MOTIVO_SEM_PERMISSAO,
  type PermissoesUsuarioPafDto,
} from "./permissoes-regras.ts";

const dto: PermissoesUsuarioPafDto = {
  usuarioId: "u",
  grupos: [],
  dosPapeis: ["encaminhar"],
  individuais: ["vip", "estatistica"],
  alteraveisPeloChamador: ["encaminhar", "vip", "fichas"],
};

test("permissão do papel fica marcada e travada, mesmo sem marca no painel", () => {
  assert.deepEqual(estadoDaPermissao("encaminhar", dto, new Set()), { marcada: true, travada: true, motivo: MOTIVO_DO_PAPEL });
});

test("permissão que o chamador não tem mostra o valor guardado e trava, nos dois sentidos", () => {
  assert.deepEqual(estadoDaPermissao("estatistica", dto, new Set()), { marcada: true, travada: true, motivo: MOTIVO_SEM_PERMISSAO });
  assert.deepEqual(estadoDaPermissao("usuarios", dto, new Set(["usuarios"])), { marcada: false, travada: true, motivo: MOTIVO_SEM_PERMISSAO });
});

test("permissão alterável segue a marca do painel", () => {
  assert.equal(estadoDaPermissao("fichas", dto, new Set(["fichas"])).marcada, true);
  assert.equal(estadoDaPermissao("vip", dto, new Set()).marcada, false);
  assert.equal(estadoDaPermissao("vip", dto, new Set()).travada, false);
});

test("alternar devolve conjunto novo e não altera o original", () => {
  const original = new Set(["a"]);
  assert.deepEqual([...alternar(original, "b")].sort(), ["a", "b"]);
  assert.deepEqual([...alternar(original, "a")], []);
  assert.deepEqual([...original], ["a"]);
});

test("o corpo enviado sai ordenado e o vazio é lista vazia", () => {
  assert.deepEqual(individuaisParaSalvar(new Set(["vip", "estatistica"])), ["estatistica", "vip"]);
  assert.deepEqual(individuaisParaSalvar(new Set()), []);
});

test("alterado compara conjunto, não ordem", () => {
  assert.equal(alterado(dto, new Set(["estatistica", "vip"])), false);
  assert.equal(alterado(dto, new Set(["vip"])), true);
  assert.equal(alterado(dto, new Set(["vip", "fichas"])), true);
});
