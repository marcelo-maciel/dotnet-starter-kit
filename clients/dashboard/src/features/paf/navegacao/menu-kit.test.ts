/// <reference types="node" />
// Run: node --import ./src/features/paf/navegacao/resolver-alias.mjs --test src/features/paf/navegacao/menu-kit.test.ts
// The URL rule of the kit screens goes through the same gates the menu hides items with (nav-data.ts).
import { test } from "node:test";
import assert from "node:assert/strict";
import { sections, telaDoKitLiberada, topNavBottom, topNavTop, visibleSections } from "@/components/layout/nav-data";

test("o que o menu mostra para um conjunto de permissões é o que a URL libera, tela por tela do kit", () => {
  const itensDoKit = [...topNavTop, ...topNavBottom, ...sections.flatMap((s) => s.items)].filter((i) => !i.to.startsWith("/paf/"));
  const conjuntos = [[], ["Permissions.Users.Update"], ["Permissions.AuditTrails.View"], ["Permissions.Sessions.ViewAll"]];
  for (const permissoes of conjuntos) {
    const noMenu = new Set([
      ...topNavTop.map((i) => i.to),
      ...topNavBottom.map((i) => i.to),
      ...visibleSections(permissoes).flatMap((s) => s.items.map((i) => i.to)),
    ]);
    for (const item of itensDoKit) {
      // Top-level items are ungated; for sectioned ones the menu is the source of truth.
      const esperado = noMenu.has(item.to);
      assert.equal(telaDoKitLiberada(item.to, permissoes), esperado, `${item.to} com ${JSON.stringify(permissoes)}`);
    }
  }
});

test("auditoria e sessões pedem a permissão do menu; saúde, atividade e visão geral são de todos", () => {
  assert.equal(telaDoKitLiberada("/system/audits", []), false);
  assert.equal(telaDoKitLiberada("/system/audits", ["Permissions.AuditTrails.View"]), true);
  assert.equal(telaDoKitLiberada("/system/sessions", ["Permissions.AuditTrails.View"]), false);
  assert.equal(telaDoKitLiberada("/system/sessions", ["Permissions.Sessions.ViewAll"]), true);
  for (const rota of ["/system/health", "/activity", "/overview", "/settings"]) assert.equal(telaDoKitLiberada(rota, []), true, rota);
});

test("barra final e subcaminho valem como a tela; caminho fora do menu não é barrado", () => {
  assert.equal(telaDoKitLiberada("/system/audits/", []), false);
  assert.equal(telaDoKitLiberada("/system/audits/a-1", []), false);
  assert.equal(telaDoKitLiberada("/system/audits-extra", []), true);
  assert.equal(telaDoKitLiberada("/rota-que-nao-existe", []), true);
});
