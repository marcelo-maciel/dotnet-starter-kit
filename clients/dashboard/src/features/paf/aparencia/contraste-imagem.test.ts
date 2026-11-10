/// <reference types="node" />
// Run: node --test src/features/paf/aparencia/contraste-imagem.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  amostraValida,
  amostrarLuminancias,
  avisosContraImagem,
  piorRazaoContraImagem,
  suficienteContraImagem,
} from "./contraste-imagem.ts";

function pixels(...cores: [number, number, number, number][]): Uint8ClampedArray {
  return new Uint8ClampedArray(cores.flat());
}

test("imagem branca: só a faixa 255, como o servidor espera", () => {
  const amostra = amostrarLuminancias(pixels([255, 255, 255, 255]), "#000000");
  assert.equal(amostra, "0".repeat(63) + "1");
  assert.equal(suficienteContraImagem("#FFFFFF", amostra), false);
  assert.equal(suficienteContraImagem("#000000", amostra), true);
  assert.equal(suficienteContraImagem("#767676", amostra), true);
  assert.equal(suficienteContraImagem("#777777", amostra), false);
});

test("imagem preta e branca: nenhuma cor passa", () => {
  const amostra = amostrarLuminancias(pixels([0, 0, 0, 255], [255, 255, 255, 255]), "#808080");
  assert.equal(amostra, "8" + "0".repeat(62) + "1");
  for (const cor of ["#000000", "#FFFFFF", "#767676", "#FF0000"]) assert.equal(suficienteContraImagem(cor, amostra), false, cor);
});

test("pixel transparente mostra o fundo do bloco", () => {
  assert.equal(amostrarLuminancias(pixels([255, 255, 255, 0]), "#000000"), "8" + "0".repeat(63));
  assert.equal(amostrarLuminancias(pixels([0, 0, 0, 0]), "#FFFFFF"), "0".repeat(63) + "1");
  // Half-transparent white over black is a mid gray, never the white of the file.
  const meio = amostrarLuminancias(pixels([255, 255, 255, 128]), "#000000");
  assert.notEqual(meio, "0".repeat(63) + "1");
  assert.equal(suficienteContraImagem("#FFFFFF", meio), false);
});

test("pior ponto é a borda mais próxima da faixa", () => {
  const amostra = "0".repeat(50) + "8" + "0".repeat(13); // band 200 only
  assert.ok(Math.abs(piorRazaoContraImagem("#FFFFFF", amostra) - 1.05 / (201 / 256 + 0.05)) < 1e-12);
});

test("avisos do cadastro: só as cores que não passam, no texto do servidor", () => {
  const clara = "0".repeat(60) + "FFFF";
  assert.deepEqual(avisosContraImagem(clara, [["Texto", "#000000"], ["Filas", "#FFFFFF"], ["Chamada", "#GGG"]]), [
    "Filas sobre a imagem de fundo: contraste 1.00:1 no pior ponto, mínimo 4.5:1.",
  ]);
  assert.deepEqual(avisosContraImagem("0".repeat(64), [["Filas", "#FFFFFF"]]), []);
});

test("amostra fora do formato é recusada", () => {
  for (const a of [null, "", "0".repeat(64), "0".repeat(63), "G" + "0".repeat(63)]) assert.equal(amostraValida(a), false, String(a));
  assert.throws(() => piorRazaoContraImagem("#000000", "0".repeat(64)));
  assert.throws(() => amostrarLuminancias(new Uint8ClampedArray(0), "#000000"));
  assert.throws(() => amostrarLuminancias(pixels([0, 0, 0, 255]), "#FFF"));
});
