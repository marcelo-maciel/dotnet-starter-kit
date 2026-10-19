/// <reference types="node" />
// Run: node --test src/features/paf/prioridades/prioridades-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  combinacoesQueSomemDoTotem,
  combinacoesSemGuiche,
  estaEmAlgumaFilaAtiva,
  validarPrioridade,
  type FilaComGrade,
} from "./prioridades-regras.ts";

test("nome e valor válidos voltam limpos", () => {
  assert.deepEqual(validarPrioridade("  Prioritário 80+ ", " 20 "), { ok: true, nome: "Prioritário 80+", valor: 20 });
  // The domain has no floor: zero and negatives are values like any other.
  assert.deepEqual(validarPrioridade("Normal", "0"), { ok: true, nome: "Normal", valor: 0 });
  assert.deepEqual(validarPrioridade("Retorno", "-5"), { ok: true, nome: "Retorno", valor: -5 });
});

test("nome vazio ou só com espaços é recusado", () => {
  for (const nome of ["", "   ", "\t"]) {
    const r = validarPrioridade(nome, "1");
    assert.equal(r.ok, false, JSON.stringify(nome));
  }
});

test("nome acima de 120 caracteres é recusado, 120 passa", () => {
  assert.equal(validarPrioridade("x".repeat(121), "1").ok, false);
  assert.equal(validarPrioridade("x".repeat(120), "1").ok, true);
});

test("valor que não é inteiro é recusado", () => {
  for (const valor of ["1.5", "1,5", "", " ", "abc", "1e3", "0x10", "+", "-", "2.0"]) {
    const r = validarPrioridade("Normal", valor);
    assert.equal(r.ok, false, JSON.stringify(valor));
    if (!r.ok) assert.match(r.mensagem, /inteiro|limite/);
  }
});

test("valor fora do int do servidor é recusado nos dois extremos", () => {
  assert.equal(validarPrioridade("A", "2147483647").ok, true);
  assert.equal(validarPrioridade("A", "-2147483648").ok, true);
  assert.equal(validarPrioridade("A", "2147483648").ok, false);
  assert.equal(validarPrioridade("A", "-2147483649").ok, false);
});

const PREF = "p-pref";
const NORMAL = "p-normal";

const filas: FilaComGrade[] = [
  {
    nome: "Caixa",
    ativa: true,
    prioridades: [
      { id: "l1", prioridadeId: NORMAL, letra: "A" },
      { id: "l2", prioridadeId: PREF, letra: "AP" },
    ],
    combinacoesSemCobertura: [],
  },
  {
    nome: "Protocolo",
    ativa: true,
    prioridades: [{ id: "l3", prioridadeId: PREF, letra: "BP" }],
    // No guichê covers Protocolo/Preferencial: that button is already off the totem.
    combinacoesSemCobertura: ["l3"],
  },
  {
    nome: "Retaguarda",
    ativa: false,
    prioridades: [{ id: "l4", prioridadeId: PREF, letra: "RP" }],
    combinacoesSemCobertura: [],
  },
];

test("desativar avisa só os botões que hoje estão no totem", () => {
  assert.deepEqual(combinacoesQueSomemDoTotem(PREF, filas), ["Caixa (AP)"]);
  assert.deepEqual(combinacoesQueSomemDoTotem(NORMAL, filas), ["Caixa (A)"]);
  assert.deepEqual(combinacoesQueSomemDoTotem("outra", filas), []);
  assert.deepEqual(combinacoesQueSomemDoTotem(PREF, []), []);
});

test("sem guichê lista só as linhas descobertas de filas ativas", () => {
  assert.deepEqual(combinacoesSemGuiche(PREF, filas), ["Protocolo (BP)"]);
  assert.deepEqual(combinacoesSemGuiche(NORMAL, filas), []);
});

test("prioridade só em fila inativa não está em nenhuma fila ativa", () => {
  const soInativa: FilaComGrade[] = [filas[2]];
  assert.equal(estaEmAlgumaFilaAtiva(PREF, soInativa), false);
  assert.equal(estaEmAlgumaFilaAtiva(PREF, filas), true);
  assert.equal(estaEmAlgumaFilaAtiva("nova", filas), false);
});
