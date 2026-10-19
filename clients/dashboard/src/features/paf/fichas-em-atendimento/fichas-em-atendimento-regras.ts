// RF13 display rules, pure so node --test covers them (D21).

export const PERMISSAO_CANCELAR = "Permissions.PAF.FichasDoDia.Cancelar";

const ROTULO_ESTADO: Record<string, string> = { Chamado: "Chamado", EmAtendimento: "Em atendimento" };

export function rotuloEstado(estado: string): string {
  return ROTULO_ESTADO[estado] ?? estado;
}

/** 59 → "menos de 1 min"; 3725 → "1 h 02 min"; 90000 → "1 d 1 h 00 min". Negative or NaN reads as zero. */
export function formatarPermanencia(segundos: number): string {
  const total = Number.isFinite(segundos) && segundos > 0 ? Math.floor(segundos) : 0;
  if (total < 60) return "menos de 1 min";
  const dias = Math.floor(total / 86400);
  const horas = Math.floor((total % 86400) / 3600);
  const minutos = Math.floor((total % 3600) / 60);
  const mm = String(minutos).padStart(2, "0");
  if (dias > 0) return `${dias} d ${horas} h ${mm} min`;
  if (horas > 0) return `${horas} h ${mm} min`;
  return `${minutos} min`;
}

const HORARIO_SP = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
});

/** Entry time on the São Paulo wall clock with the day, because an abandoned atendimento is often from yesterday. */
export function formatarEntrada(iso: string): string {
  return HORARIO_SP.format(new Date(iso));
}

export function textoTotal(total: number): string {
  if (total === 0) return "Nenhuma ficha em atendimento.";
  return total === 1 ? "1 ficha em atendimento" : `${total} fichas em atendimento`;
}
