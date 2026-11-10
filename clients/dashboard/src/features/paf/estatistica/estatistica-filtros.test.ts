/// <reference types="node" />
// Run: node --test src/features/paf/estatistica/estatistica-filtros.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  consultaDaApi,
  hojeEmSaoPaulo,
  mensagensDeValidacao,
  MENSAGEM_PERIODO_INCOMPLETO,
  MENSAGEM_PERIODO_INVERTIDO,
  problemaDoPeriodo,
  TIPOS_ESTATISTICA,
  type FiltrosEstatistica,
} from "./estatistica-filtros.ts";

const base: FiltrosEstatistica = {
  tipo: 1,
  dataInicial: "2026-10-01",
  dataFinal: "2026-10-19",
  agrupamento: "Setor",
  unidadeId: null,
  filaId: null,
  prioridadeId: null,
  somenteEstacoesComAtendimento: true,
};

test("hoje é o dia do calendário de São Paulo, não o do UTC", () => {
  // 02:30Z on the 20th is still 23:30 of the 19th in BRT (UTC-3, no DST since 2019).
  assert.equal(hojeEmSaoPaulo(new Date("2026-10-20T02:30:00Z")), "2026-10-19");
  assert.equal(hojeEmSaoPaulo(new Date("2026-10-20T03:00:00Z")), "2026-10-20");
  assert.equal(hojeEmSaoPaulo(new Date("2026-01-01T02:59:59Z")), "2025-12-31");
});

test("período: final antes da inicial é bloqueado, mesmo dia e limites passam", () => {
  assert.equal(problemaDoPeriodo("2026-10-19", "2026-10-18"), MENSAGEM_PERIODO_INVERTIDO);
  assert.equal(problemaDoPeriodo("2026-10-19", "2026-10-19"), null);
  assert.equal(problemaDoPeriodo("2025-12-31", "2026-01-01"), null);
  assert.equal(problemaDoPeriodo("2026-02-01", "2026-10-01"), null);
});

test("período: data vazia ou fora do formato é bloqueada", () => {
  assert.equal(problemaDoPeriodo("", "2026-10-19"), MENSAGEM_PERIODO_INCOMPLETO);
  assert.equal(problemaDoPeriodo("2026-10-19", ""), MENSAGEM_PERIODO_INCOMPLETO);
  assert.equal(problemaDoPeriodo("19/10/2026", "2026-10-19"), MENSAGEM_PERIODO_INCOMPLETO);
});

test("consulta: agrupamento só nos tipos 9 e 10, estações com atendimento só no tipo 3", () => {
  const tipo1 = new URLSearchParams(consultaDaApi(base));
  assert.equal(tipo1.get("tipo"), "1");
  assert.equal(tipo1.has("agrupamento"), false);
  assert.equal(tipo1.has("somenteEstacoesComAtendimento"), false);
  assert.equal(new URLSearchParams(consultaDaApi({ ...base, tipo: 9 })).get("agrupamento"), "Setor");
  assert.equal(new URLSearchParams(consultaDaApi({ ...base, tipo: 10, agrupamento: "Fila" })).get("agrupamento"), "Fila");
  assert.equal(new URLSearchParams(consultaDaApi({ ...base, tipo: 3 })).get("somenteEstacoesComAtendimento"), "true");
  assert.equal(new URLSearchParams(consultaDaApi({ ...base, tipo: 3, somenteEstacoesComAtendimento: false })).has("somenteEstacoesComAtendimento"), false);
});

test("consulta: datas vão como o calendário escolheu; unidade, fila e prioridade só quando escolhidas", () => {
  const vazio = new URLSearchParams(consultaDaApi(base));
  assert.equal(vazio.get("dataInicial"), "2026-10-01");
  assert.equal(vazio.get("dataFinal"), "2026-10-19");
  assert.equal(vazio.has("unidades") || vazio.has("filas") || vazio.has("prioridades"), false);
  const cheio = new URLSearchParams(consultaDaApi({ ...base, unidadeId: "u-1", filaId: "f-1", prioridadeId: "p-1" }));
  assert.deepEqual([cheio.getAll("unidades"), cheio.getAll("filas"), cheio.getAll("prioridades")], [["u-1"], ["f-1"], ["p-1"]]);
});

test("os dez tipos do RF07 com os números que o cliente usa", () => {
  assert.deepEqual(TIPOS_ESTATISTICA.map((t) => t.valor), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
});

test("mensagens do 400: por campo (FluentValidation) e lista simples (CustomException)", () => {
  assert.deepEqual(mensagensDeValidacao({ DataFinal: [MENSAGEM_PERIODO_INVERTIDO], Tipo: ["a", "b"] }), [MENSAGEM_PERIODO_INVERTIDO, "a", "b"]);
  assert.deepEqual(mensagensDeValidacao(["x"]), ["x"]);
  assert.deepEqual(mensagensDeValidacao(undefined), []);
});
