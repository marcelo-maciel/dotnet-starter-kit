// RF06 screen rules (pure, node --test per D21): labels, the inherited or overridden badge and the choices of each
// field. The server validates and stores; this only says what the screen shows and offers.
import type { MidiaDaUnidade, ParametroDto } from "./parametros-api.ts";

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
  exibirInformacaoDetalhadaFila: "Exibir informação detalhada de fila no guichê",
  chamarFichaPorVoz: "Chamar ficha por voz: o que a voz fala",
  mensagemAoChamar: "Mensagem exibida ao chamar a ficha",
  logotipo: "Logotipo do totem, do painel e da ficha impressa",
};

const ROTULOS_OPCAO: Record<string, string> = {
  ordem: "Ordem da estação (prioridade normal)",
  nome: "Nome da fila",
  senha: "Só a senha",
  senhaEMensagem: "A senha e a mensagem ao chamar",
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
    // C19: the images of the unidade are appended by `escolhasDeImagem`; the system logo is always offered.
    case "Imagem":
      return [["", LOGOTIPO_DO_SISTEMA]];
    case "Inteiro":
    case "Texto":
      return [];
  }
}

/** Effective value as the screen shows it: the choice's label, or "(vazio)" for an empty text. */
export function valorExibido(item: Pick<ParametroDto, "tipo" | "opcoes">, valor: string): string {
  const escolha = escolhasDoCampo(item).find(([v]) => v === valor);
  if (escolha) return escolha[1];
  if (item.tipo === "Imagem") return IMAGEM_PERSONALIZADA;
  return valor === "" ? "(vazio)" : valor;
}

// --- C19: RF06 "Logotipo" ---

export const LOGOTIPO_DO_SISTEMA = "Logotipo padrão do sistema";
export const IMAGEM_PERSONALIZADA = "Imagem personalizada";

/**
 * The logo picker: the system logo, then the active images of the unidade by description. A current value outside
 * them (the empresa's image lives in another unidade, or it was deactivated) stays selectable as is, so the select
 * never shows a value it does not have; the server decides whether it may be stored here.
 */
export function escolhasDeImagem(midias: readonly MidiaDaUnidade[], valorAtual: string): [string, string][] {
  const imagens: [string, string][] = midias
    .filter((m) => m.tipo === "Imagem" && m.ativa)
    .sort((a, b) => a.descricao.localeCompare(b.descricao, "pt-BR", { sensitivity: "base" }))
    .map((m) => [m.id, m.descricao]);
  const conhecida = valorAtual === "" || imagens.some(([id]) => id === valorAtual);
  return [["", LOGOTIPO_DO_SISTEMA], ...imagens, ...(conhecida ? [] : [[valorAtual, IMAGEM_PERSONALIZADA] as [string, string]])];
}

/**
 * Review R4 of wave 4: the server stores only the system logo or an ACTIVE image of the current unidade, at either
 * level (GravacaoParametros reads the mídia under the unit filter). An inherited image of another unidade, or one
 * deactivated since, stays visible in the picker but cannot be saved: the save buttons wait for a valid choice.
 */
export function logotipoGravavel(midias: readonly MidiaDaUnidade[], valor: string): boolean {
  return valor === "" || midias.some((m) => m.id === valor && m.tipo === "Imagem" && m.ativa);
}

export const MENSAGEM_LOGOTIPO_NAO_GRAVAVEL =
  "Este logotipo é uma imagem de outra unidade ou inativa: escolha o padrão do sistema ou uma imagem desta unidade para salvar.";

/** The logo value as the screen shows it: the image's description when it is one of the unidade's. */
export function logotipoExibido(midias: readonly MidiaDaUnidade[], valor: string): string {
  if (valor === "") return LOGOTIPO_DO_SISTEMA;
  const midia = midias.find((m) => m.id === valor);
  return midia ? `${IMAGEM_PERSONALIZADA}: ${midia.descricao}` : IMAGEM_PERSONALIZADA;
}

/** Groups in the catalog's order, each with its items in the same order. */
export function agruparPorGrupo(itens: ParametroDto[]): [string, ParametroDto[]][] {
  const grupos = new Map<string, ParametroDto[]>();
  for (const item of itens) grupos.set(item.grupo, [...(grupos.get(item.grupo) ?? []), item]);
  return [...grupos.entries()];
}
