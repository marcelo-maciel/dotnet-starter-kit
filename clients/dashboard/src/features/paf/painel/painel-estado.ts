// Panel state as a pure reducer (arquitetura seção 7, RF21). Rules it enforces:
// - every event arrives twice by design (direct send + outbox): an EventoId already applied is dropped;
// - an event whose Versao is not above the last one applied to that atendimento is dropped;
// - the list of last calls updates on arrival; only the highlight and the voice wait in the queue;
// - a recall moves the entry to the top, never duplicates it, and does not add a second queue item
//   while the same atendimento is still waiting its turn;
// - a cancel removes that atendimento's pending announcement;
// - the re-query on (re)connect brings at most 10 calls, so it removes a pending announcement only when it
//   contradicts it (a state other than Chamado with a Versao above the announced one); an atendimento
//   absent from the re-query keeps its announcement, in its place in the queue.

export type ChamadaPainelDto = {
  atendimentoId: string;
  fichaId: string;
  versao: number;
  senha: string;
  estado: string;
  estacaoId: string;
  nomeEstacao: string;
  chamadoEm: string;
  ultimaRechamadaEm: string | null;
};

export type EstadoPainelDto = { chamadas: ChamadaPainelDto[] };

export type SenhaChamadaEvento = {
  eventoId: string;
  unidadeId: string;
  atendimentoId: string;
  fichaId: string;
  versao: number;
  senha: string;
  estacaoId: string;
  nomeEstacao: string;
};

export type AtendimentoAtualizadoEvento = {
  eventoId: string;
  unidadeId: string;
  atendimentoId: string;
  fichaId: string;
  versao: number;
  estado: string;
  estacaoId: string | null;
};

export type ChamadaExibida = {
  atendimentoId: string;
  senha: string;
  nomeEstacao: string;
  estado: string;
};

/** `versao` is the one the call carried: the re-query contradicts the announcement only above it. */
export type Anuncio = { atendimentoId: string; senha: string; nomeEstacao: string; versao: number };

export type EstadoPainel = {
  /** Most recent call or recall first. */
  chamadas: ChamadaExibida[];
  versoes: Record<string, number>;
  eventosAplicados: string[];
  /** Announcements only queue after the start gesture; before it the list still updates. */
  anunciando: boolean;
  fila: Anuncio[];
  destaque: Anuncio | null;
  ultimoDestaque: Anuncio | null;
};

export type AcaoPainel =
  | { tipo: "estadoCarregado"; estado: EstadoPainelDto }
  | { tipo: "senhaChamada"; evento: SenhaChamadaEvento }
  | { tipo: "senhaRechamada"; evento: SenhaChamadaEvento }
  | { tipo: "atendimentoAtualizado"; evento: AtendimentoAtualizadoEvento }
  | { tipo: "anunciosIniciados" }
  | { tipo: "destaqueIniciado" }
  | { tipo: "destaqueEncerrado" };

export const LIMITE_LISTA = 10;
// Enough to recognise the outbox copy of any event still in flight; older ids can never come back.
const LIMITE_EVENTOS_LEMBRADOS = 500;

export const ESTADO_INICIAL_PAINEL: EstadoPainel = {
  chamadas: [],
  versoes: {},
  eventosAplicados: [],
  anunciando: false,
  fila: [],
  destaque: null,
  ultimoDestaque: null,
};

const ESTADO_CANCELADO = "Cancelado";
const ESTADO_CHAMADO = "Chamado";

function lembrarEvento(estado: EstadoPainel, eventoId: string): string[] {
  const lista = [...estado.eventosAplicados, eventoId];
  return lista.length > LIMITE_EVENTOS_LEMBRADOS ? lista.slice(-LIMITE_EVENTOS_LEMBRADOS) : lista;
}

function deveIgnorar(estado: EstadoPainel, eventoId: string, atendimentoId: string, versao: number): boolean {
  if (estado.eventosAplicados.includes(eventoId)) return true;
  const aplicada = estado.versoes[atendimentoId];
  return aplicada !== undefined && versao <= aplicada;
}

function aplicarChamada(estado: EstadoPainel, evento: SenhaChamadaEvento): EstadoPainel {
  if (deveIgnorar(estado, evento.eventoId, evento.atendimentoId, evento.versao)) return estado;
  const exibida: ChamadaExibida = {
    atendimentoId: evento.atendimentoId,
    senha: evento.senha,
    nomeEstacao: evento.nomeEstacao,
    estado: "Chamado",
  };
  const chamadas = [exibida, ...estado.chamadas.filter((c) => c.atendimentoId !== evento.atendimentoId)].slice(
    0,
    LIMITE_LISTA,
  );
  const jaNaFila = estado.fila.some((a) => a.atendimentoId === evento.atendimentoId);
  const anuncio: Anuncio = {
    atendimentoId: evento.atendimentoId,
    senha: evento.senha,
    nomeEstacao: evento.nomeEstacao,
    versao: evento.versao,
  };
  const fila = !estado.anunciando || jaNaFila ? estado.fila : [...estado.fila, anuncio];
  return {
    ...estado,
    chamadas,
    fila,
    versoes: { ...estado.versoes, [evento.atendimentoId]: evento.versao },
    eventosAplicados: lembrarEvento(estado, evento.eventoId),
  };
}

function aplicarAtualizacao(estado: EstadoPainel, evento: AtendimentoAtualizadoEvento): EstadoPainel {
  if (deveIgnorar(estado, evento.eventoId, evento.atendimentoId, evento.versao)) return estado;
  return {
    ...estado,
    chamadas: estado.chamadas
      .map((c) => (c.atendimentoId === evento.atendimentoId ? { ...c, estado: evento.estado } : c))
      .filter((c) => c.estado !== ESTADO_CANCELADO),
    fila:
      evento.estado === ESTADO_CANCELADO
        ? estado.fila.filter((a) => a.atendimentoId !== evento.atendimentoId)
        : estado.fila,
    versoes: { ...estado.versoes, [evento.atendimentoId]: evento.versao },
    eventosAplicados: lembrarEvento(estado, evento.eventoId),
  };
}

function aplicarEstadoCarregado(estado: EstadoPainel, dto: EstadoPainelDto): EstadoPainel {
  // The server is the truth after a (re)connection; a version already applied locally above the
  // server's (event raced the query) is kept so a stale copy of that event is still dropped later.
  const versoes = { ...estado.versoes };
  for (const c of dto.chamadas) versoes[c.atendimentoId] = Math.max(versoes[c.atendimentoId] ?? 0, c.versao);
  const doServidor = new Map(dto.chamadas.map((c) => [c.atendimentoId, c]));
  const contradiz = (a: Anuncio) => {
    const c = doServidor.get(a.atendimentoId);
    return c !== undefined && c.estado !== ESTADO_CHAMADO && c.versao > a.versao;
  };
  return {
    ...estado,
    versoes,
    chamadas: dto.chamadas
      .filter((c) => c.estado !== ESTADO_CANCELADO)
      .slice(0, LIMITE_LISTA)
      .map((c) => ({ atendimentoId: c.atendimentoId, senha: c.senha, nomeEstacao: c.nomeEstacao, estado: c.estado })),
    fila: estado.fila.filter((a) => !contradiz(a)),
  };
}

export function reduzirPainel(estado: EstadoPainel, acao: AcaoPainel): EstadoPainel {
  switch (acao.tipo) {
    case "estadoCarregado":
      return aplicarEstadoCarregado(estado, acao.estado);
    case "senhaChamada":
    case "senhaRechamada":
      return aplicarChamada(estado, acao.evento);
    case "atendimentoAtualizado":
      return aplicarAtualizacao(estado, acao.evento);
    case "anunciosIniciados":
      return { ...estado, anunciando: true };
    case "destaqueIniciado": {
      if (estado.destaque !== null || estado.fila.length === 0) return estado;
      const [proximo, ...resto] = estado.fila;
      return { ...estado, destaque: proximo, ultimoDestaque: proximo, fila: resto };
    }
    case "destaqueEncerrado":
      return { ...estado, destaque: null };
  }
}
