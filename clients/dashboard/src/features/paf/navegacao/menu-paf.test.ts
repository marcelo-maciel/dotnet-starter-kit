/// <reference types="node" />
// Run: node --import ./src/features/paf/navegacao/resolver-alias.mjs --test src/features/paf/navegacao/menu-paf.test.ts
// Goes through the kit's visibleSections and findSectionForPath with the PAF section as nav-data.ts lists it.
import { test } from "node:test";
import assert from "node:assert/strict";
import { findSectionForPath, visibleSections } from "@/components/layout/nav-data";

const GUICHE = "Permissions.PAF.Guiches.Selecionar";

function itensPaf(permissoes: string[]): string[] {
  return visibleSections(permissoes).find((s) => s.id === "paf")?.items.map((i) => i.to) ?? [];
}

test("quem seleciona guichê vê a entrada do guichê", () => {
  assert.deepEqual(itensPaf([GUICHE, "Permissions.PAF.Atendimentos.Operar"]), ["/paf/guiche"]);
});

test("sem a permissão da listagem a seção some inteira", () => {
  assert.deepEqual(itensPaf([]), []);
  assert.equal(visibleSections(["Permissions.Users.Update"]).some((s) => s.id === "paf"), false);
  // Near-miss names never pass: the gate is an exact match of the server constant.
  assert.deepEqual(itensPaf(["Permissions.PAF.Guiches", "permissions.paf.guiches.selecionar"]), []);
});

test("a seção do PAF não esconde nem mostra seção do kit por engano", () => {
  const kit = visibleSections(["Permissions.Users.Update"]).map((s) => s.id);
  const comPaf = visibleSections(["Permissions.Users.Update", GUICHE]).map((s) => s.id);
  assert.deepEqual(comPaf.filter((id) => id !== "paf"), kit);
});

test("cada cadastro aparece só com a permissão da sua listagem", () => {
  assert.deepEqual(itensPaf(["Permissions.PAF.Unidades.View"]), ["/paf/unidades"]);
  assert.deepEqual(itensPaf(["Permissions.PAF.Filas.View", "Permissions.PAF.Estacoes.View"]), ["/paf/filas", "/paf/estacoes"]);
  assert.deepEqual(itensPaf(["Permissions.PAF.Filas.Create", "Permissions.PAF.Estacoes.Update"]), []);
  assert.deepEqual(itensPaf(["Permissions.PAF.Usuarios.View"]), ["/paf/usuarios"]);
  // Managing without listing shows nothing: the page lists first (C12).
  assert.deepEqual(itensPaf(["Permissions.PAF.Usuarios.Manage", "Permissions.PAF.UsuariosUnidade.Update"]), []);
});

test("consultar ficha aparece só com a permissão de ver fichas do dia", () => {
  // F06 and F14 share FichasDoDia.View, so that permission opens both screens.
  assert.deepEqual(itensPaf(["Permissions.PAF.FichasDoDia.View"]), ["/paf/fichas-do-dia", "/paf/consultar-ficha", "/paf/ficha-de-espera"]);
  assert.deepEqual(itensPaf(["Permissions.PAF.FichasDoDia.Cancelar", "Permissions.PAF.FichasDoDia.Reabilitar"]), []);
  assert.deepEqual(itensPaf(["Permissions.PAF.Prioridades.View", "Permissions.PAF.Prioridades.Update"]), ["/paf/prioridades"]);
});

test("rotas do PAF abrem a seção do PAF no menu", () => {
  assert.equal(findSectionForPath("/paf/guiche"), "paf");
  assert.equal(findSectionForPath("/paf/unidades"), "paf");
  assert.equal(findSectionForPath("/paf/filas"), "paf");
  assert.equal(findSectionForPath("/paf/estacoes"), "paf");
  assert.equal(findSectionForPath("/paf/fichas-do-dia"), "paf");
  assert.equal(findSectionForPath("/paf/consultar-ficha"), "paf");
  assert.equal(findSectionForPath("/paf/ficha-de-espera"), "paf");
  assert.equal(findSectionForPath("/paf/usuarios"), "paf");
  assert.equal(findSectionForPath("/paf/prioridades"), "paf");
  assert.equal(findSectionForPath("/identity/users"), "identity");
});

test("fichas do dia aparece só com FichasDoDia.View, não com cancelar ou reabilitar", () => {
  // F06 and F14 share FichasDoDia.View, so that permission opens both screens.
  assert.deepEqual(itensPaf(["Permissions.PAF.FichasDoDia.View"]), ["/paf/fichas-do-dia", "/paf/consultar-ficha", "/paf/ficha-de-espera"]);
  assert.deepEqual(itensPaf(["Permissions.PAF.FichasDoDia.Cancelar", "Permissions.PAF.FichasDoDia.Reabilitar"]), []);
});

test("fichas em atendimento aparece só com FichasEmAtendimento.View (F07)", () => {
  assert.deepEqual(itensPaf(["Permissions.PAF.FichasEmAtendimento.View"]), ["/paf/fichas-em-atendimento"]);
  assert.deepEqual(itensPaf(["Permissions.PAF.FichasDoDia.Cancelar"]), []);
  assert.equal(findSectionForPath("/paf/fichas-em-atendimento"), "paf");
});

test("parâmetros (C10) aparecem só com Parametros.View e abrem a seção do PAF", () => {
  assert.deepEqual(itensPaf(["Permissions.PAF.Parametros.View"]), ["/paf/parametros"]);
  assert.deepEqual(itensPaf(["Permissions.PAF.Parametros.UpdateUnidade"]), []);
  assert.equal(findSectionForPath("/paf/parametros"), "paf");
});

test("estatística (E5) aparece só com Estatisticas.View e abre a seção do PAF", () => {
  assert.deepEqual(itensPaf(["Permissions.PAF.Estatisticas.View"]), ["/paf/estatistica"]);
  // ViewPorAtendente only widens type 4; without View the endpoint is a 403, so no menu entry.
  assert.deepEqual(itensPaf(["Permissions.PAF.Estatisticas.ViewPorAtendente"]), []);
  assert.equal(findSectionForPath("/paf/estatistica"), "paf");
});
