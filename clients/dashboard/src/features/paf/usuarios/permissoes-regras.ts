// RF08 per-user permissions (C15): pure rules of the permission panel, so the checkbox states are testable without React.

export type PermissaoPafDto = { nome: string; rotulo: string };
export type GrupoPermissoesPafDto = { nome: string; observacao: string | null; permissoes: PermissaoPafDto[] };
export type PermissoesUsuarioPafDto = {
  usuarioId: string;
  grupos: GrupoPermissoesPafDto[];
  dosPapeis: string[];
  individuais: string[];
  alteraveisPeloChamador: string[];
};

export type EstadoPermissao = {
  marcada: boolean;
  travada: boolean;
  /** Why the checkbox is locked, said in words next to it; null when the caller may toggle it. */
  motivo: string | null;
};

export const MOTIVO_DO_PAPEL = "vem do perfil";
export const MOTIVO_SEM_PERMISSAO = "você não tem esta permissão";

/**
 * A permission of the user's role is always on and cannot be taken from him here (roles add up); one the caller does
 * not hold keeps the user's stored value, because the server refuses granting or removing it; the rest follow the
 * panel's marks.
 */
export function estadoDaPermissao(nome: string, dto: PermissoesUsuarioPafDto, marcadas: ReadonlySet<string>): EstadoPermissao {
  if (dto.dosPapeis.includes(nome)) return { marcada: true, travada: true, motivo: MOTIVO_DO_PAPEL };
  if (!dto.alteraveisPeloChamador.includes(nome)) {
    return { marcada: dto.individuais.includes(nome), travada: true, motivo: MOTIVO_SEM_PERMISSAO };
  }
  return { marcada: marcadas.has(nome), travada: false, motivo: null };
}

/** Toggles `nome`; returns a new set and never touches the given one. */
export function alternar(marcadas: ReadonlySet<string>, nome: string): Set<string> {
  const nova = new Set(marcadas);
  if (nova.has(nome)) nova.delete(nome);
  else nova.add(nome);
  return nova;
}

/** The individual set to send: the marks, sorted so the request body is stable. */
export function individuaisParaSalvar(marcadas: ReadonlySet<string>): string[] {
  return [...marcadas].sort();
}

/** True when the marks differ from what the server holds, to enable the save button. */
export function alterado(dto: PermissoesUsuarioPafDto, marcadas: ReadonlySet<string>): boolean {
  return dto.individuais.length !== marcadas.size || dto.individuais.some((p) => !marcadas.has(p));
}
