// What the painel plays now (M08, RF21 amended, D12, D13), as pure functions over the `estacao/painel/midias` answer
// (M06) and the local clock. The server sends its own instant (`agora`, São Paulo offset): the screen keeps the offset
// to its local clock, so a kiosk whose clock drifted still switches on the server's second. The loop plays whenever
// it has a playable item; the first programação (server precedence order, D13) whose window holds the instant covers
// it. Nothing playable = no media at all and the calls take the whole screen.

export type TipoMidiaPainel = "Video" | "Imagem" | "Audio";

export type ItemMidiaPainelDto = {
  itemId: string;
  midiaId: string;
  tipo: TipoMidiaPainel;
  duracaoSegundos: number;
  url: string;
  expiraEm: string;
};

export type ProgramacaoPainelDto = {
  programacaoId: string;
  midiaId: string;
  tipo: TipoMidiaPainel;
  duracaoSegundos: number;
  repeticoes: number;
  inicioSegundos: number;
  fimSegundos: number;
  url: string;
  expiraEm: string;
};

export type MidiasPainelDto = {
  agora: string;
  lista: ItemMidiaPainelDto[];
  programacoes: ProgramacaoPainelDto[];
};

export type AgendaPainel = {
  /** Server clock minus local clock, in ms. */
  deslocamentoMs: number;
  /** São Paulo midnight of the day the answer is about, on the server clock. */
  meiaNoiteMs: number;
  lista: ItemMidiaPainelDto[];
  programacoes: ProgramacaoPainelDto[];
};

/** One exhibition on screen; `id` changes on every new exhibition, so the player remounts its element. */
export type Execucao = {
  id: number;
  origem: "lista" | "programacao";
  /** Loop position (lista) or -1 (programação). */
  posicao: number;
  /** `itemId` of the loop row or `programacaoId`. */
  chave: string;
  midiaId: string;
  tipo: TipoMidiaPainel;
  duracaoSegundos: number;
  url: string;
  /** Where in the file the exhibition starts: a programação joined mid-window starts at its own offset. */
  inicioNaMidiaSegundos: number;
};

/** URL → local instant it failed. A failed URL is skipped for `QUARENTENA_FALHA_MS` (a re-read usually signs a new one). */
export type FalhasMidia = ReadonlyMap<string, number>;

const SEGUNDOS_NO_DIA = 86_400;
export const QUARENTENA_FALHA_MS = 60_000;
// The URL is re-read this long before the first one expires, so a slow answer still lands in time.
const ANTECEDENCIA_RELEITURA_MS = 120_000;
// A URL already close to expiry must not turn the re-read into a tight loop.
const RELEITURA_MINIMA_MS = 30_000;

const HORA_LOCAL = /T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(?:[+-]\d{2}:\d{2}|Z)$/;

/** Builds the agenda from the answer and the local instants the request left and arrived (offset = midpoint). */
export function montarAgenda(dto: MidiasPainelDto, enviadoEmLocalMs: number, recebidoEmLocalMs: number): AgendaPainel {
  const agoraMs = Date.parse(dto.agora);
  const hora = HORA_LOCAL.exec(dto.agora);
  if (Number.isNaN(agoraMs) || hora === null) throw new Error(`instante do servidor ilegível: ${dto.agora}`);
  const fracaoMs = hora[4] === undefined ? 0 : Math.floor(Number(`0.${hora[4]}`) * 1000);
  // The wall-clock part of `agora` is already São Paulo's (the server sends that offset), so midnight is that far back.
  const desdeMeiaNoiteMs = (Number(hora[1]) * 3600 + Number(hora[2]) * 60 + Number(hora[3])) * 1000 + fracaoMs;
  return {
    deslocamentoMs: agoraMs - (enviadoEmLocalMs + recebidoEmLocalMs) / 2,
    meiaNoiteMs: agoraMs - desdeMeiaNoiteMs,
    lista: dto.lista,
    programacoes: dto.programacoes,
  };
}

function falhou(falhas: FalhasMidia, url: string, agoraLocalMs: number): boolean {
  const em = falhas.get(url);
  return em !== undefined && agoraLocalMs - em < QUARENTENA_FALHA_MS;
}

/** True once the server clock passed the end of the agenda's day: today's programações are stale, re-read. */
export function diaVirou(agenda: AgendaPainel, agoraLocalMs: number): boolean {
  return agoraLocalMs + agenda.deslocamentoMs >= agenda.meiaNoiteMs + SEGUNDOS_NO_DIA * 1000;
}

/** The programação on air at the instant (first in precedence order whose window holds it and whose URL works), or null. */
export function programacaoNoAr(
  agenda: AgendaPainel,
  agoraLocalMs: number,
  falhas: FalhasMidia,
): { programacao: ProgramacaoPainelDto; decorridoSegundos: number } | null {
  if (diaVirou(agenda, agoraLocalMs)) return null;
  const segundos = (agoraLocalMs + agenda.deslocamentoMs - agenda.meiaNoiteMs) / 1000;
  for (const programacao of agenda.programacoes) {
    if (segundos < programacao.inicioSegundos || segundos >= programacao.fimSegundos) continue;
    if (falhou(falhas, programacao.url, agoraLocalMs)) continue;
    return { programacao, decorridoSegundos: segundos - programacao.inicioSegundos };
  }
  return null;
}

/**
 * Local instant of the next moment the answer to "what is on air" may change by the clock alone: a window start or
 * end, or the São Paulo midnight (re-read). Never earlier than now + 1 ms.
 */
export function proximaTrocaLocalMs(agenda: AgendaPainel, agoraLocalMs: number): number {
  const fimDoDiaMs = agenda.meiaNoiteMs + SEGUNDOS_NO_DIA * 1000;
  const agoraServidorMs = agoraLocalMs + agenda.deslocamentoMs;
  let proxima = fimDoDiaMs;
  for (const p of agenda.programacoes) {
    for (const limite of [p.inicioSegundos, p.fimSegundos]) {
      const emMs = agenda.meiaNoiteMs + limite * 1000;
      if (emMs > agoraServidorMs && emMs < proxima) proxima = emMs;
    }
  }
  return Math.max(agoraLocalMs + 1, proxima - agenda.deslocamentoMs);
}

/** Local instant to re-read before the first URL expires; null with nothing to play. */
export function proximaReleituraLocalMs(agenda: AgendaPainel, agoraLocalMs: number): number | null {
  const expiracoes = [...agenda.lista, ...agenda.programacoes].map((i) => Date.parse(i.expiraEm));
  if (expiracoes.length === 0) return null;
  const primeiraLocalMs = Math.min(...expiracoes) - agenda.deslocamentoMs;
  return Math.max(agoraLocalMs + RELEITURA_MINIMA_MS, primeiraLocalMs - ANTECEDENCIA_RELEITURA_MS);
}

/** The next playable loop position after `posicao` (-1 = from the start), wrapping; null when none plays. */
export function proximaDaLista(
  lista: readonly ItemMidiaPainelDto[],
  posicao: number,
  falhas: FalhasMidia,
  agoraLocalMs: number,
): number | null {
  for (let passo = 1; passo <= lista.length; passo++) {
    const indice = (((posicao + passo) % lista.length) + lista.length) % lista.length;
    if (!falhou(falhas, lista[indice].url, agoraLocalMs)) return indice;
  }
  return null;
}

function daLista(lista: readonly ItemMidiaPainelDto[], posicao: number, id: number): Execucao {
  const item = lista[posicao];
  return {
    id,
    origem: "lista",
    posicao,
    chave: item.itemId,
    midiaId: item.midiaId,
    tipo: item.tipo,
    duracaoSegundos: item.duracaoSegundos,
    url: item.url,
    inicioNaMidiaSegundos: 0,
  };
}

function daProgramacao(programacao: ProgramacaoPainelDto, decorridoSegundos: number, id: number): Execucao {
  return {
    id,
    origem: "programacao",
    posicao: -1,
    chave: programacao.programacaoId,
    midiaId: programacao.midiaId,
    tipo: programacao.tipo,
    duracaoSegundos: programacao.duracaoSegundos,
    url: programacao.url,
    // An image has no timeline: only video and audio resume mid-file.
    inicioNaMidiaSegundos: programacao.tipo === "Imagem" || programacao.duracaoSegundos <= 0 ? 0 : decorridoSegundos % programacao.duracaoSegundos,
  };
}

/**
 * What should be on screen after the agenda, the clock or the failures changed, keeping the current exhibition
 * (same object) when it is still right: a programação still on air, or a loop item whose mídia is still in the loop
 * while no programação covers it. A loop item that left the list (mídia disabled, list edited) stops at once.
 */
export function reavaliar(
  agenda: AgendaPainel | null,
  atual: Execucao | null,
  agoraLocalMs: number,
  falhas: FalhasMidia,
): Execucao | null {
  if (agenda === null) return null;
  const proximoId = (atual?.id ?? 0) + 1;
  const noAr = programacaoNoAr(agenda, agoraLocalMs, falhas);
  if (noAr !== null) {
    if (atual?.origem === "programacao" && atual.chave === noAr.programacao.programacaoId && atual.midiaId === noAr.programacao.midiaId) {
      return atual;
    }
    return daProgramacao(noAr.programacao, noAr.decorridoSegundos, proximoId);
  }
  if (atual?.origem === "lista" && !falhou(falhas, atual.url, agoraLocalMs)) {
    // The row id survives a re-read of the same list; a replaced list has new row ids, so the mídia decides then.
    const pelaLinha = agenda.lista.findIndex((i) => i.itemId === atual.chave);
    const posicao = pelaLinha >= 0 ? pelaLinha : agenda.lista.findIndex((i) => i.midiaId === atual.midiaId);
    if (posicao >= 0) return posicao === atual.posicao ? atual : { ...atual, posicao };
  }
  const posicao = proximaDaLista(agenda.lista, atual?.origem === "lista" ? atual.posicao - 1 : -1, falhas, agoraLocalMs);
  return posicao === null ? null : daLista(agenda.lista, posicao, proximoId);
}

/** The exhibition after `atual` ended (or failed and was put in `falhas`): the next loop item, or the programação again. */
export function aposTermino(
  agenda: AgendaPainel | null,
  atual: Execucao,
  agoraLocalMs: number,
  falhas: FalhasMidia,
): Execucao | null {
  if (agenda === null) return null;
  const proximoId = atual.id + 1;
  const noAr = programacaoNoAr(agenda, agoraLocalMs, falhas);
  if (noAr !== null) return daProgramacao(noAr.programacao, noAr.decorridoSegundos, proximoId);
  const posicao = proximaDaLista(agenda.lista, atual.origem === "lista" ? atual.posicao : -1, falhas, agoraLocalMs);
  return posicao === null ? null : daLista(agenda.lista, posicao, proximoId);
}

/** The loop item to preload while `atual` plays: only the next one, and only if it is another file. */
export function proximaParaPrecarga(
  agenda: AgendaPainel | null,
  atual: Execucao | null,
  agoraLocalMs: number,
  falhas: FalhasMidia,
): ItemMidiaPainelDto | null {
  if (agenda === null || atual === null || atual.origem !== "lista") return null;
  const posicao = proximaDaLista(agenda.lista, atual.posicao, falhas, agoraLocalMs);
  if (posicao === null) return null;
  const item = agenda.lista[posicao];
  return item.url === atual.url ? null : item;
}
