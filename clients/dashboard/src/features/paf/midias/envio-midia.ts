import { formatarBytes } from "./midias-regras.ts";

// Upload of a mídia file (RF03, M03 contract): ask for a presigned PUT, send the bytes straight to the store, then
// confirm. The order is the invariant: the confirmation (the only step that creates or swaps the mídia) runs only
// after the store accepted the whole file, so a network failure in the PUT leaves no mídia behind.

/** EnvioMidiaDto as the API serializes it. */
export type EnvioAssinado = {
  envioId: string;
  url: string;
  cabecalhosObrigatorios: Record<string, string>;
  expiraEm: string;
  tetoBytes: number;
};

/** A refusal decided in the browser, already in the operator's words. */
export class ErroDoEnvio extends Error {}

export type EtapasDoEnvio<T> = {
  pedir: () => Promise<EnvioAssinado>;
  enviar: (envio: EnvioAssinado) => Promise<void>;
  confirmar: (envioId: string) => Promise<T>;
};

export async function enviarEConfirmar<T>(tamanhoBytes: number, etapas: EtapasDoEnvio<T>): Promise<T> {
  // Checked before asking: an empty file would only be refused at confirmation, after an envio row and a PUT.
  if (tamanhoBytes <= 0) throw new ErroDoEnvio("O arquivo está vazio.");
  const envio = await etapas.pedir();
  // The signature does not bind the size, so the server checks it only at confirmation; checking here spares
  // sending hundreds of megabytes that would be refused anyway.
  if (tamanhoBytes > envio.tetoBytes) {
    throw new ErroDoEnvio(`O arquivo tem ${formatarBytes(tamanhoBytes)}; o limite deste tipo é ${formatarBytes(envio.tetoBytes)}.`);
  }
  await etapas.enviar(envio);
  return etapas.confirmar(envio.envioId);
}
