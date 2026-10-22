import type { AtendimentoDaFichaDto, EstadoAtendimento } from "./consultar-ficha-api.ts";

// Same bound as the server validator: the longest letter (5) followed by the largest number.
export const SENHA_MAX = 15;

/** Same folding as the server (trim, upper case), so the screen shows what is searched. */
export function normalizarSenha(texto: string): string {
  return texto.trim().toUpperCase();
}

/** The printed senha always ends in its number; anything else would only earn a 400. */
export function senhaValida(texto: string): boolean {
  const senha = normalizarSenha(texto);
  return senha.length > 0 && senha.length <= SENHA_MAX && /[0-9]$/.test(senha);
}

/** Calendar day in America/Sao_Paulo (RNF10), as the date input wants it: yyyy-mm-dd. */
export function hojeEmSaoPaulo(agora: Date): string {
  return new Intl.DateTimeFormat("en-CA", { timeZone: "America/Sao_Paulo" }).format(agora);
}

// RF12 rules as F04 and F05 enforce them; the server still decides and answers 409 otherwise.
export function acoesDoAtendimento(estado: EstadoAtendimento): { cancelar: boolean; reabilitar: boolean } {
  return {
    cancelar: estado === "Aguardando" || estado === "Chamado" || estado === "EmAtendimento",
    reabilitar: estado === "Cancelado",
  };
}

const ROTULOS: Record<EstadoAtendimento, string> = {
  Aguardando: "Aguardando",
  Chamado: "Chamado",
  EmAtendimento: "Em atendimento",
  Encerrado: "Encerrado",
  Cancelado: "Cancelado",
};

export function rotuloEstado(estado: EstadoAtendimento): string {
  return ROTULOS[estado];
}

/** hh:mm:ss with hours unbounded (a forgotten atendimento can run past a day); "-" when it never started. */
export function formatarDuracao(segundos: number | null): string {
  if (segundos === null) return "-";
  const total = Math.max(0, Math.floor(segundos));
  const horas = Math.floor(total / 3600);
  const minutos = Math.floor((total % 3600) / 60);
  const resto = total % 60;
  return [horas, minutos, resto].map((n) => String(n).padStart(2, "0")).join(":");
}

const FORMATO_INSTANTE = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

/** Instant shown in America/Sao_Paulo, with the day: the atendimento may end after midnight. */
export function formatarInstante(iso: string | null): string {
  return iso === null ? "-" : FORMATO_INSTANTE.format(new Date(iso));
}

/** Fila the atendimento was forwarded from, or null for the emission one (or an origin outside the list). */
export function filaDeOrigem(atendimentos: AtendimentoDaFichaDto[], atendimento: AtendimentoDaFichaDto): string | null {
  if (atendimento.atendimentoOrigemId === null) return null;
  return atendimentos.find((a) => a.atendimentoId === atendimento.atendimentoOrigemId)?.fila ?? null;
}
