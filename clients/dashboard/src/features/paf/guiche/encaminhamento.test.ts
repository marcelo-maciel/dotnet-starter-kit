/// <reference types="node" />
// Run: node --test src/features/paf/guiche/encaminhamento.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  alternarFila,
  bloqueioConfirmacao,
  intersecaoPrioridades,
  MENSAGEM_SEM_FILAS,
  MENSAGEM_SEM_PRIORIDADE_COMUM,
  prioridadeSelecionada,
  type DestinoEncaminhamento,
} from "./encaminhamento.ts";

const NORMAL = { prioridadeId: "p-normal", nome: "Normal" };
const PREF = { prioridadeId: "p-pref", nome: "Preferencial" };
const IDOSO = { prioridadeId: "p-80", nome: "80+" };

const DESTINOS: DestinoEncaminhamento[] = [
  { filaId: "caixa", nome: "Caixa", foraDoHorario: false, prioridades: [PREF, NORMAL, IDOSO] },
  { filaId: "credito", nome: "Crédito", foraDoHorario: true, prioridades: [NORMAL, PREF] },
  { filaId: "seguro", nome: "Seguro", foraDoHorario: false, prioridades: [IDOSO] },
  { filaId: "vazia", nome: "Sem cobertura", foraDoHorario: false, prioridades: [] },
];

const ids = (lista: { prioridadeId: string }[]) => lista.map((p) => p.prioridadeId);

test("nenhuma fila marcada: interseção vazia e confirmar bloqueado com a mensagem do RF17", () => {
  const intersecao = intersecaoPrioridades(DESTINOS, []);
  assert.deepEqual(intersecao, []);
  assert.equal(bloqueioConfirmacao([], intersecao, ""), MENSAGEM_SEM_FILAS);
});

test("uma fila marcada: as prioridades cobertas dela, na ordem dela", () => {
  assert.deepEqual(ids(intersecaoPrioridades(DESTINOS, ["caixa"])), ["p-pref", "p-normal", "p-80"]);
});

test("duas filas: só as comuns, na ordem da primeira fila da lista, seja qual for a ordem do clique", () => {
  assert.deepEqual(ids(intersecaoPrioridades(DESTINOS, ["caixa", "credito"])), ["p-pref", "p-normal"]);
  assert.deepEqual(ids(intersecaoPrioridades(DESTINOS, ["credito", "caixa"])), ["p-pref", "p-normal"]);
});

test("filas sem prioridade em comum: interseção vazia e confirmar bloqueado com o aviso", () => {
  const marcadas = ["credito", "seguro"];
  const intersecao = intersecaoPrioridades(DESTINOS, marcadas);
  assert.deepEqual(intersecao, []);
  assert.equal(prioridadeSelecionada(intersecao, null, "p-normal"), "");
  assert.equal(bloqueioConfirmacao(marcadas, intersecao, ""), MENSAGEM_SEM_PRIORIDADE_COMUM);
});

test("fila sem nenhuma prioridade coberta zera a interseção", () => {
  assert.deepEqual(intersecaoPrioridades(DESTINOS, ["caixa", "vazia"]), []);
});

test("original dentro da interseção vem selecionada e confirmar libera", () => {
  const marcadas = ["caixa", "credito"];
  const intersecao = intersecaoPrioridades(DESTINOS, marcadas);
  const escolhida = prioridadeSelecionada(intersecao, null, "p-normal");
  assert.equal(escolhida, "p-normal");
  assert.equal(bloqueioConfirmacao(marcadas, intersecao, escolhida), null);
});

test("original fora da interseção: nada selecionado e confirmar pede a prioridade", () => {
  const marcadas = ["seguro"];
  const intersecao = intersecaoPrioridades(DESTINOS, marcadas);
  const escolhida = prioridadeSelecionada(intersecao, null, "p-normal");
  assert.equal(escolhida, "");
  assert.equal(bloqueioConfirmacao(marcadas, intersecao, escolhida), "Selecione a prioridade.");
});

test("recálculo ao marcar: a escolha do operador que saiu da interseção cai para a original", () => {
  const soCaixa = intersecaoPrioridades(DESTINOS, ["caixa"]);
  assert.equal(prioridadeSelecionada(soCaixa, "p-80", "p-normal"), "p-80");
  const comCredito = intersecaoPrioridades(DESTINOS, alternarFila(["caixa"], "credito"));
  assert.equal(prioridadeSelecionada(comCredito, "p-80", "p-normal"), "p-normal");
});

test("recálculo ao desmarcar: a interseção volta a crescer e a escolha do operador volta a valer", () => {
  let marcadas = ["caixa", "seguro"];
  assert.deepEqual(ids(intersecaoPrioridades(DESTINOS, marcadas)), ["p-80"]);
  assert.equal(prioridadeSelecionada(intersecaoPrioridades(DESTINOS, marcadas), "p-pref", "p-normal"), "");
  marcadas = alternarFila(marcadas, "seguro");
  assert.deepEqual(marcadas, ["caixa"]);
  const intersecao = intersecaoPrioridades(DESTINOS, marcadas);
  assert.deepEqual(ids(intersecao), ["p-pref", "p-normal", "p-80"]);
  assert.equal(prioridadeSelecionada(intersecao, "p-pref", "p-normal"), "p-pref");
});

test("desmarcar a última fila volta ao bloqueio de nenhuma fila", () => {
  const marcadas = alternarFila(["caixa"], "caixa");
  assert.deepEqual(marcadas, []);
  assert.equal(bloqueioConfirmacao(marcadas, intersecaoPrioridades(DESTINOS, marcadas), "p-normal"), MENSAGEM_SEM_FILAS);
});

test("fila marcada que sumiu da lista (desativada entre leituras) não entra na interseção", () => {
  assert.deepEqual(ids(intersecaoPrioridades(DESTINOS, ["caixa", "removida"])), ["p-pref", "p-normal", "p-80"]);
});
