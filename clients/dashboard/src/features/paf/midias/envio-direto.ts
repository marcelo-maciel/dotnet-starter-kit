import { ErroDoEnvio, type EnvioAssinado } from "@/features/paf/midias/envio-midia";

/**
 * PUT of the file straight to the store through the presigned URL, with upload progress. XMLHttpRequest, not
 * fetch: fetch has no upload progress. The deadline is on silence, not on the whole transfer: a 500 MB video on a
 * slow store link may legitimately take many minutes, while a transfer that stops moving is dead.
 */
export function enviarDireto(
  envio: EnvioAssinado,
  arquivo: Blob,
  aoProgredir: (enviados: number, total: number) => void,
  prazoSemProgressoMs: number,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    let vigia = 0;
    const rearmar = () => {
      window.clearTimeout(vigia);
      vigia = window.setTimeout(() => {
        xhr.abort();
        reject(new ErroDoEnvio("O envio do arquivo parou de avançar. Nada foi cadastrado; tente de novo."));
      }, prazoSemProgressoMs);
    };

    xhr.open("PUT", envio.url);
    // Sent verbatim: the Content-Type is part of the signature, a different one is refused by the store.
    for (const [nome, valor] of Object.entries(envio.cabecalhosObrigatorios)) xhr.setRequestHeader(nome, valor);
    xhr.upload.onprogress = (e) => {
      rearmar();
      aoProgredir(e.loaded, e.lengthComputable ? e.total : arquivo.size);
    };
    xhr.onload = () => {
      window.clearTimeout(vigia);
      if (xhr.status >= 200 && xhr.status < 300) {
        aoProgredir(arquivo.size, arquivo.size);
        resolve();
        return;
      }
      reject(new ErroDoEnvio(`O armazenamento recusou o arquivo (status ${xhr.status}). Nada foi cadastrado; tente de novo.`));
    };
    // A network error and a CORS refusal look the same to the page (status 0, no detail).
    xhr.onerror = () => {
      window.clearTimeout(vigia);
      reject(new ErroDoEnvio("Falha de rede no envio do arquivo. Nada foi cadastrado; tente de novo."));
    };
    rearmar();
    xhr.send(arquivo);
  });
}
