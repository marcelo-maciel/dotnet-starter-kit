/// <reference types="node" />
// Run: node --test src/features/paf/tempo-real/conexao-sessao.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { avisoDeOutraAba, chaveConexaoPafHub, lerAvisoTrocaUnidade, type AvisoTrocaUnidade } from "./conexao-sessao.ts";

const PESSOA = { sub: "u1", tenant: "acme", sid: "s1" };

test("a renovação do token mantém a chave, e outra sessão ou outro usuário a troca", () => {
  const chave = chaveConexaoPafHub(PESSOA);
  assert.notEqual(chave, null);
  assert.equal(chaveConexaoPafHub({ ...PESSOA }), chave);
  assert.notEqual(chaveConexaoPafHub({ ...PESSOA, sid: "s2" }), chave);
  assert.notEqual(chaveConexaoPafHub({ ...PESSOA, sub: "u2" }), chave);
  assert.notEqual(chaveConexaoPafHub({ ...PESSOA, tenant: "globex" }), chave);
});

test("sem token, sem sub ou sem tenant não conecta", () => {
  assert.equal(chaveConexaoPafHub(null), null);
  assert.equal(chaveConexaoPafHub({ tenant: "acme" }), null);
  assert.equal(chaveConexaoPafHub({ sub: "u1" }), null);
  assert.equal(chaveConexaoPafHub({ sub: "", tenant: "acme" }), null);
});

test("só a impersonação da Codefi conecta; a do administrador do cliente receberia 403 no hub", () => {
  const codefi = chaveConexaoPafHub({ ...PESSOA, act_sub: "op1", act_tenant: "root" });
  assert.notEqual(codefi, null);
  assert.notEqual(codefi, chaveConexaoPafHub(PESSOA));
  assert.equal(chaveConexaoPafHub({ ...PESSOA, act_sub: "adm", act_tenant: "acme" }), null);
  assert.equal(chaveConexaoPafHub({ ...PESSOA, act_sub: "adm" }), null);
  assert.equal(chaveConexaoPafHub({ ...PESSOA, act_sub: "adm", act_tenant: "ROOT" }), null);
});

const AVISO: AvisoTrocaUnidade = { tenant: "acme", usuarioId: "u1", abaId: "a1", unidadeId: "x", unidade: "Loja B" };

test("aviso bem formado é lido; qualquer outra mensagem do canal é descartada", () => {
  assert.deepEqual(lerAvisoTrocaUnidade({ ...AVISO, extra: 1 }), AVISO);
  assert.equal(lerAvisoTrocaUnidade(null), null);
  assert.equal(lerAvisoTrocaUnidade("troca"), null);
  assert.equal(lerAvisoTrocaUnidade({ ...AVISO, unidadeId: 7 }), null);
  assert.equal(lerAvisoTrocaUnidade({ ...AVISO, unidade: "" }), null);
  assert.equal(lerAvisoTrocaUnidade({ ...AVISO, abaId: undefined }), null);
});

test("só a troca do mesmo usuário e tenant feita em outra aba interessa", () => {
  const aba = { tenant: "acme", usuarioId: "u1", abaId: "a2" };
  assert.equal(avisoDeOutraAba(AVISO, aba), true);
  assert.equal(avisoDeOutraAba(AVISO, { ...aba, abaId: "a1" }), false, "a própria aba recebe o que postou");
  assert.equal(avisoDeOutraAba(AVISO, { ...aba, usuarioId: "u2" }), false);
  assert.equal(avisoDeOutraAba(AVISO, { ...aba, tenant: "globex" }), false);
});
