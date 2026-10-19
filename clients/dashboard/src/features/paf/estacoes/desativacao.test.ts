/// <reference types="node" />
// Run: node --test src/features/paf/estacoes/desativacao.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  atendimentoCancelavel,
  avisoDoResultado,
  linhasDoImpacto,
  type CombinacaoDto,
  type ImpactoDesativacaoDto,
} from "./desativacao.ts";

const combinacao: CombinacaoDto = { id: "c1", filaId: "f1", fila: "Protocolo", prioridadeId: "p1", prioridade: "Normal", letra: "B" };
const chamada = { atendimentoId: "a1", fichaId: "f1", senha: "A12", estado: "Chamado", versao: 2 };

function impacto(parcial: Partial<ImpactoDesativacaoDto>): ImpactoDesativacaoDto {
  return { id: "e1", guicheOcupado: false, emCurso: [], combinacoesSemCobertura: [], ...parcial };
}

test("impacto: estação livre e coberta não gera linha", () => {
  assert.deepEqual(linhasDoImpacto(impacto({})), []);
  assert.equal(atendimentoCancelavel(impacto({})), null);
});

test("impacto: senha em curso aparece com o estado em português e é a cancelável", () => {
  const i = impacto({ guicheOcupado: true, emCurso: [chamada] });
  const linhas = linhasDoImpacto(i);
  assert.equal(linhas.length, 1, "com em curso, a linha de guichê ocupado não se repete");
  assert.match(linhas[0], /A senha A12 está chamada/);
  assert.equal(atendimentoCancelavel(i)?.atendimentoId, "a1");
});

test("impacto: guichê ocupado sem atendimento avisa a liberação", () => {
  assert.deepEqual(linhasDoImpacto(impacto({ guicheOcupado: true })), [
    "O guichê está em uso e será liberado; o atendente volta à escolha de guichê.",
  ]);
});

test("impacto: estado desconhecido passa como veio, combinações listadas com a letra", () => {
  const linhas = linhasDoImpacto(impacto({ emCurso: [{ ...chamada, estado: "Novo" }], combinacoesSemCobertura: [combinacao] }));
  assert.match(linhas[0], /está Novo/);
  assert.equal(linhas[1], "Ficam sem guichê e saem do totem: Protocolo/Normal (B).");
});

test("resultado: nada a dizer devolve null; em curso e cobertura viram um aviso", () => {
  assert.equal(avisoDoResultado({ id: "e1", combinacoesSemCobertura: [], emCurso: [], guicheLiberado: true }), null);
  const aviso = avisoDoResultado({
    id: "e1",
    combinacoesSemCobertura: [combinacao],
    emCurso: [{ ...chamada, estado: "EmAtendimento" }],
    guicheLiberado: false,
  });
  assert.equal(
    aviso,
    "A senha A12 segue em atendimento; o guichê é liberado quando ela terminar. Sem guichê depois desta alteração (saem do totem): Protocolo/Normal (B).",
  );
});
