/// <reference types="node" />
// Run: node --test src/features/paf/unidade-corrente.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { comAvisoDeUnidadeCorrente, recarregarAoReconectar, UNIDADE_CORRENTE_ALTERADA } from "./unidade-corrente.ts";

test("o aviso de unidade corrente recarrega o contexto uma vez por aviso", () => {
  let recargas = 0;
  const handlers = comAvisoDeUnidadeCorrente({}, () => {
    recargas += 1;
  });

  handlers[UNIDADE_CORRENTE_ALTERADA]({ eventoId: "e1", unidadeId: "u2" });
  handlers[UNIDADE_CORRENTE_ALTERADA]({ eventoId: "e2", unidadeId: "u3" });

  assert.equal(recargas, 2);
});

test("os handlers da tela continuam e não recarregam o contexto", () => {
  const recebidos: unknown[] = [];
  let recargas = 0;
  const handlers = comAvisoDeUnidadeCorrente({ SenhaChamada: (p) => recebidos.push(p) }, () => {
    recargas += 1;
  });

  handlers.SenhaChamada({ senha: "N001" });

  assert.deepEqual(recebidos, [{ senha: "N001" }]);
  assert.equal(recargas, 0);
  assert.deepEqual(Object.keys(handlers).sort(), ["SenhaChamada", UNIDADE_CORRENTE_ALTERADA]);
});

test("o nome do método é o do contrato do servidor", () => {
  assert.equal(UNIDADE_CORRENTE_ALTERADA, "UnidadeCorrenteAlterada");
});

test("a primeira conexão não recarrega; toda reconexão recarrega", () => {
  let recargas = 0;
  const aoConectar = recarregarAoReconectar(() => {
    recargas += 1;
  });

  aoConectar();
  assert.equal(recargas, 0);
  aoConectar();
  aoConectar();
  assert.equal(recargas, 2);
});
