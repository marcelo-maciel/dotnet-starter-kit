// Device link of the station cadastro (RF04, C06): which types get a link, what the listing says about the live link
// and the last connection per role, and the notice shown with a freshly generated link. Pure module (node --test, D21);
// the server decides everything (GerarVinculo refuses a guichê or an inactive station), this only words it.
import { papeisDoTipo, type TipoEstacao } from "./estacoes-regras.ts";

export type PapelComLink = "Emissao" | "Painel";

/** The live (not revoked) link as the listing returns it; `papeisResgatados` is empty until a device redeems it. */
export type VinculoAtualDto = { criadoEm: string; resgatadoEm: string | null; papeisResgatados: string[] };

export type SituacaoVinculo = {
  tipo: TipoEstacao;
  vinculoAtual: VinculoAtualDto | null;
  ultimaConexaoEmissaoEm: string | null;
  ultimaConexaoPainelEm: string | null;
};

const ROTULO_PAPEL_LINK: Record<PapelComLink, string> = { Emissao: "Totem", Painel: "Painel" };

const HORARIO_SP = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
});

/** Instant on the São Paulo wall clock, with the year: a panel may sit linked for months. */
export function formatarInstante(iso: string): string {
  return HORARIO_SP.format(new Date(iso));
}

/** Roles of the type that run on a linked device; guichê and administração always need a person's login. */
export function papeisComLink(tipo: TipoEstacao): PapelComLink[] {
  return papeisDoTipo(tipo).filter((p): p is PapelComLink => p === "Emissao" || p === "Painel");
}

export function usaLink(tipo: TipoEstacao): boolean {
  return papeisComLink(tipo).length > 0;
}

/** One line for the link, then one per role of the type: whether the current redemption covers it and its last connection. */
export function linhasDoVinculo(s: SituacaoVinculo): string[] {
  const papeis = papeisComLink(s.tipo);
  if (papeis.length === 0) return [];
  const v = s.vinculoAtual;
  const linhas = [
    v === null
      ? "Sem link: gere um para vincular o aparelho."
      : v.resgatadoEm === null
        ? `Link gerado em ${formatarInstante(v.criadoEm)}, aguardando o aparelho.`
        : `Link resgatado em ${formatarInstante(v.resgatadoEm)}.`,
  ];
  const ultima: Record<PapelComLink, string | null> = { Emissao: s.ultimaConexaoEmissaoEm, Painel: s.ultimaConexaoPainelEm };
  for (const papel of papeis) {
    const instante = ultima[papel];
    const conexao = instante === null ? "nunca conectou" : `última conexão em ${formatarInstante(instante)}`;
    const foraDoResgate = v?.resgatadoEm != null && !v.papeisResgatados.includes(papel);
    linhas.push(`${ROTULO_PAPEL_LINK[papel]}: ${foraDoResgate ? "fora do resgate atual; " : ""}${conexao}.`);
  }
  return linhas;
}

/** Notice shown once with the new link; `tinhaLink` comes from the listing read before the generation. */
export function avisosDoLinkGerado(tinhaLink: boolean): string[] {
  const avisos = ["Abra este link uma única vez no aparelho da estação. Ele só aparece agora: se perder, gere outro."];
  if (tinhaLink) avisos.push("O link anterior deixou de valer e o aparelho que o usava foi desconectado.");
  return avisos;
}
