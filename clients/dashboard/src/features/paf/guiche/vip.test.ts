/// <reference types="node" />
// Run: node --test src/features/paf/guiche/vip.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { hojeEmSaoPaulo, montarPedidoVip, podeChamarVip, PERMISSAO_VIP, SENHA_MAX } from "./vip.ts";

const FILA = "0199f0aa-0000-7000-8000-000000000001";

test("senha digitada em minúscula e com espaços vai normalizada", () => {
  assert.deepEqual(montarPedidoVip("  ap12 ", "2026-10-19", FILA), {
    ok: true,
    pedido: { senha: "AP12", data: "2026-10-19", filaId: FILA },
  });
});

test("senha vazia, longa demais, sem data ou sem fila é recusada antes do envio", () => {
  assert.equal(montarPedidoVip("   ", "2026-10-19", FILA).ok, false);
  assert.equal(montarPedidoVip("A".repeat(SENHA_MAX + 1), "2026-10-19", FILA).ok, false);
  assert.equal(montarPedidoVip("A".repeat(SENHA_MAX), "2026-10-19", FILA).ok, true);
  assert.equal(montarPedidoVip("A1", "", FILA).ok, false);
  assert.equal(montarPedidoVip("A1", "19/10/2026", FILA).ok, false);
  assert.equal(montarPedidoVip("A1", "2026-10-19", "").ok, false);
});

test("o dia é o de São Paulo: 01h UTC ainda é o dia anterior", () => {
  assert.equal(hojeEmSaoPaulo(new Date("2026-10-20T01:00:00Z")), "2026-10-19");
  assert.equal(hojeEmSaoPaulo(new Date("2026-10-20T03:00:00Z")), "2026-10-20");
  assert.equal(hojeEmSaoPaulo(new Date("2026-01-01T02:59:59Z")), "2025-12-31");
});

test("o formulário só aparece com a permissão exata do VIP", () => {
  assert.equal(podeChamarVip([PERMISSAO_VIP]), true);
  assert.equal(podeChamarVip(["Permissions.PAF.Atendimentos.Operar"]), false);
  assert.equal(podeChamarVip([PERMISSAO_VIP.toLowerCase()]), false);
  assert.equal(podeChamarVip([]), false);
});
