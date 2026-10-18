/// <reference types="node" />
// Run: node --test src/features/paf/guiche/guiche-fluxo.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  chamarProxima,
  encerrarEChamar,
  MENSAGEM_NAO_ATENDENDO,
  perguntaCancelarEChamar,
  perguntaEncerrarEChamar,
  RecusaGuiche,
  type AtendimentoNaTela,
  type PassosGuiche,
} from "./guiche-fluxo.ts";

const CHAMADO: AtendimentoNaTela = { atendimentoId: "a1", senha: "N007", estado: "Chamado" };
const EM_ATENDIMENTO: AtendimentoNaTela = { atendimentoId: "a2", senha: "P003", estado: "EmAtendimento" };

type Registro = { chamadas: string[]; perguntas: string[] };

function passos(resposta: boolean, falhaEm: string | null): PassosGuiche & Registro {
  const chamadas: string[] = [];
  const perguntas: string[] = [];
  const registrar = async (passo: string) => {
    chamadas.push(passo);
    if (passo === falhaEm) throw new Error(`falha em ${passo}`);
  };
  return {
    chamadas,
    perguntas,
    confirmar: (pergunta) => {
      perguntas.push(pergunta);
      return resposta;
    },
    operar: (atual, operacao) => registrar(`${operacao}:${atual.atendimentoId}`),
    chamar: () => registrar("chamar"),
  };
}

test("sem atendimento chama direto, sem perguntar", async () => {
  const p = passos(false, null);
  assert.equal(await chamarProxima(null, p), true);
  assert.deepEqual(p.perguntas, []);
  assert.deepEqual(p.chamadas, ["chamar"]);
});

test("com Chamado confirmado cancela e só depois chama", async () => {
  const p = passos(true, null);
  assert.equal(await chamarProxima(CHAMADO, p), true);
  assert.deepEqual(p.perguntas, [perguntaCancelarEChamar("N007")]);
  assert.deepEqual(p.chamadas, ["cancelar:a1", "chamar"]);
});

test("com EmAtendimento confirmado encerra e só depois chama", async () => {
  const p = passos(true, null);
  assert.equal(await chamarProxima(EM_ATENDIMENTO, p), true);
  assert.deepEqual(p.perguntas, [perguntaEncerrarEChamar("P003")]);
  assert.deepEqual(p.chamadas, ["encerrar:a2", "chamar"]);
});

for (const atual of [CHAMADO, EM_ATENDIMENTO]) {
  test(`recusa da confirmação com ${atual.estado} não dispara requisição`, async () => {
    const p = passos(false, null);
    assert.equal(await chamarProxima(atual, p), false);
    assert.equal(p.perguntas.length, 1);
    assert.deepEqual(p.chamadas, []);
  });
}

test("erro no cancelar não dispara chamar e o erro chega à tela", async () => {
  const p = passos(true, "cancelar:a1");
  await assert.rejects(chamarProxima(CHAMADO, p), /falha em cancelar:a1/);
  assert.deepEqual(p.chamadas, ["cancelar:a1"]);
});

test("erro no encerrar não dispara chamar", async () => {
  const p = passos(true, "encerrar:a2");
  await assert.rejects(chamarProxima(EM_ATENDIMENTO, p), /falha em encerrar:a2/);
  assert.deepEqual(p.chamadas, ["encerrar:a2"]);
});

test("fila vazia no segundo passo: o cancelamento fica e o erro do chamar chega à tela", async () => {
  const p = passos(true, "chamar");
  await assert.rejects(chamarProxima(CHAMADO, p), /falha em chamar/);
  assert.deepEqual(p.chamadas, ["cancelar:a1", "chamar"]);
});

test("encerrar e chamar com EmAtendimento encerra e só depois chama, sem perguntar", async () => {
  const p = passos(false, null);
  await encerrarEChamar(EM_ATENDIMENTO, p);
  assert.deepEqual(p.perguntas, []);
  assert.deepEqual(p.chamadas, ["encerrar:a2", "chamar"]);
});

for (const atual of [CHAMADO, null]) {
  test(`encerrar e chamar com ${atual?.estado ?? "nenhum atendimento"} recusa sem requisição`, async () => {
    const p = passos(true, null);
    await assert.rejects(encerrarEChamar(atual, p), (e: unknown) => {
      assert.ok(e instanceof RecusaGuiche);
      assert.equal(e.message, MENSAGEM_NAO_ATENDENDO);
      return true;
    });
    assert.deepEqual(p.chamadas, []);
  });
}

test("erro no encerrar de encerrar e chamar não dispara chamar", async () => {
  const p = passos(true, "encerrar:a2");
  await assert.rejects(encerrarEChamar(EM_ATENDIMENTO, p), /falha em encerrar:a2/);
  assert.deepEqual(p.chamadas, ["encerrar:a2"]);
});
