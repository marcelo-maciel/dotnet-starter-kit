/// <reference types="node" />
// Run: node --test src/features/paf/estacao/link-vinculo.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { lerLinkVinculo, montarLinkVinculo, ROTA_RESGATE_VINCULO } from "./link-vinculo.ts";

const ORIGEM = "https://paf.exemplo.com.br";

test("tenant com & e ç e token com + e / voltam iguais pela leitura da página de resgate", () => {
  for (const [tenant, token] of [
    ["a&b", "ab+cd/ef"],
    ["loja-ç", "+/+/"],
    ["x=y#z", "Zm9v+YmFy/YmF6"],
    ["acme", "AbC_dEf-123"],
  ]) {
    const link = montarLinkVinculo(ORIGEM, tenant, token);
    const url = new URL(link);
    assert.equal(url.origin + url.pathname, ORIGEM + ROTA_RESGATE_VINCULO);
    assert.equal(url.search, "", "nada do link vai na query, que chega ao log do servidor");
    assert.deepEqual(lerLinkVinculo(url.hash), { tenant, token }, link);
  }
});

test("o fragmento leva + e / codificados, nunca crus", () => {
  const link = montarLinkVinculo(ORIGEM, "a&b", "ab+cd/ef");
  const fragmento = link.slice(link.indexOf("#"));
  assert.equal(fragmento, "#tenant=a%26b&token=ab%2Bcd%2Fef");
});

test("fragmento sem cliente, sem token ou com valor em branco não é link", () => {
  assert.equal(lerLinkVinculo(""), null);
  assert.equal(lerLinkVinculo("#token=abc"), null);
  assert.equal(lerLinkVinculo("#tenant=acme"), null);
  assert.equal(lerLinkVinculo("#tenant=%20&token=abc"), null);
  assert.deepEqual(lerLinkVinculo("tenant=acme&token=abc"), { tenant: "acme", token: "abc" }, "o # inicial é opcional");
});
