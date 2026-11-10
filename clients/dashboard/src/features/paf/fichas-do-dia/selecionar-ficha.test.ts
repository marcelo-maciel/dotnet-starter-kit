/// <reference types="node" />
// Run: node --test src/features/paf/fichas-do-dia/selecionar-ficha.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { chamarProxima, type AtendimentoNaTela } from "../guiche/guiche-fluxo.ts";
import { passosParaEscolhida, posicaoNaChamada, selecaoIndisponivel } from "./selecionar-ficha.ts";

const ESCOLHIDA = "escolhida-42";
const CHAMADO: AtendimentoNaTela = { atendimentoId: "a1", senha: "A7", estado: "Chamado" };
const EM_ATENDIMENTO: AtendimentoNaTela = { atendimentoId: "a2", senha: "A3", estado: "EmAtendimento" };

type Registro = { requisicoes: string[]; perguntas: string[] };

function api(resposta: boolean, falhaEm: string | null) {
  const registro: Registro = { requisicoes: [], perguntas: [] };
  const passos = passosParaEscolhida<AtendimentoNaTela>(ESCOLHIDA, {
    confirmar: (pergunta) => {
      registro.perguntas.push(pergunta);
      return resposta;
    },
    operar: async (atual, operacao) => {
      registro.requisicoes.push(`${operacao} ${atual.atendimentoId}`);
      if (falhaEm === operacao) throw new Error(`409 no ${operacao}`);
    },
    chamarEscolhida: async (atendimentoId) => {
      registro.requisicoes.push(`chamar ${atendimentoId}`);
      if (falhaEm === "chamar") throw new Error("A ficha já foi chamada por outro guichê.");
    },
  });
  return { registro, passos };
}

test("sem atendimento no guichê: chama a escolhida direto, sem pergunta", async () => {
  const { registro, passos } = api(true, null);
  assert.equal(await chamarProxima(null, passos), true);
  assert.deepEqual(registro.requisicoes, [`chamar ${ESCOLHIDA}`]);
  assert.deepEqual(registro.perguntas, []);
});

test("recusa da pergunta não dispara nenhuma requisição", async () => {
  for (const atual of [CHAMADO, EM_ATENDIMENTO]) {
    const { registro, passos } = api(false, null);
    assert.equal(await chamarProxima(atual, passos), false);
    assert.deepEqual(registro.requisicoes, []);
    assert.equal(registro.perguntas.length, 1);
  }
});

test("Chamado: cancela por não comparecimento e só depois chama a escolhida", async () => {
  const { registro, passos } = api(true, null);
  await chamarProxima(CHAMADO, passos);
  assert.deepEqual(registro.requisicoes, ["cancelar a1", `chamar ${ESCOLHIDA}`]);
  assert.match(registro.perguntas[0] ?? "", /A7 foi chamada e não iniciada/);
});

test("EmAtendimento: encerra e só depois chama a escolhida", async () => {
  const { registro, passos } = api(true, null);
  await chamarProxima(EM_ATENDIMENTO, passos);
  assert.deepEqual(registro.requisicoes, ["encerrar a2", `chamar ${ESCOLHIDA}`]);
});

test("erro no encerrar não dispara o chamar", async () => {
  const { registro, passos } = api(true, "encerrar");
  await assert.rejects(chamarProxima(EM_ATENDIMENTO, passos), /409 no encerrar/);
  assert.deepEqual(registro.requisicoes, ["encerrar a2"]);
});

test("erro no cancelar não dispara o chamar", async () => {
  const { registro, passos } = api(true, "cancelar");
  await assert.rejects(chamarProxima(CHAMADO, passos), /409 no cancelar/);
  assert.deepEqual(registro.requisicoes, ["cancelar a1"]);
});

test("409 de outro guichê no chamar chega à tela como erro", async () => {
  const { registro, passos } = api(true, "chamar");
  await assert.rejects(chamarProxima(null, passos), /já foi chamada por outro guichê/);
  assert.deepEqual(registro.requisicoes, [`chamar ${ESCOLHIDA}`]);
});

test("só 403 e 409 escondem a seleção; outra falha aparece", () => {
  assert.equal(selecaoIndisponivel(403), true);
  assert.equal(selecaoIndisponivel(409), true);
  for (const status of [null, 400, 401, 404, 500, 503]) assert.equal(selecaoIndisponivel(status), false, String(status));
});

test("posição na chamada segue a ordem do servidor, a partir de 1", () => {
  const posicoes = posicaoNaChamada([{ atendimentoId: "x" }, { atendimentoId: "y" }, { atendimentoId: "z" }]);
  assert.deepEqual([...posicoes.entries()], [["x", 1], ["y", 2], ["z", 3]]);
  assert.equal(posicaoNaChamada([]).size, 0);
});
