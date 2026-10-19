/// <reference types="node" />
// Run: node --test src/features/paf/guiche/emissao-guiche-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  botoesDaFila,
  chaveDoClique,
  CODIGO_EM_CURSO,
  desfechoDoErro,
  formatarUltimaChamada,
  pendenteApos,
  podeEmitirNoGuiche,
  situacaoDaFila,
  type FilaEmissaoGuicheDto,
} from "./emissao-guiche-regras.ts";

const NORMAL = { prioridadeId: "p-n", nome: "Normal", letra: "A" };
const fila = (parcial: Partial<FilaEmissaoGuicheDto>): FilaEmissaoGuicheDto => ({
  filaId: "f-1",
  nomeExibicao: "Caixa",
  ativa: true,
  foraDoHorario: false,
  emissivel: true,
  ultimaChamadaEm: null,
  prioridades: [NORMAL],
  ...parcial,
});

test("só quem tem Fichas.Emitir vê o painel", () => {
  assert.equal(podeEmitirNoGuiche(["Permissions.PAF.Fichas.Emitir"]), true);
  assert.equal(podeEmitirNoGuiche(["Permissions.PAF.FichasDoDia.View"]), false);
  assert.equal(podeEmitirNoGuiche([]), false);
});

test("fila fora do horário, inativa ou indisponível não mostra botão e diz por quê", () => {
  assert.deepEqual(botoesDaFila(fila({})), [NORMAL]);
  assert.equal(situacaoDaFila(fila({})), "");

  const fora = fila({ foraDoHorario: true, emissivel: false });
  assert.deepEqual(botoesDaFila(fora), []);
  assert.equal(situacaoDaFila(fora), "Fora do horário");

  const inativa = fila({ ativa: false, emissivel: false });
  assert.deepEqual(botoesDaFila(inativa), []);
  assert.equal(situacaoDaFila(inativa), "Inativa");

  // Server says no even with the fila active and in window (unidade or station inactive): no button.
  const unidadeInativa = fila({ emissivel: false });
  assert.deepEqual(botoesDaFila(unidadeInativa), []);
  assert.equal(situacaoDaFila(unidadeInativa), "Indisponível");

  assert.equal(situacaoDaFila(fila({ prioridades: [] })), "Sem guichê que atenda");
});

test("última chamada em São Paulo; nunca chamada ou inválida mostra traço", () => {
  assert.equal(formatarUltimaChamada(null), "—");
  assert.equal(formatarUltimaChamada("lixo"), "—");
  // 02:30 UTC is still the previous day in São Paulo (UTC-3).
  assert.equal(formatarUltimaChamada("2026-10-20T02:30:00+00:00"), "19/10/2026, 23:30");
  assert.equal(formatarUltimaChamada("2026-10-19T09:05:00-03:00"), "19/10/2026, 09:05");
});

test("só a falha que pode ter chegado ao servidor mantém a chave", () => {
  assert.equal(desfechoDoErro(null, null), "transitoria");
  assert.equal(desfechoDoErro(503, null), "transitoria");
  assert.equal(desfechoDoErro(408, null), "transitoria");
  assert.equal(desfechoDoErro(429, null), "transitoria");
  assert.equal(desfechoDoErro(409, CODIGO_EM_CURSO), "emCurso");
  // A coverage or window 409 is final: resending the key cannot change it.
  assert.equal(desfechoDoErro(409, null), "definitiva");
  assert.equal(desfechoDoErro(409, "Outro.Codigo"), "definitiva");
  assert.equal(desfechoDoErro(404, null), "definitiva");
  assert.equal(desfechoDoErro(400, null), "definitiva");
});

test("o mesmo botão depois de falha transitória reenvia a mesma chave; outro botão gera chave nova", () => {
  let n = 0;
  const nova = () => `k${++n}`;

  const primeira = chaveDoClique(null, "f-1", "p-n", nova);
  assert.equal(primeira.chave, "k1");

  const pendente = pendenteApos(primeira, "transitoria");
  assert.deepEqual(chaveDoClique(pendente, "f-1", "p-n", nova), primeira);
  assert.equal(chaveDoClique(pendente, "f-1", "p-p", nova).chave, "k2", "outra prioridade nunca reaproveita a chave");
  assert.equal(chaveDoClique(pendente, "f-2", "p-n", nova).chave, "k3", "outra fila nunca reaproveita a chave");

  assert.deepEqual(pendenteApos(primeira, "emCurso"), primeira);
  assert.equal(pendenteApos(primeira, "sucesso"), null);
  assert.equal(pendenteApos(primeira, "definitiva"), null);
  assert.equal(chaveDoClique(pendenteApos(primeira, "sucesso"), "f-1", "p-n", nova).chave, "k4", "depois do sucesso a mesma escolha emite ficha nova");
});
