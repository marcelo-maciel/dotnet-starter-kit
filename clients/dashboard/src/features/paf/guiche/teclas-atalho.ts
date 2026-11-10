// Guichê hotkeys of RF06 (pure, node --test per D21). The server stores one key per action, F1 to F12 only, and
// refuses two actions on one key; this module turns a keydown into the action, if any, and mirrors the buttons'
// own enable rules so a key never does what its button could not.

export type AcaoAtalho = "chamarProxima" | "encaminhar" | "encerrar" | "encerrarEChamar" | "iniciar" | "chamarNovamente";

/** GET api/v1/paf/guiche/teclas-atalho. */
export type TeclasGuicheDto = Record<AcaoAtalho, string>;

export type TeclaPressionada = {
  key: string;
  ctrlKey: boolean;
  altKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
  repeat: boolean;
};

export const ACOES_ATALHO: readonly AcaoAtalho[] = [
  "chamarProxima",
  "encaminhar",
  "encerrar",
  "encerrarEChamar",
  "iniciar",
  "chamarNovamente",
];

/**
 * The action bound to the key, or null. A modified key (Ctrl+F5, Alt+F4) belongs to the browser or the system, and
 * an auto-repeat of a held key would fire the same operation many times, so both are ignored.
 */
export function acaoDaTecla(teclas: TeclasGuicheDto, tecla: TeclaPressionada): AcaoAtalho | null {
  if (tecla.repeat || tecla.ctrlKey || tecla.altKey || tecla.metaKey || tecla.shiftKey) return null;
  const nome = tecla.key.toUpperCase();
  return ACOES_ATALHO.find((acao) => teclas[acao].toUpperCase() === nome) ?? null;
}

/**
 * Same conditions as the guichê buttons: rechamar and iniciar need a Chamado atendimento, encerrar and encaminhar an
 * EmAtendimento one (RF17: forwarding only from an atendimento in progress); calling is refused only while busy.
 */
export function acaoPermitida(acao: AcaoAtalho, estado: string | null, ocupado: boolean): boolean {
  if (ocupado) return false;
  switch (acao) {
    case "chamarProxima":
    case "encerrarEChamar":
      return true;
    case "iniciar":
    case "chamarNovamente":
      return estado === "Chamado";
    case "encerrar":
    case "encaminhar":
      return estado === "EmAtendimento";
  }
}
