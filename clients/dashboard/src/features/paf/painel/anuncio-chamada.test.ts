/// <reference types="node" />
// Run: node --test src/features/paf/painel/anuncio-chamada.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  ANUNCIO_PADRAO,
  FALA_SENHA_E_MENSAGEM,
  FALA_SO_SENHA,
  lerAnuncioDaChamada,
  textoDoAnuncio,
} from "./anuncio-chamada.ts";

test("padrão: fala só a senha, sem mensagem", () => {
  assert.equal(textoDoAnuncio("A12", ANUNCIO_PADRAO), "senha A 12");
});

test("mensagem gravada com a fala só da senha: a voz não lê a mensagem", () => {
  assert.equal(
    textoDoAnuncio("AP3", { mensagemAoChamar: "Dirija-se ao guichê", chamarFichaPorVoz: FALA_SO_SENHA }),
    "senha AP 3",
  );
});

test("senha e mensagem: a voz fala a senha e depois a mensagem", () => {
  assert.equal(
    textoDoAnuncio("B7", { mensagemAoChamar: "Dirija-se ao guichê", chamarFichaPorVoz: FALA_SENHA_E_MENSAGEM }),
    "senha B 7. Dirija-se ao guichê",
  );
});

test("senha e mensagem sem mensagem gravada: só a senha, sem ponto solto", () => {
  assert.equal(textoDoAnuncio("B7", { mensagemAoChamar: "", chamarFichaPorVoz: FALA_SENHA_E_MENSAGEM }), "senha B 7");
});

test("valor de voz desconhecido nunca cala a chamada (RNF19)", () => {
  for (const voz of ["false", "", "desligada"]) {
    assert.equal(textoDoAnuncio("A1", { mensagemAoChamar: "Olá", chamarFichaPorVoz: voz }), "senha A 1");
  }
});

test("leitura da configuração: ausente ou malformada cai no padrão; mensagem aparada", () => {
  assert.deepEqual(lerAnuncioDaChamada(undefined), ANUNCIO_PADRAO);
  assert.deepEqual(lerAnuncioDaChamada(null), ANUNCIO_PADRAO);
  assert.deepEqual(lerAnuncioDaChamada("x"), ANUNCIO_PADRAO);
  assert.deepEqual(lerAnuncioDaChamada({ mensagemAoChamar: 3, chamarFichaPorVoz: null }), ANUNCIO_PADRAO);
  assert.deepEqual(lerAnuncioDaChamada({ mensagemAoChamar: "  Bem-vindo  ", chamarFichaPorVoz: FALA_SENHA_E_MENSAGEM }), {
    mensagemAoChamar: "Bem-vindo",
    chamarFichaPorVoz: FALA_SENHA_E_MENSAGEM,
  });
});
