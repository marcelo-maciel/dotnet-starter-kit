// RF08: the first unidade of the list is the principal, where the licence cascade starts and which pays the licence,
// so the screen says it in words next to the row, never only by position.
export const ROTULO_PRINCIPAL = "Unidade principal (paga a licença)";

/** Moves the item at `indice` one step up (-1) or down (+1); out of bounds returns the same order. */
export function moverUnidade(lista: readonly string[], indice: number, passo: -1 | 1): string[] {
  const destino = indice + passo;
  if (indice < 0 || indice >= lista.length || destino < 0 || destino >= lista.length) return [...lista];
  const nova = [...lista];
  [nova[indice], nova[destino]] = [nova[destino], nova[indice]];
  return nova;
}

/** Unidades of `antes` missing from `depois`: the ones whose atendimentos in progress the screen offers to release. */
export function unidadesRetiradas(antes: readonly string[], depois: readonly string[]): string[] {
  const ficam = new Set(depois);
  return antes.filter((u) => !ficam.has(u));
}

/** "admin@loja.com.br" → "loja.com.br"; null when the text has no single "@" with a domain after it. */
export function dominioDoEmail(email: string | undefined): string | null {
  if (!email) return null;
  const partes = email.trim().split("@");
  return partes.length === 2 && partes[0] !== "" && partes[1].includes(".") ? partes[1].toLowerCase() : null;
}

/**
 * D14: the login is the e-mail; the legacy "automatic login" became a suggestion, `nome.sobrenome@<client domain>`,
 * without accents or anything an address does not take. Null when there is no name or no domain to suggest from.
 */
export function sugestaoDeEmail(nome: string, sobrenome: string, dominio: string | null): string | null {
  if (dominio === null) return null;
  const parte = (texto: string) =>
    texto
      .normalize("NFD")
      .replace(/[̀-ͯ]/g, "")
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, ".")
      .replace(/^\.+|\.+$/g, "");
  const local = [parte(nome), parte(sobrenome)].filter((p) => p !== "").join(".");
  return local === "" ? null : `${local}@${dominio}`;
}
