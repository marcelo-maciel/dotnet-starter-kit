/// <reference types="node" />
// Run: node --test src/features/paf/painel/agenda-midia.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  aposTermino,
  diaVirou,
  montarAgenda,
  programacaoNoAr,
  proximaDaLista,
  proximaParaPrecarga,
  proximaReleituraLocalMs,
  proximaTrocaLocalMs,
  QUARENTENA_FALHA_MS,
  reavaliar,
  type ItemMidiaPainelDto,
  type MidiasPainelDto,
  type ProgramacaoPainelDto,
} from "./agenda-midia.ts";

const SEM_FALHAS = new Map<string, number>();
// 19/10/2026 09:59:00 in São Paulo, as the server sends it (7 fraction digits, -03:00).
const AGORA = "2026-10-19T09:59:00.0000000-03:00";
const AGORA_MS = Date.parse(AGORA);
const MEIA_NOITE_MS = Date.parse("2026-10-19T00:00:00-03:00");
const EXPIRA = "2026-10-19T11:30:00-03:00";

function item(id: string, tipo: ItemMidiaPainelDto["tipo"] = "Video"): ItemMidiaPainelDto {
  return { itemId: `i-${id}`, midiaId: `m-${id}`, tipo, duracaoSegundos: 30, url: `https://s3/${id}`, expiraEm: EXPIRA };
}

function programacao(id: string, inicio: string, fim: string, duracao = 60): ProgramacaoPainelDto {
  const seg = (h: string) => {
    const [hh, mm, ss] = h.split(":").map(Number);
    return hh * 3600 + mm * 60 + (ss ?? 0);
  };
  return {
    programacaoId: `p-${id}`,
    midiaId: `m-${id}`,
    tipo: "Video",
    duracaoSegundos: duracao,
    repeticoes: (seg(fim) - seg(inicio)) / duracao,
    inicioSegundos: seg(inicio),
    fimSegundos: seg(fim),
    url: `https://s3/${id}`,
    expiraEm: EXPIRA,
  };
}

function dto(lista: ItemMidiaPainelDto[], programacoes: ProgramacaoPainelDto[]): MidiasPainelDto {
  return { agora: AGORA, lista, programacoes };
}

/** The local instant matching a São Paulo wall-clock time today, given the local clock is `adiantadoMs` ahead. */
function local(hora: string, adiantadoMs: number): number {
  return Date.parse(`2026-10-19T${hora}-03:00`) + adiantadoMs;
}

test("montarAgenda: midnight from the São Paulo wall clock, offset from the request's departure", () => {
  // The server read its clock as the request arrived; the 1.4 s it then spent signing URLs must not move the offset.
  const agenda = montarAgenda(dto([], []), AGORA_MS - 400);
  assert.equal(agenda.meiaNoiteMs, MEIA_NOITE_MS);
  assert.equal(agenda.deslocamentoMs, 400);
});

test("montarAgenda: an unreadable instant throws instead of guessing a day", () => {
  assert.throws(() => montarAgenda({ agora: "ontem", lista: [], programacoes: [] }, 0));
  assert.throws(() => montarAgenda({ agora: "2026-10-19", lista: [], programacoes: [] }, 0));
});

test("switches exactly at 10:00:00 and back at 10:10:00", () => {
  const agenda = montarAgenda(dto([item("a")], [programacao("x", "10:00:00", "10:10:00")]), AGORA_MS);
  assert.equal(programacaoNoAr(agenda, local("09:59:59.999", 0), SEM_FALHAS), null);
  assert.equal(programacaoNoAr(agenda, local("10:00:00", 0), SEM_FALHAS)?.programacao.programacaoId, "p-x");
  assert.equal(programacaoNoAr(agenda, local("10:09:59.999", 0), SEM_FALHAS)?.programacao.programacaoId, "p-x");
  assert.equal(programacaoNoAr(agenda, local("10:10:00", 0), SEM_FALHAS), null);
  assert.equal(proximaTrocaLocalMs(agenda, local("09:59:00", 0)), local("10:00:00", 0));
  assert.equal(proximaTrocaLocalMs(agenda, local("10:00:00", 0)), local("10:10:00", 0));
  // After the last window the next change is the São Paulo midnight (the day turn re-read).
  assert.equal(proximaTrocaLocalMs(agenda, local("10:10:00", 0)), MEIA_NOITE_MS + 86_400_000);
});

test("a local clock 5 minutes ahead still switches on the server's 10:00:00", () => {
  const adiantado = 5 * 60_000;
  // The request left and came back at local instants 5 min ahead of the server's `agora`.
  const agenda = montarAgenda(dto([], [programacao("x", "10:00:00", "10:10:00")]), AGORA_MS + adiantado);
  assert.equal(agenda.deslocamentoMs, -adiantado);
  assert.equal(programacaoNoAr(agenda, local("09:59:59", adiantado), SEM_FALHAS), null);
  assert.equal(programacaoNoAr(agenda, local("10:00:00", adiantado), SEM_FALHAS)?.programacao.programacaoId, "p-x");
  assert.equal(proximaTrocaLocalMs(agenda, local("09:59:00", adiantado)), local("10:00:00", adiantado));
});

test("a clock 5 minutes behind is corrected the same way", () => {
  const atrasado = -5 * 60_000;
  const agenda = montarAgenda(dto([], [programacao("x", "10:00:00", "10:10:00")]), AGORA_MS + atrasado);
  assert.equal(programacaoNoAr(agenda, local("09:59:59", atrasado), SEM_FALHAS), null);
  assert.equal(programacaoNoAr(agenda, local("10:00:00", atrasado), SEM_FALHAS)?.programacao.programacaoId, "p-x");
});

test("empty answer: nothing on air, nothing to preload, no URL re-read", () => {
  const agenda = montarAgenda(dto([], []), AGORA_MS);
  assert.equal(reavaliar(agenda, null, AGORA_MS, SEM_FALHAS), null);
  assert.equal(proximaParaPrecarga(agenda, null, AGORA_MS, SEM_FALHAS), null);
  assert.equal(proximaReleituraLocalMs(agenda, AGORA_MS), null);
  assert.equal(reavaliar(null, null, AGORA_MS, SEM_FALHAS), null);
});

test("D13: overlapping windows play the first in the server's precedence order", () => {
  // The server lists the latest start first; the screen trusts that order.
  const tarde = programacao("tarde", "10:05:00", "10:08:00");
  const cedo = programacao("cedo", "10:00:00", "10:20:00");
  const agenda = montarAgenda(dto([], [tarde, cedo]), AGORA_MS);
  assert.equal(programacaoNoAr(agenda, local("10:04:59", 0), SEM_FALHAS)?.programacao.programacaoId, "p-cedo");
  assert.equal(programacaoNoAr(agenda, local("10:05:00", 0), SEM_FALHAS)?.programacao.programacaoId, "p-tarde");
  assert.equal(programacaoNoAr(agenda, local("10:08:00", 0), SEM_FALHAS)?.programacao.programacaoId, "p-cedo");
  assert.equal(proximaTrocaLocalMs(agenda, local("10:01:00", 0)), local("10:05:00", 0));
});

test("D12: the loop plays outside the window and the programação covers it inside", () => {
  const agenda = montarAgenda(dto([item("a"), item("b")], [programacao("x", "10:00:00", "10:10:00")]), AGORA_MS);
  const antes = reavaliar(agenda, null, local("09:59:30", 0), SEM_FALHAS);
  assert.equal(antes?.origem, "lista");
  assert.equal(antes?.chave, "i-a");
  const durante = reavaliar(agenda, antes, local("10:00:00", 0), SEM_FALHAS);
  assert.equal(durante?.origem, "programacao");
  assert.equal(durante?.chave, "p-x");
  assert.equal(durante?.inicioNaMidiaSegundos, 0);
  assert.equal(reavaliar(agenda, durante, local("10:03:00", 0), SEM_FALHAS), durante, "same programação keeps playing");
  const depois = reavaliar(agenda, durante, local("10:10:00", 0), SEM_FALHAS);
  assert.equal(depois?.origem, "lista");
  assert.notEqual(depois?.id, durante?.id);
});

test("a programação joined mid-window starts at its offset in the file; an image at zero", () => {
  const agenda = montarAgenda(dto([], [programacao("x", "10:00:00", "10:10:00", 60)]), AGORA_MS);
  assert.equal(reavaliar(agenda, null, local("10:02:15", 0), SEM_FALHAS)?.inicioNaMidiaSegundos, 15);
  const imagem = { ...programacao("y", "10:00:00", "10:10:00", 60), tipo: "Imagem" as const };
  const agendaImagem = montarAgenda(dto([], [imagem]), AGORA_MS);
  assert.equal(reavaliar(agendaImagem, null, local("10:02:15", 0), SEM_FALHAS)?.inicioNaMidiaSegundos, 0);
});

test("the loop wraps and a repeated mídia keeps its own rows", () => {
  const agenda = montarAgenda(dto([item("a"), item("b"), { ...item("a"), itemId: "i-a2" }], []), AGORA_MS);
  let atual = reavaliar(agenda, null, AGORA_MS, SEM_FALHAS);
  const vistos: string[] = [];
  for (let i = 0; i < 4 && atual !== null; i++) {
    vistos.push(atual.chave);
    atual = aposTermino(agenda, atual, AGORA_MS, SEM_FALHAS);
  }
  assert.deepEqual(vistos, ["i-a", "i-b", "i-a2", "i-a"]);
});

test("a failed URL is skipped, and the loop with every URL failed plays nothing until the quarantine ends", () => {
  const agenda = montarAgenda(dto([item("a"), item("b")], []), AGORA_MS);
  const atual = reavaliar(agenda, null, AGORA_MS, SEM_FALHAS);
  const falhas = new Map([["https://s3/a", AGORA_MS]]);
  assert.equal(aposTermino(agenda, atual!, AGORA_MS, falhas)?.chave, "i-b");
  falhas.set("https://s3/b", AGORA_MS);
  assert.equal(aposTermino(agenda, atual!, AGORA_MS, falhas), null);
  assert.equal(proximaDaLista(agenda.lista, -1, falhas, AGORA_MS + QUARENTENA_FALHA_MS), 0);
});

test("a programação whose URL failed falls back to the loop", () => {
  const agenda = montarAgenda(dto([item("a")], [programacao("x", "10:00:00", "10:10:00")]), AGORA_MS);
  const agora = local("10:01:00", 0);
  const falhas = new Map([["https://s3/x", agora]]);
  assert.equal(reavaliar(agenda, null, agora, falhas)?.chave, "i-a");
});

test("a programação that ends restarts from its offset while the window lasts", () => {
  const agenda = montarAgenda(dto([item("a")], [programacao("x", "10:00:00", "10:10:00", 60)]), AGORA_MS);
  const primeira = reavaliar(agenda, null, local("10:00:00", 0), SEM_FALHAS)!;
  const segunda = aposTermino(agenda, primeira, local("10:01:00", 0), SEM_FALHAS);
  assert.equal(segunda?.chave, "p-x");
  assert.equal(segunda?.inicioNaMidiaSegundos, 0);
  assert.equal(segunda?.id, primeira.id + 1);
});

test("a disabled mídia leaves the re-read list and stops at once; a re-read of the same list keeps the item", () => {
  const agenda = montarAgenda(dto([item("a"), item("b")], []), AGORA_MS);
  const tocandoB = aposTermino(agenda, reavaliar(agenda, null, AGORA_MS, SEM_FALHAS)!, AGORA_MS, SEM_FALHAS)!;
  assert.equal(tocandoB.chave, "i-b");
  // New signatures, same rows: nothing restarts.
  const relida = montarAgenda(dto([{ ...item("a"), url: "https://s3/a?v2" }, { ...item("b"), url: "https://s3/b?v2" }], []), AGORA_MS);
  assert.equal(reavaliar(relida, tocandoB, AGORA_MS, SEM_FALHAS), tocandoB);
  // A replaced list has new row ids: the mídia still in it keeps playing.
  const substituida = montarAgenda(dto([{ ...item("b"), itemId: "novo-b" }], []), AGORA_MS);
  assert.equal(reavaliar(substituida, tocandoB, AGORA_MS, SEM_FALHAS)?.id, tocandoB.id);
  // b disabled: it leaves the list and the screen moves on.
  const semB = montarAgenda(dto([item("a")], []), AGORA_MS);
  const depois = reavaliar(semB, tocandoB, AGORA_MS, SEM_FALHAS);
  assert.equal(depois?.chave, "i-a");
  assert.notEqual(depois?.id, tocandoB.id);
  // Everything disabled: no media.
  assert.equal(reavaliar(montarAgenda(dto([], []), AGORA_MS), tocandoB, AGORA_MS, SEM_FALHAS), null);
});

test("day turn: stale programações stop at São Paulo midnight and the loop goes on", () => {
  const agenda = montarAgenda(dto([item("a")], [programacao("noite", "23:00:00", "24:00:00")]), AGORA_MS);
  const quase = Date.parse("2026-10-19T23:59:59.999-03:00");
  const meiaNoite = Date.parse("2026-10-20T00:00:00-03:00");
  assert.equal(diaVirou(agenda, quase), false);
  assert.equal(programacaoNoAr(agenda, quase, SEM_FALHAS)?.programacao.programacaoId, "p-noite");
  assert.equal(diaVirou(agenda, meiaNoite), true);
  assert.equal(programacaoNoAr(agenda, meiaNoite, SEM_FALHAS), null);
  assert.equal(reavaliar(agenda, null, meiaNoite, SEM_FALHAS)?.origem, "lista");
  assert.equal(proximaTrocaLocalMs(agenda, Date.parse("2026-10-19T23:30:00-03:00")), meiaNoite);
});

test("URL re-read: 30 s before a URL stops covering a whole exhibition, never sooner than 5 s from now", () => {
  // Loop item of 30 s expiring 11:00 → due 10:59:30; programação of 60 s × 10 expiring 10:30 → due 10:20.
  const agenda = montarAgenda(
    dto([{ ...item("a"), expiraEm: "2026-10-19T11:00:00-03:00" }], [{ ...programacao("x", "10:00:00", "10:10:00"), expiraEm: "2026-10-19T10:30:00-03:00" }]),
    AGORA_MS,
  );
  assert.equal(proximaReleituraLocalMs(agenda, AGORA_MS), Date.parse("2026-10-19T10:19:30-03:00"));
  assert.equal(proximaReleituraLocalMs(agenda, Date.parse("2026-10-19T10:19:50-03:00")), Date.parse("2026-10-19T10:19:55-03:00"));
  // A local clock 5 min ahead moves the local instant by the same 5 min.
  const adiantada = montarAgenda(dto([{ ...item("a"), expiraEm: "2026-10-19T11:00:00-03:00" }], []), AGORA_MS + 300_000);
  assert.equal(proximaReleituraLocalMs(adiantada, AGORA_MS + 300_000), Date.parse("2026-10-19T10:59:00-03:00") + 300_000);
});

test("preload: only the next loop item, never the same file, never during a programação", () => {
  const agenda = montarAgenda(dto([item("a"), item("b")], [programacao("x", "10:00:00", "10:10:00")]), AGORA_MS);
  const tocandoA = reavaliar(agenda, null, AGORA_MS, SEM_FALHAS);
  assert.equal(proximaParaPrecarga(agenda, tocandoA, AGORA_MS, SEM_FALHAS)?.itemId, "i-b");
  const unica = montarAgenda(dto([item("a")], []), AGORA_MS);
  assert.equal(proximaParaPrecarga(unica, reavaliar(unica, null, AGORA_MS, SEM_FALHAS), AGORA_MS, SEM_FALHAS), null);
  const programada = reavaliar(agenda, null, local("10:00:00", 0), SEM_FALHAS);
  assert.equal(proximaParaPrecarga(agenda, programada, local("10:00:00", 0), SEM_FALHAS), null);
});

test("day turn: no clock switch is due on a stale agenda, so the screen re-reads once instead of re-arming at now + 1 ms", () => {
  const agenda = montarAgenda(dto([item("a")], [programacao("noite", "23:00:00", "24:00:00")]), AGORA_MS);
  assert.equal(proximaTrocaLocalMs(agenda, Date.parse("2026-10-20T00:00:00-03:00")), null);
  assert.equal(proximaTrocaLocalMs(agenda, Date.parse("2026-10-20T00:00:05-03:00")), null);
});

test("a programação whose file is shorter than its registered duration restarts from 0, not from the window offset", () => {
  // Registered 60 s, file of 10 s: at 10:00:10 the file ended; joining at the offset (10 s) would seek past its end.
  const agenda = montarAgenda(dto([item("a")], [programacao("x", "10:00:00", "10:10:00", 60)]), AGORA_MS);
  const primeira = reavaliar(agenda, null, local("10:00:00", 0), SEM_FALHAS)!;
  const depois = aposTermino(agenda, primeira, local("10:00:10", 0), SEM_FALHAS);
  assert.equal(depois?.chave, "p-x");
  assert.equal(depois?.inicioNaMidiaSegundos, 0);
  // Another programação joined after one ended still starts at its own offset in the window.
  const duas = montarAgenda(dto([], [programacao("x", "10:00:00", "10:05:00", 60), { ...programacao("y", "10:00:00", "10:10:00", 60), midiaId: "m-y" }]), AGORA_MS);
  const x = reavaliar(duas, null, local("10:04:30", 0), SEM_FALHAS)!;
  assert.equal(x.chave, "p-x");
  assert.equal(aposTermino(duas, x, local("10:05:20", 0), SEM_FALHAS)?.inicioNaMidiaSegundos, 20);
  // A loop item ending inside a window joins the programação at its offset too.
  const lista = reavaliar(agenda, null, local("09:59:50", 0), SEM_FALHAS)!;
  assert.equal(aposTermino(agenda, lista, local("10:00:15", 0), SEM_FALHAS)?.inicioNaMidiaSegundos, 15);
});
