/// <reference types="node" />
// Run: node --test src/features/paf/fichas-em-atendimento/fichas-em-atendimento-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { formatarEntrada, formatarPermanencia, rotuloEstado, textoTotal } from "./fichas-em-atendimento-regras.ts";

test("permanência abaixo de um minuto, em minutos, em horas e em dias", () => {
  assert.equal(formatarPermanencia(0), "menos de 1 min");
  assert.equal(formatarPermanencia(59), "menos de 1 min");
  assert.equal(formatarPermanencia(60), "1 min");
  assert.equal(formatarPermanencia(3599), "59 min");
  assert.equal(formatarPermanencia(3 * 3600), "3 h 00 min");
  assert.equal(formatarPermanencia(3725), "1 h 02 min");
  assert.equal(formatarPermanencia(86400 + 3600), "1 d 1 h 00 min");
});

test("permanência negativa ou inválida nunca vira texto quebrado", () => {
  assert.equal(formatarPermanencia(-5), "menos de 1 min");
  assert.equal(formatarPermanencia(Number.NaN), "menos de 1 min");
  assert.equal(formatarPermanencia(Number.POSITIVE_INFINITY), "menos de 1 min");
});

test("entrada no relógio de São Paulo, com o dia", () => {
  // 02:30 UTC is still the previous day in São Paulo (UTC-3, no DST since 2019).
  assert.equal(formatarEntrada("2026-10-19T02:30:00Z"), "18/10, 23:30");
  assert.equal(formatarEntrada("2026-10-19T13:05:00+00:00"), "19/10, 10:05");
});

test("estado e total em texto para o operador", () => {
  assert.equal(rotuloEstado("EmAtendimento"), "Em atendimento");
  assert.equal(rotuloEstado("Chamado"), "Chamado");
  assert.equal(textoTotal(0), "Nenhuma ficha em atendimento.");
  assert.equal(textoTotal(1), "1 ficha em atendimento");
  assert.equal(textoTotal(2), "2 fichas em atendimento");
});
