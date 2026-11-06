/// <reference types="node" />
// Run: node --test src/features/paf/fichas-do-dia/fichas-do-dia-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  acoesDoAtendimento,
  consultaDosFiltros,
  descricaoGuiche,
  formatarEspera,
  PERMISSAO_CANCELAR,
  PERMISSAO_REABILITAR,
  rotuloEstado,
} from "./fichas-do-dia-regras.ts";

const TODAS = [PERMISSAO_CANCELAR, PERMISSAO_REABILITAR];

test("sem filtro a consulta vai vazia e o servidor escolhe o dia", () => {
  assert.equal(consultaDosFiltros({ data: null, filaId: null, estado: null }), "");
  assert.equal(consultaDosFiltros({ data: "", filaId: "", estado: null }), "");
});

test("filtros entram codificados e só os preenchidos", () => {
  assert.equal(
    consultaDosFiltros({ data: "2026-10-18", filaId: "f1", estado: "EmAtendimento" }),
    "?data=2026-10-18&filaId=f1&estado=EmAtendimento",
  );
  assert.equal(consultaDosFiltros({ data: null, filaId: "a b&c", estado: null }), "?filaId=a+b%26c");
});

test("cancelar só nos estados que o servidor cancela, reabilitar só no cancelado", () => {
  for (const estado of ["Aguardando", "Chamado", "EmAtendimento"]) {
    assert.deepEqual(acoesDoAtendimento(estado, TODAS), { cancelar: true, reabilitar: false }, estado);
  }
  assert.deepEqual(acoesDoAtendimento("Cancelado", TODAS), { cancelar: false, reabilitar: true });
  assert.deepEqual(acoesDoAtendimento("Encerrado", TODAS), { cancelar: false, reabilitar: false });
  assert.deepEqual(acoesDoAtendimento("Desconhecido", TODAS), { cancelar: false, reabilitar: false });
});

test("sem a permissão o botão some, mesmo no estado certo", () => {
  assert.deepEqual(acoesDoAtendimento("Aguardando", [PERMISSAO_REABILITAR]), { cancelar: false, reabilitar: false });
  assert.deepEqual(acoesDoAtendimento("Cancelado", [PERMISSAO_CANCELAR]), { cancelar: false, reabilitar: false });
  // Near-miss names never pass: the gate is the exact server constant.
  assert.deepEqual(acoesDoAtendimento("Cancelado", ["permissions.paf.fichasdodia.reabilitar"]), { cancelar: false, reabilitar: false });
});

test("espera em m:ss e h:mm:ss, nunca negativa nem fracionada", () => {
  assert.equal(formatarEspera(0), "0:00");
  assert.equal(formatarEspera(59.9), "0:59");
  assert.equal(formatarEspera(300), "5:00");
  assert.equal(formatarEspera(3599), "59:59");
  assert.equal(formatarEspera(3600), "1:00:00");
  assert.equal(formatarEspera(90061), "25:01:01");
  assert.equal(formatarEspera(-5), "0:00");
});

test("guichê pelo número do cadastro, senão pelo nome da estação, senão traço", () => {
  assert.equal(descricaoGuiche({ guiche: "Guichê 1", numeroGuiche: 3, nomeExibicaoGuiche: "Caixa" }), "3 - Caixa");
  assert.equal(descricaoGuiche({ guiche: "Guichê 1", numeroGuiche: 3, nomeExibicaoGuiche: null }), "3");
  assert.equal(descricaoGuiche({ guiche: "Guichê 1", numeroGuiche: null, nomeExibicaoGuiche: null }), "Guichê 1");
  assert.equal(descricaoGuiche({ guiche: null, numeroGuiche: null, nomeExibicaoGuiche: null }), "—");
});

test("rótulo do estado legível e o desconhecido passa como veio", () => {
  assert.equal(rotuloEstado("EmAtendimento"), "Em atendimento");
  assert.equal(rotuloEstado("Novo"), "Novo");
});
