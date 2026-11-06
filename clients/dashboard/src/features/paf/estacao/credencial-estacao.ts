// Station credential storage (arquitetura seção 5, "chaves distintas por papel"). The keys stay outside
// the `fsh.dashboard.` prefix on purpose: the kit's AuthProvider reacts to any change under that prefix
// and would carry the attendant's logout into the panel window.

export type PapelEstacao = "Emissao" | "Painel";

export type CredencialEstacao = {
  papel: PapelEstacao;
  tenant: string;
  /** Long-lived secret, used only to renew the access token. */
  segredo: string;
  accessToken: string;
  /** ISO instant from the server. */
  accessTokenExpiresAt: string;
};

const PREFIXO = "paf.estacao.";

function chave(papel: PapelEstacao): string {
  return `${PREFIXO}${papel.toLowerCase()}`;
}

export function ehPapelEstacao(valor: string): valor is PapelEstacao {
  return valor === "Emissao" || valor === "Painel";
}

export function lerCredencial(papel: PapelEstacao): CredencialEstacao | null {
  const bruto = localStorage.getItem(chave(papel));
  if (bruto === null) return null;
  try {
    const lido = JSON.parse(bruto) as Partial<CredencialEstacao>;
    if (
      lido.papel !== papel ||
      typeof lido.tenant !== "string" ||
      typeof lido.segredo !== "string" ||
      typeof lido.accessToken !== "string" ||
      typeof lido.accessTokenExpiresAt !== "string"
    ) {
      return null;
    }
    return lido as CredencialEstacao;
  } catch {
    // A corrupted entry is the same as no credential: the screen asks for a new link.
    return null;
  }
}

export function gravarCredencial(credencial: CredencialEstacao): void {
  localStorage.setItem(chave(credencial.papel), JSON.stringify(credencial));
}

export function apagarCredencial(papel: PapelEstacao): void {
  localStorage.removeItem(chave(papel));
}
