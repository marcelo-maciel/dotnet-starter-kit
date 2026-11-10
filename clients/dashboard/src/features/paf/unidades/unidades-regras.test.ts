/// <reference types="node" />
// Run: node --test src/features/paf/unidades/unidades-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { acoesDaUnidade, AVISO_CONTRATO, dataDoCalendario, resumoContrato, SEM_CONTRATO } from "./unidades-regras.ts";

test("escopo empresa desativa a ativa e reativa a inativa", () => {
  assert.deepEqual(acoesDaUnidade(true, true), { renomear: true, desativar: true, reativar: false });
  assert.deepEqual(acoesDaUnidade(true, false), { renomear: true, desativar: false, reativar: true });
});

test("filial nunca vê desativar nem reativar, em unidade ativa ou inativa", () => {
  assert.deepEqual(acoesDaUnidade(false, true), { renomear: true, desativar: false, reativar: false });
  assert.deepEqual(acoesDaUnidade(false, false), { renomear: true, desativar: false, reativar: false });
});

test("data do contrato não muda de dia pelo fuso do navegador", () => {
  assert.equal(dataDoCalendario("2027-01-01"), "01/01/2027");
  assert.equal(dataDoCalendario("2026-12-31"), "31/12/2026");
});

test("resumo do contrato: plano, limite e fim; singular para um login", () => {
  assert.equal(resumoContrato({ plano: "Pro", limiteSessoes: 12, fimVigencia: "2027-03-31" }), "Plano Pro · 12 logins simultâneos · até 31/03/2027");
  assert.equal(resumoContrato({ plano: "Básico", limiteSessoes: 1, fimVigencia: null }), "Plano Básico · 1 login simultâneo · sem data de fim");
});

test("sem contrato vigente o resumo avisa que a unidade não recebe logins", () => {
  assert.equal(resumoContrato(null), SEM_CONTRATO);
  assert.match(AVISO_CONTRATO, /só recebe logins depois que a Codefi gravar o contrato/);
});
