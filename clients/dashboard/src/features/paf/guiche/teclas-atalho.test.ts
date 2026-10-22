/// <reference types="node" />
// Run: node --test src/features/paf/guiche/teclas-atalho.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { acaoDaTecla, acaoPermitida, type TeclaPressionada, type TeclasGuicheDto } from "./teclas-atalho.ts";

const PADRAO: TeclasGuicheDto = {
  chamarProxima: "F7",
  encaminhar: "F8",
  encerrar: "F4",
  encerrarEChamar: "F6",
  iniciar: "F3",
  chamarNovamente: "F2",
};

function tecla(key: string, extra: Partial<TeclaPressionada>): TeclaPressionada {
  return { key, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, repeat: false, ...extra };
}

test("cada tecla padrão do legado dispara a sua ação", () => {
  assert.equal(acaoDaTecla(PADRAO, tecla("F7", {})), "chamarProxima");
  assert.equal(acaoDaTecla(PADRAO, tecla("F8", {})), "encaminhar");
  assert.equal(acaoDaTecla(PADRAO, tecla("F4", {})), "encerrar");
  assert.equal(acaoDaTecla(PADRAO, tecla("F6", {})), "encerrarEChamar");
  assert.equal(acaoDaTecla(PADRAO, tecla("F3", {})), "iniciar");
  assert.equal(acaoDaTecla(PADRAO, tecla("F2", {})), "chamarNovamente");
});

test("tecla configurada pelo parâmetro substitui o padrão", () => {
  const teclas = { ...PADRAO, chamarProxima: "F9" };
  assert.equal(acaoDaTecla(teclas, tecla("F9", {})), "chamarProxima");
  assert.equal(acaoDaTecla(teclas, tecla("F7", {})), null);
});

test("tecla com modificador ou repetida não dispara", () => {
  assert.equal(acaoDaTecla(PADRAO, tecla("F4", { altKey: true })), null);
  assert.equal(acaoDaTecla(PADRAO, tecla("F7", { ctrlKey: true })), null);
  assert.equal(acaoDaTecla(PADRAO, tecla("F7", { shiftKey: true })), null);
  assert.equal(acaoDaTecla(PADRAO, tecla("F7", { metaKey: true })), null);
  assert.equal(acaoDaTecla(PADRAO, tecla("F7", { repeat: true })), null);
  assert.equal(acaoDaTecla(PADRAO, tecla("a", {})), null);
  assert.equal(acaoDaTecla(PADRAO, tecla("F12", {})), null);
});

test("a tecla só faz o que o botão faria no estado atual", () => {
  assert.equal(acaoPermitida("chamarProxima", null, false), true);
  assert.equal(acaoPermitida("chamarProxima", null, true), false);
  assert.equal(acaoPermitida("iniciar", "Chamado", false), true);
  assert.equal(acaoPermitida("iniciar", "EmAtendimento", false), false);
  assert.equal(acaoPermitida("chamarNovamente", null, false), false);
  assert.equal(acaoPermitida("encerrar", "EmAtendimento", false), true);
  assert.equal(acaoPermitida("encerrar", "Chamado", false), false);
  assert.equal(acaoPermitida("encaminhar", "EmAtendimento", false), true);
  assert.equal(acaoPermitida("encaminhar", "Chamado", false), false);
  assert.equal(acaoPermitida("encerrarEChamar", "Chamado", false), true);
});
