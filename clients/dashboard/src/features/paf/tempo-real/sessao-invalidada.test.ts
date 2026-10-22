/// <reference types="node" />
// Run: node --test src/features/paf/tempo-real/sessao-invalidada.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  avisoDaPropriaSessao,
  lerSessaoInvalidada,
  MOTIVO_PADRAO,
  SESSAO_INVALIDADA,
  tratarSessaoInvalidada,
  type AcoesSessaoInvalidada,
} from "./sessao-invalidada.ts";

const SID = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

test("o nome do método é o do contrato do servidor", () => {
  assert.equal(SESSAO_INVALIDADA, "SessaoInvalidada");
});

test("aviso de outra sessão não desloga esta tela", () => {
  const aviso = lerSessaoInvalidada({ sessaoId: "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d", motivo: "Sessão aberta em outra máquina." });
  assert.ok(aviso);
  assert.equal(avisoDaPropriaSessao(aviso, SID), false);
});

test("aviso da própria sessão desloga, sem depender da caixa do Guid", () => {
  const aviso = lerSessaoInvalidada({ sessaoId: SID.toUpperCase(), motivo: "Sessão aberta em outra máquina." });
  assert.ok(aviso);
  assert.equal(avisoDaPropriaSessao(aviso, SID), true);
  assert.equal(aviso.motivo, "Sessão aberta em outra máquina.");
});

test("tela sem sid (token sem sessão, raiz) nunca se desloga pelo aviso", () => {
  const aviso = lerSessaoInvalidada({ sessaoId: SID, motivo: "x" });
  assert.ok(aviso);
  assert.equal(avisoDaPropriaSessao(aviso, undefined), false);
  assert.equal(avisoDaPropriaSessao(aviso, ""), false);
});

test("carga malformada é descartada", () => {
  for (const dado of [null, undefined, "SessaoInvalidada", 42, {}, { sessaoId: "" }, { sessaoId: 7, motivo: "x" }]) {
    assert.equal(lerSessaoInvalidada(dado), null, JSON.stringify(dado));
  }
});

test("motivo ausente ou em branco vira a frase padrão", () => {
  assert.equal(lerSessaoInvalidada({ sessaoId: SID })?.motivo, MOTIVO_PADRAO);
  assert.equal(lerSessaoInvalidada({ sessaoId: SID, motivo: "   " })?.motivo, MOTIVO_PADRAO);
});

function acoesRegistradas(): { acoes: AcoesSessaoInvalidada; chamadas: string[] } {
  const chamadas: string[] = [];
  const acoes = {
    pararConexao: () => chamadas.push("parar"),
    avisar: (motivo: string) => chamadas.push(`avisar:${motivo}`),
    sairLocalmente: () => chamadas.push("sairLocalmente"),
  };
  return { acoes, chamadas };
}

test("o aviso da própria sessão para a conexão, avisa o motivo e sai só localmente", () => {
  const { acoes, chamadas } = acoesRegistradas();

  tratarSessaoInvalidada({ sessaoId: SID, motivo: "Encerrada em outra aba." }, SID, acoes);

  assert.deepEqual(chamadas, ["parar", "avisar:Encerrada em outra aba.", "sairLocalmente"]);
});

test("aviso de outra sessão, malformado ou sem sessão atual não faz nada", () => {
  const { acoes, chamadas } = acoesRegistradas();

  tratarSessaoInvalidada({ sessaoId: "9b2c1d3e-0000-4000-8000-000000000000", motivo: "x" }, SID, acoes);
  tratarSessaoInvalidada({ motivo: "sem id" }, SID, acoes);
  tratarSessaoInvalidada(null, SID, acoes);
  tratarSessaoInvalidada({ sessaoId: SID, motivo: "x" }, undefined, acoes);

  assert.deepEqual(chamadas, []);
});
