// D03 (RF22): the user who logged in above the unit's licence limit, in a unit that allows it, sees a notice on his
// own screen. The server keeps the record (ConsumosLicenca, Excedente) and answers GET api/v1/paf/sessao/aviso-licenca
// with the latest one of the session, or 204. Closing hides that record for the browser tab; a new one shows again.

export type AvisoLicencaDto = {
  registroId: string;
  unidadeId: string;
  unidade: string;
  ocupadas: number;
  limite: number;
  registradoEm: string;
};

export const CHAVE_AVISO_LICENCA_FECHADO = "paf:aviso-licenca-fechado";

/** The notice to show, or null: none recorded, or the user already closed this very record. */
export function avisoAMostrar(aviso: AvisoLicencaDto | null | undefined, fechado: string | null): AvisoLicencaDto | null {
  if (!aviso) return null;
  return aviso.registroId === fechado ? null : aviso;
}

export function textoAvisoLicenca(aviso: AvisoLicencaDto): string {
  return (
    `Você entrou acima do limite de licenças da unidade ${aviso.unidade}: ` +
    `${aviso.ocupadas} sessões para ${aviso.limite} contratadas. O acesso foi liberado e o excedente foi registrado.`
  );
}
