import type { ContratoVigenteDto } from "./unidades-api.ts";

// RF23 (REQ 03/10/2026): a unidade without a contract in force has no licence balance, so a new store only gets
// sessions after the Codefi records its contract. The screen says it always, not only after a creation.
export const AVISO_CONTRATO =
  "Plano e limite de licenças de cada unidade são gravados pela Codefi. Uma unidade nova só recebe logins depois que a Codefi gravar o contrato dela.";

export const SEM_CONTRATO = "Sem contrato vigente: a unidade não recebe logins até a Codefi gravar o contrato.";

/** Which buttons a row shows. Renaming follows the scope (server checks it); the rest is the escopo empresa's alone. */
export type AcoesUnidade = { renomear: boolean; desativar: boolean; reativar: boolean };

export function acoesDaUnidade(administraUnidades: boolean, ativa: boolean): AcoesUnidade {
  return {
    renomear: true,
    desativar: administraUnidades && ativa,
    reativar: administraUnidades && !ativa,
  };
}

/** "2027-01-31" → "31/01/2027", by text: a Date would shift the calendar day by the browser's offset. */
export function dataDoCalendario(iso: string): string {
  const [ano, mes, dia] = iso.split("-");
  return `${dia}/${mes}/${ano}`;
}

export function resumoContrato(contrato: ContratoVigenteDto | null): string {
  if (contrato === null) return SEM_CONTRATO;
  const sessoes = contrato.limiteSessoes === 1 ? "1 login simultâneo" : `${contrato.limiteSessoes} logins simultâneos`;
  const vigencia = contrato.fimVigencia === null ? "sem data de fim" : `até ${dataDoCalendario(contrato.fimVigencia)}`;
  return `Plano ${contrato.plano} · ${sessoes} · ${vigencia}`;
}
