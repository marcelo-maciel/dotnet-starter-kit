/// <reference types="node" />
// Run: node --test src/features/paf/logotipo/logotipo.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { aguardarImagens, lerLogotipo } from "./logotipo.ts";

test("lerLogotipo: URL assinada do backend vira o src", () => {
  const url = "https://r2.exemplo.com/app/paf-midia/t/u/abc.png?X-Amz-Expires=3600&X-Amz-Signature=f00";
  assert.equal(lerLogotipo({ url, expiraEm: "2026-10-19T12:00:00Z" }), url);
  assert.equal(lerLogotipo({ url: "http://localhost:5030/api/v1/files/x.png" }), "http://localhost:5030/api/v1/files/x.png");
});

test("lerLogotipo: nulo, ausente ou malformado é o logotipo do sistema", () => {
  for (const dado of [null, undefined, "https://x/y.png", 42, {}, { url: 3 }, { url: "" }, { url: "logo.png" }]) {
    assert.equal(lerLogotipo(dado), null, JSON.stringify(dado));
  }
});

test("lerLogotipo: recusa esquema que não seja http(s)", () => {
  for (const url of ["javascript:alert(1)", "data:image/png;base64,AAAA", "file:///C:/logo.png", "blob:https://x/1"]) {
    assert.equal(lerLogotipo({ url }), null, url);
  }
});

const imagem = (decode: () => Promise<void>) => ({ decode });

test("aguardarImagens: espera todas decodificarem", async () => {
  const ordem: string[] = [];
  const lenta = imagem(() => new Promise<void>((r) => setTimeout(() => (ordem.push("lenta"), r()), 30)));
  await aguardarImagens([lenta, imagem(() => Promise.resolve())], 2_000);
  ordem.push("fim");
  assert.deepEqual(ordem, ["lenta", "fim"]);
});

test("aguardarImagens: imagem que falha não segura nem rejeita a impressão", async () => {
  await aguardarImagens([imagem(() => Promise.reject(new Error("404")))], 2_000);
});

test("aguardarImagens: imagem que nunca carrega solta no prazo", async () => {
  const inicio = Date.now();
  await aguardarImagens([imagem(() => new Promise<void>(() => undefined))], 50);
  const decorrido = Date.now() - inicio;
  assert.ok(decorrido >= 45 && decorrido < 1_000, `decorrido ${decorrido} ms`);
});

test("aguardarImagens: sem imagem resolve na hora", async () => {
  await aguardarImagens([], 10_000);
});
