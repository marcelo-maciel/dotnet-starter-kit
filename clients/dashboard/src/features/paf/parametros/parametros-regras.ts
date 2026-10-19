// RF06 screen rules (pure, node --test per D21): labels, the inherited or overridden badge and the choices of each
// field. The server validates and stores; this only says what the screen shows and offers.
import type { ParametroDto } from "./parametros-api.ts";

const ROTULOS: Record<string, string> = {
  somenteFichasDaDataAtual: "Chamar somente fichas da data atual",
  tituloFichas: "Título das fichas",
  mensagemRodapeFicha: "Mensagem de rodapé impressa",
  imprimirQrFicha: "Imprimir o QR da ficha",
  teclaChamarProxima: "Chamar o próximo da fila",
  teclaEncaminhar: "Encaminhar atendimento",
  teclaEncerrar: "Encerrar atendimento",
  teclaEncerrarEChamar: "Encerrar e chamar",
  teclaIniciar: "Iniciar atendimento",
  teclaChamarNovamente: "Chamar novamente",
  ordenacaoFilas: "Ordenação de filas",
  quantidadeChamadasPainel: "Quantidade de fichas chamadas no painel",
  fonteMinimaTotemPx: "Fonte mínima do totem (px)",
};

const ROTULOS_OPCAO: Record<string, string> = {
  ordem: "Ordem da estação (prioridade normal)",
  nome: "Nome da fila",
};

export const TECLAS_PERMITIDAS: readonly string[] = Array.from({ length: 12 }, (_, i) => `F${i + 1}`);

export type Selo = { texto: string; sobrescrito: boolean };

export function rotuloDoParametro(chave: string): string {
  return ROTULOS[chave] ?? chave;
}

/** RF06: the screen says what is inherited and what was overridden, or nobody understands why two stores differ. */
export function seloDaOrigem(origem: ParametroDto["origem"]): Selo {
  switch (origem) {
    case "Unidade":
      return { texto: "Sobrescrito nesta unidade", sobrescrito: true };
    case "Empresa":
      return { texto: "Herdado da empresa", sobrescrito: false };
    case "Fabrica":
      return { texto: "Padrão do sistema", sobrescrito: false };
  }
}

/** Fixed choices of the field, as [stored value, label]; empty for free text and numbers. */
export function escolhasDoCampo(item: Pick<ParametroDto, "tipo" | "opcoes">): [string, string][] {
  switch (item.tipo) {
    case "Booleano":
      return [
        ["true", "Sim"],
        ["false", "Não"],
      ];
    case "Tecla":
      return TECLAS_PERMITIDAS.map((t) => [t, t]);
    case "Opcao":
      return item.opcoes.map((o) => [o, ROTULOS_OPCAO[o] ?? o]);
    case "Inteiro":
    case "Texto":
      return [];
  }
}

/** Effective value as the screen shows it: the choice's label, or "(vazio)" for an empty text. */
export function valorExibido(item: Pick<ParametroDto, "tipo" | "opcoes">, valor: string): string {
  const escolha = escolhasDoCampo(item).find(([v]) => v === valor);
  if (escolha) return escolha[1];
  return valor === "" ? "(vazio)" : valor;
}

/** Groups in the catalog's order, each with its items in the same order. */
export function agruparPorGrupo(itens: ParametroDto[]): [string, ParametroDto[]][] {
  const grupos = new Map<string, ParametroDto[]>();
  for (const item of itens) grupos.set(item.grupo, [...(grupos.get(item.grupo) ?? []), item]);
  return [...grupos.entries()];
}
