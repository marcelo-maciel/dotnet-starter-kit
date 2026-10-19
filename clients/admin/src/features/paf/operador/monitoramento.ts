// Pure display rules of the station monitoring screen (RNF20). The server decides connected state and warning (it
// needs the connection registry and the PostgreSQL clock); this only groups and words what it returned.

export type PapelEstacaoMonitorado = {
  unidadeId: string;
  unidadeNome: string;
  estacaoId: string;
  estacaoNome: string;
  tipo: string;
  ativa: boolean;
  papel: string;
  ultimaConexaoEm: string | null;
  ultimaQuedaEm: string | null;
  conectada: boolean;
  aviso: boolean;
};

export type SituacaoPapel = "Conectada" | "Sem conexão" | "Nunca conectou" | "Desativada";

export function situacaoPapel(p: PapelEstacaoMonitorado): SituacaoPapel {
  if (!p.ativa) return "Desativada";
  if (p.conectada) return "Conectada";
  return p.ultimaConexaoEm === null ? "Nunca conectou" : "Sem conexão";
}

const ROTULO_PAPEL: Record<string, string> = { Emissao: "Totem (emissão)", Painel: "Painel chamador" };

export function rotuloPapel(papel: string): string {
  return ROTULO_PAPEL[papel] ?? papel;
}

const FORMATO = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

/** An instant from the server shown in America/Sao_Paulo (RNF10), whatever the browser's zone. */
export function instanteEmSaoPaulo(iso: string | null): string {
  return iso === null ? "nunca" : FORMATO.format(new Date(iso));
}

export type GrupoUnidade = { unidadeId: string; unidadeNome: string; papeis: PapelEstacaoMonitorado[]; avisos: number };

/** Keeps the server's order (unidade, station name, role) and counts the warnings of each unidade. */
export function agruparPorUnidade(papeis: PapelEstacaoMonitorado[]): GrupoUnidade[] {
  const grupos: GrupoUnidade[] = [];
  for (const p of papeis) {
    let grupo = grupos.at(-1);
    if (grupo?.unidadeId !== p.unidadeId) {
      grupo = { unidadeId: p.unidadeId, unidadeNome: p.unidadeNome, papeis: [], avisos: 0 };
      grupos.push(grupo);
    }
    grupo.papeis.push(p);
    if (p.aviso) grupo.avisos += 1;
  }
  return grupos;
}
