// Programação de mídias (RF10, M10): the live calculation the form shows and what it checks before sending. The
// server computes the same values on every read (ProgramacaoMidiaDto) and refuses the same cases (M05, D13).

export const SEGUNDOS_NO_DIA = 86_400;
export const NOME_MAXIMO = 120; // ProgramacaoMidia.NomeMaxLength
export const TODOS_OS_DIAS = 127;

/** Weekday bits in DayOfWeek order, as ProgramacaoMidiaDto.Dias (1 = domingo … 64 = sábado). */
export const DIAS = [
  { bit: 1, curto: "Dom", nome: "Domingo" },
  { bit: 2, curto: "Seg", nome: "Segunda" },
  { bit: 4, curto: "Ter", nome: "Terça" },
  { bit: 8, curto: "Qua", nome: "Quarta" },
  { bit: 16, curto: "Qui", nome: "Quinta" },
  { bit: 32, curto: "Sex", nome: "Sexta" },
  { bit: 64, curto: "Sáb", nome: "Sábado" },
] as const;

export function alternarDia(mascara: number, bit: number): number {
  return mascara ^ bit;
}

export function rotuloDosDias(mascara: number): string {
  if (mascara === TODOS_OS_DIAS) return "Todos os dias";
  const dias = DIAS.filter((d) => (mascara & d.bit) !== 0).map((d) => d.curto);
  return dias.length === 0 ? "Nenhum dia" : dias.join(", ");
}

/** "HH:MM" or "HH:MM:SS" (the time input gives either) to seconds from midnight; null when malformed. */
export function segundosDaHora(hora: string): number | null {
  const m = /^(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(hora.trim());
  if (!m) return null;
  const [h, min, s] = [Number(m[1]), Number(m[2]), Number(m[3] ?? "0")];
  if (h > 23 || min > 59 || s > 59) return null;
  return h * 3600 + min * 60 + s;
}

/** Seconds from midnight as "HH:MM:SS"; 86400 is "24:00:00", the end of a programação that ends at midnight. */
export function horaDosSegundos(segundos: number): string {
  const h = Math.floor(segundos / 3600);
  const min = Math.floor((segundos % 3600) / 60);
  const s = segundos % 60;
  return [h, min, s].map((n) => String(n).padStart(2, "0")).join(":");
}

/** A duration in seconds as "1 h 02 min 03 s", without the zero leading units. */
export function duracaoLegivel(segundos: number): string {
  const h = Math.floor(segundos / 3600);
  const min = Math.floor((segundos % 3600) / 60);
  const s = segundos % 60;
  if (h > 0) return `${h} h ${String(min).padStart(2, "0")} min ${String(s).padStart(2, "0")} s`;
  if (min > 0) return `${min} min ${String(s).padStart(2, "0")} s`;
  return `${s} s`;
}

/** Positive integer typed by the operator, or null. */
export function inteiroPositivo(texto: string): number | null {
  const limpo = texto.trim();
  if (!/^\d+$/.test(limpo)) return null;
  const n = Number(limpo);
  return n >= 1 && Number.isSafeInteger(n) ? n : null;
}

export type Calculo = { tempoMidiaSegundos: number; tempoTotalSegundos: number; horaFinalSegundos: number; passaDaMeiaNoite: boolean };

/**
 * Times of a programação as M05 computes them: total = mídia duration × repetitions, end = start + total. Null
 * while the form lacks a mídia, a valid start or a valid repetition count.
 */
export function calcular(duracaoMidiaSegundos: number | null, horaInicial: string, repeticoesTexto: string): Calculo | null {
  const inicio = segundosDaHora(horaInicial);
  const repeticoes = inteiroPositivo(repeticoesTexto);
  if (duracaoMidiaSegundos === null || inicio === null || repeticoes === null) return null;
  const total = duracaoMidiaSegundos * repeticoes;
  const fim = inicio + total;
  return { tempoMidiaSegundos: duracaoMidiaSegundos, tempoTotalSegundos: total, horaFinalSegundos: fim, passaDaMeiaNoite: fim > SEGUNDOS_NO_DIA };
}

export type FormProgramacao = {
  midiaId: string;
  nome: string;
  dataInicial: string;
  dataFinal: string;
  horaInicial: string;
  repeticoes: string;
  dias: number;
};

export type CorpoProgramacao = {
  midiaId: string;
  nome: string;
  dataInicial: string;
  dataFinal: string;
  horaInicial: string;
  repeticoes: number;
  dias: number;
};

export type ProgramacaoValidada = { ok: true; corpo: CorpoProgramacao } | { ok: false; mensagem: string };

const DATA = /^\d{4}-\d{2}-\d{2}$/;

export function validarProgramacao(form: FormProgramacao, duracaoMidiaSegundos: number | null): ProgramacaoValidada {
  const nome = form.nome.trim();
  if (nome === "") return { ok: false, mensagem: "O nome é obrigatório." };
  if (nome.length > NOME_MAXIMO) return { ok: false, mensagem: `O nome tem no máximo ${NOME_MAXIMO} caracteres.` };
  if (form.midiaId === "" || duracaoMidiaSegundos === null) return { ok: false, mensagem: "Escolha a mídia." };
  if (!DATA.test(form.dataInicial) || !DATA.test(form.dataFinal)) return { ok: false, mensagem: "Informe a data inicial e a data final." };
  // ISO dates compare as text: no Date round trip, which would shift a day across the browser's time zone.
  if (form.dataFinal < form.dataInicial) return { ok: false, mensagem: "A data final não pode ser anterior à inicial." };
  const inicio = segundosDaHora(form.horaInicial);
  if (inicio === null) return { ok: false, mensagem: "Informe a hora inicial." };
  const repeticoes = inteiroPositivo(form.repeticoes);
  if (repeticoes === null || repeticoes > SEGUNDOS_NO_DIA) {
    return { ok: false, mensagem: `As repetições vão de 1 a ${SEGUNDOS_NO_DIA}.` };
  }
  if (form.dias < 1 || form.dias > TODOS_OS_DIAS) return { ok: false, mensagem: "Escolha ao menos um dia da semana." };
  const calculo = calcular(duracaoMidiaSegundos, form.horaInicial, form.repeticoes);
  if (calculo?.passaDaMeiaNoite) {
    return { ok: false, mensagem: "A programação termina depois da meia-noite. Reduza as repetições ou antecipe a hora inicial." };
  }
  return {
    ok: true,
    corpo: { midiaId: form.midiaId, nome, dataInicial: form.dataInicial, dataFinal: form.dataFinal, horaInicial: horaDosSegundos(inicio), repeticoes, dias: form.dias },
  };
}

export type FiltrosPesquisa = { nome: string; dias: number; hora: string; de: string; ate: string };

export const FILTROS_VAZIOS: FiltrosPesquisa = { nome: "", dias: 0, hora: "", de: "", ate: "" };

/**
 * Query string of the RF10 search (SearchProgramacoesMidiaQuery): only the filters filled in; zero days means "any
 * day" and is not sent (the server refuses a zero mask).
 */
export function consultaDaPesquisa(filtros: FiltrosPesquisa, pagina: number, tamanhoPagina: number): string {
  const p = new URLSearchParams();
  const nome = filtros.nome.trim();
  if (nome !== "") p.set("nome", nome);
  if (filtros.dias > 0) p.set("dias", String(filtros.dias));
  const hora = segundosDaHora(filtros.hora);
  if (hora !== null) p.set("hora", horaDosSegundos(hora));
  if (DATA.test(filtros.de)) p.set("de", filtros.de);
  if (DATA.test(filtros.ate)) p.set("ate", filtros.ate);
  p.set("pageNumber", String(pagina));
  p.set("pageSize", String(tamanhoPagina));
  return p.toString();
}

/** The period filter is blocked on screen when inverted (the server would answer 400). */
export function periodoInvertido(filtros: FiltrosPesquisa): boolean {
  return DATA.test(filtros.de) && DATA.test(filtros.ate) && filtros.ate < filtros.de;
}
