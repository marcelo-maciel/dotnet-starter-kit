/// <reference types="node" />
// Run: node --test src/features/paf/sessao/logout-unico.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import { umLogoutPorVez } from "./logout-unico.ts";

function sairControlado(): { sair: () => Promise<void>; chamadas: () => number; concluir: () => void } {
  let chamadas = 0;
  let concluir = () => {};
  const sair = () => {
    chamadas += 1;
    return new Promise<void>((resolver) => {
      concluir = resolver;
    });
  };
  return { sair, chamadas: () => chamadas, concluir: () => concluir() };
}

test("chamadas simultâneas na mesma aba disparam um único logout", () => {
  const { sair, chamadas } = sairControlado();
  const logout = umLogoutPorVez(sair);

  logout();
  logout();
  logout();

  assert.equal(chamadas(), 1);
});

test("depois de concluído, um novo logout volta a sair", async () => {
  const { sair, chamadas, concluir } = sairControlado();
  const logout = umLogoutPorVez(sair);

  logout();
  concluir();
  await new Promise((resolver) => setImmediate(resolver));
  logout();

  assert.equal(chamadas(), 2);
});

test("cada aba tem a própria guarda: o logout de uma não bloqueia o aviso na outra", () => {
  const abaA = sairControlado();
  const abaB = sairControlado();
  const logoutA = umLogoutPorVez(abaA.sair);
  const logoutB = umLogoutPorVez(abaB.sair);

  logoutA();
  logoutB();

  assert.equal(abaA.chamadas(), 1);
  assert.equal(abaB.chamadas(), 1);
});

test("o logout local sem servidor (já resolvido) não prende a guarda", async () => {
  let chamadas = 0;
  const logout = umLogoutPorVez(() => {
    chamadas += 1;
    return Promise.resolve();
  });

  logout();
  await new Promise((resolver) => setImmediate(resolver));
  logout();

  assert.equal(chamadas, 2);
});
