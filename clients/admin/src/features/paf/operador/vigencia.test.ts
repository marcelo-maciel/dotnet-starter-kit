/// <reference types="node" />
// Run: node --test src/features/paf/operador/vigencia.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { contratoSobreposto, fimAntesDoInicio, hojeEmSaoPaulo, situacaoContrato, type Vigencia } from "./vigencia.ts";

const PRIMEIRO: Vigencia = { id: "a", inicioVigencia: "2026-01-01", fimVigencia: "2026-06-30" };
const ABERTO: Vigencia = { id: "b", inicioVigencia: "2026-07-01", fimVigencia: null };

test("as pontas são dias inclusivos: começar no último dia de outro sobrepõe, no dia seguinte não", () => {
  assert.equal(contratoSobreposto([PRIMEIRO], { id: "", inicioVigencia: "2026-06-30", fimVigencia: null }), PRIMEIRO);
  assert.equal(contratoSobreposto([PRIMEIRO], { id: "", inicioVigencia: "2026-07-01", fimVigencia: null }), undefined);
  assert.equal(contratoSobreposto([PRIMEIRO], { id: "", inicioVigencia: "2025-01-01", fimVigencia: "2026-01-01" }), PRIMEIRO);
  assert.equal(contratoSobreposto([PRIMEIRO], { id: "", inicioVigencia: "2025-01-01", fimVigencia: "2025-12-31" }), undefined);
});

test("fim aberto cobre todo o futuro, dos dois lados", () => {
  assert.equal(contratoSobreposto([ABERTO], { id: "", inicioVigencia: "2031-01-01", fimVigencia: "2031-12-31" }), ABERTO);
  assert.equal(contratoSobreposto([PRIMEIRO], { id: "", inicioVigencia: "2025-01-01", fimVigencia: null }), PRIMEIRO);
});

test("a edição compara só com os outros contratos", () => {
  assert.equal(contratoSobreposto([PRIMEIRO, ABERTO], { ...PRIMEIRO, inicioVigencia: "2026-02-01" }), undefined);
  assert.equal(contratoSobreposto([PRIMEIRO, ABERTO], { ...PRIMEIRO, fimVigencia: "2026-07-01" }), ABERTO);
  assert.equal(contratoSobreposto([], { id: "", inicioVigencia: "2026-01-01", fimVigencia: null }), undefined);
});

test("fim antes do início é recusado; fim igual ao início e fim aberto passam", () => {
  assert.equal(fimAntesDoInicio("2026-06-01", "2026-05-31"), true);
  assert.equal(fimAntesDoInicio("2026-06-01", "2026-06-01"), false);
  assert.equal(fimAntesDoInicio("2026-06-01", null), false);
});

test("situação pelo dia de hoje, pontas inclusivas", () => {
  assert.equal(situacaoContrato(PRIMEIRO, "2026-01-01"), "Vigente");
  assert.equal(situacaoContrato(PRIMEIRO, "2026-06-30"), "Vigente");
  assert.equal(situacaoContrato(PRIMEIRO, "2026-07-01"), "Encerrado");
  assert.equal(situacaoContrato(PRIMEIRO, "2025-12-31"), "Futuro");
  assert.equal(situacaoContrato(ABERTO, "2099-01-01"), "Vigente");
});

test("hoje é o dia de São Paulo, não o de UTC", () => {
  // 02:00 UTC on 1 Jan is still 31 Dec in São Paulo (UTC-3, no DST since 2019).
  assert.equal(hojeEmSaoPaulo(new Date("2026-01-01T02:00:00Z")), "2025-12-31");
  assert.equal(hojeEmSaoPaulo(new Date("2026-01-01T03:00:00Z")), "2026-01-01");
});
