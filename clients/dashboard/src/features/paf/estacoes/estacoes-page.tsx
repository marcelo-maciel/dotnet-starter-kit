import { useId, useState, type ReactNode } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ApiRequestError } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { useAuth } from "@/auth/use-auth";
import { DialogoDesativacao } from "@/features/paf/estacoes/desativacao-dialogo";
import {
  atualizarEstacao,
  criarEstacao,
  liberarGuiche,
  listarEstacoes,
  listarFilas,
  listarPrioridades,
  type EstacaoCorpo,
  type EstacaoDto,
} from "@/features/paf/estacoes/estacoes-api";
import {
  ALVO_MINIMO_BOTAO_PX,
  alternar,
  avisosDeCor,
  EMISSAO_VAZIA,
  formularios,
  GUICHE_VAZIO,
  montarBlocos,
  mover,
  PAINEL_VAZIO,
  papeisDoTipo,
  ROTULO_TIPO,
  TEMPO_MAXIMO_S,
  TEMPO_MINIMO_S,
  TIPOS_ESTACAO,
  type EmissaoForm,
  type GuicheForm,
  type PainelForm,
  type Papel,
  type TipoEstacao,
} from "@/features/paf/estacoes/estacoes-regras";

const CHAVE_ESTACOES = ["paf", "estacoes"] as const;
const PERMISSAO_LIBERAR_GUICHE = "Permissions.PAF.Guiches.Liberar";
const ROTULO_PAPEL: Record<Papel, string> = { Emissao: "Emissão de Fichas", Guiche: "Guichê", Painel: "Painel Chamador" };

type Edicao = {
  id: string | null;
  nome: string;
  tipo: TipoEstacao;
  ramal: string;
  filas: Record<Papel, string[]>;
  prioridades: string[];
  emissao: EmissaoForm;
  guiche: GuicheForm;
  painel: PainelForm;
};

function novaEdicao(): Edicao {
  return {
    id: null,
    nome: "",
    tipo: "Guiche",
    ramal: "",
    filas: { Emissao: [], Guiche: [], Painel: [] },
    prioridades: [],
    emissao: EMISSAO_VAZIA,
    guiche: GUICHE_VAZIO,
    painel: PAINEL_VAZIO,
  };
}

function edicaoDe(e: EstacaoDto): Edicao {
  const porPapel = (papel: Papel) =>
    e.filas.filter((f) => f.papel === papel).sort((a, b) => a.ordem - b.ordem).map((f) => f.filaId);
  return {
    id: e.id,
    nome: e.nome,
    tipo: e.tipo,
    ramal: e.ramal ?? "",
    filas: { Emissao: porPapel("Emissao"), Guiche: porPapel("Guiche"), Painel: porPapel("Painel") },
    prioridades: [...e.prioridades].sort((a, b) => a.ordem - b.ordem).map((p) => p.prioridadeId),
    ...formularios(e.configuracao),
  };
}

function corpoDe(edicao: Edicao): EstacaoCorpo {
  const papeis = papeisDoTipo(edicao.tipo);
  return {
    nome: edicao.nome.trim(),
    ramal: edicao.ramal.trim() === "" ? null : edicao.ramal.trim(),
    filas: papeis.flatMap((papel) => edicao.filas[papel].map((filaId, ordem) => ({ papel, filaId, ordem }))),
    prioridades: papeis.includes("Guiche") ? edicao.prioridades.map((prioridadeId, ordem) => ({ prioridadeId, ordem })) : [],
    configuracao: montarBlocos(edicao.tipo, edicao.emissao, edicao.guiche, edicao.painel),
  };
}

/** The server's reasons, one per broken rule (CustomException errors), else the friendly line. */
function motivos(erro: unknown): string[] {
  if (erro instanceof ApiRequestError && Array.isArray(erro.problem?.errors) && erro.problem.errors.length > 0) {
    return erro.problem.errors;
  }
  return [mensagemAmigavel(erro)];
}

function Campo({ rotulo, children }: { rotulo: string; children: (id: string) => ReactNode }) {
  const id = useId();
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{rotulo}</Label>
      {children(id)}
    </div>
  );
}

function Lista({
  titulo,
  itens,
  selecionados,
  onChange,
}: {
  titulo: string;
  itens: { id: string; nome: string; ativa: boolean }[];
  selecionados: string[];
  onChange: (ids: string[]) => void;
}) {
  const nome = (id: string) => itens.find((i) => i.id === id)?.nome ?? id;
  return (
    <fieldset className="space-y-2" data-lista={titulo}>
      <legend className="text-sm font-medium">{titulo}</legend>
      <div className="flex flex-wrap gap-3">
        {itens.map((i) => (
          <label key={i.id} className="flex items-center gap-1 text-sm">
            <input type="checkbox" checked={selecionados.includes(i.id)} onChange={() => onChange(alternar(selecionados, i.id))} />
            {i.nome}
            {!i.ativa && " (inativa)"}
          </label>
        ))}
      </div>
      {selecionados.length > 0 && (
        <ol className="space-y-1 text-sm">
          {selecionados.map((id, i) => (
            <li key={id} className="flex items-center gap-2">
              <span>
                {i + 1}. {nome(id)}
              </span>
              <Button size="sm" variant="outline" aria-label={`Subir ${nome(id)}`} disabled={i === 0} onClick={() => onChange(mover(selecionados, id, -1))}>
                ↑
              </Button>
              <Button
                size="sm"
                variant="outline"
                aria-label={`Descer ${nome(id)}`}
                disabled={i === selecionados.length - 1}
                onClick={() => onChange(mover(selecionados, id, 1))}
              >
                ↓
              </Button>
            </li>
          ))}
        </ol>
      )}
    </fieldset>
  );
}

function Avisos({ avisos }: { avisos: string[] }) {
  if (avisos.length === 0) return null;
  return (
    <ul role="status" className="list-disc pl-5 text-sm text-[var(--color-destructive)]">
      {avisos.map((a) => (
        <li key={a}>{a}</li>
      ))}
    </ul>
  );
}

export function EstacoesPage() {
  const queryClient = useQueryClient();
  const estacoes = useQuery({ queryKey: CHAVE_ESTACOES, queryFn: listarEstacoes });
  const filas = useQuery({ queryKey: ["paf", "filas"], queryFn: listarFilas });
  const prioridades = useQuery({ queryKey: ["paf", "prioridades"], queryFn: listarPrioridades });

  const [edicao, setEdicao] = useState<Edicao | null>(null);
  const [erros, setErros] = useState<string[]>([]);
  const [aviso, setAviso] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [desativando, setDesativando] = useState<EstacaoDto | null>(null);
  const { user, permissionsHydrated } = useAuth();
  const podeLiberar = permissionsHydrated && (user?.permissions.includes(PERMISSAO_LIBERAR_GUICHE) ?? false);

  const liberar = async (e: EstacaoDto) => {
    setOcupado(true);
    setErros([]);
    setAviso(null);
    try {
      await liberarGuiche(e.id);
      setAviso(`Guichê ${e.nome} liberado.`);
    } catch (erro) {
      // The errors list lives inside the edit form; the list-level notice is where this row's outcome shows.
      setAviso(mensagemAmigavel(erro));
    } finally {
      setOcupado(false);
    }
  };

  const alterar = (parcial: Partial<Edicao>) => setEdicao((atual) => (atual ? { ...atual, ...parcial } : atual));
  const emissao = (parcial: Partial<EmissaoForm>) => setEdicao((a) => (a ? { ...a, emissao: { ...a.emissao, ...parcial } } : a));
  const guiche = (parcial: Partial<GuicheForm>) => setEdicao((a) => (a ? { ...a, guiche: { ...a.guiche, ...parcial } } : a));
  const painel = (parcial: Partial<PainelForm>) => setEdicao((a) => (a ? { ...a, painel: { ...a.painel, ...parcial } } : a));

  const salvar = async () => {
    if (!edicao) return;
    setOcupado(true);
    setErros([]);
    setAviso(null);
    try {
      const corpo = corpoDe(edicao);
      if (edicao.id === null) {
        await criarEstacao(edicao.tipo, corpo);
      } else {
        const alterada = await atualizarEstacao(edicao.id, corpo);
        if (alterada.combinacoesSemCobertura.length > 0) {
          setAviso(
            `Sem guichê depois desta alteração (saem do totem): ${alterada.combinacoesSemCobertura
              .map((c) => `${c.fila}/${c.prioridade} (${c.letra})`)
              .join(", ")}.`,
          );
        }
      }
      await queryClient.invalidateQueries({ queryKey: CHAVE_ESTACOES });
      setEdicao(null);
    } catch (e) {
      setErros(motivos(e));
    } finally {
      setOcupado(false);
    }
  };

  const papeis = edicao ? papeisDoTipo(edicao.tipo) : [];
  const itensFila = filas.data ?? [];
  const itensPrioridade = prioridades.data ?? [];

  return (
    <div className="mx-auto max-w-5xl space-y-6">
      <header className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-2xl font-semibold">Estações</h1>
        {!edicao && (
          <Button
            onClick={() => {
              setErros([]);
              setEdicao(novaEdicao());
            }}
          >
            Nova estação
          </Button>
        )}
      </header>

      {aviso && (
        <p role="status" data-aviso-cobertura className="rounded-md border border-[var(--color-border)] px-4 py-3 text-sm">
          {aviso}
        </p>
      )}

      {edicao && (
        <section className="space-y-5 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-edicao-estacao>
          <h2 className="font-medium">{edicao.id === null ? "Nova estação" : `Editar ${edicao.nome}`}</h2>

          {erros.length > 0 && (
            <ul role="alert" className="list-disc rounded-md border border-[var(--color-destructive)] py-3 pl-8 pr-4 text-[var(--color-destructive)]">
              {erros.map((e) => (
                <li key={e}>{e}</li>
              ))}
            </ul>
          )}

          <div className="grid gap-3 sm:grid-cols-3">
            <Campo rotulo="Nome">
              {(id) => <Input id={id} value={edicao.nome} maxLength={120} onChange={(e) => alterar({ nome: e.target.value })} />}
            </Campo>
            <Campo rotulo="Tipo">
              {(id) => (
                <select
                  id={id}
                  className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-2"
                  value={edicao.tipo}
                  disabled={edicao.id !== null}
                  onChange={(e) => alterar({ tipo: e.target.value as TipoEstacao })}
                >
                  {TIPOS_ESTACAO.map((t) => (
                    <option key={t} value={t}>
                      {ROTULO_TIPO[t]}
                    </option>
                  ))}
                </select>
              )}
            </Campo>
            <Campo rotulo="Ramal">
              {(id) => <Input id={id} value={edicao.ramal} maxLength={20} onChange={(e) => alterar({ ramal: e.target.value })} />}
            </Campo>
          </div>
          {edicao.id !== null && (
            <p className="text-sm text-[var(--color-muted-foreground)]">O tipo não muda depois de criado: para outro tipo, cadastre uma estação nova.</p>
          )}

          {papeis.includes("Emissao") && (
            <fieldset className="space-y-3 rounded-lg border border-[var(--color-border)] p-4" data-bloco="Emissao">
              <legend className="px-1 font-medium">{ROTULO_PAPEL.Emissao}</legend>
              <Lista titulo="Filas a atender" itens={itensFila} selecionados={edicao.filas.Emissao} onChange={(ids) => alterar({ filas: { ...edicao.filas, Emissao: ids } })} />
              <div className="grid gap-3 sm:grid-cols-3">
                <Campo rotulo="Título da tela">
                  {(id) => <Input id={id} value={edicao.emissao.titulo} maxLength={120} onChange={(e) => emissao({ titulo: e.target.value })} />}
                </Campo>
                <Campo rotulo="Fonte (px)">
                  {(id) => <Input id={id} inputMode="numeric" value={edicao.emissao.tamanhoFontePx} onChange={(e) => emissao({ tamanhoFontePx: e.target.value })} />}
                </Campo>
                <Campo rotulo={`Botão (px, mínimo ${ALVO_MINIMO_BOTAO_PX})`}>
                  {(id) => <Input id={id} inputMode="numeric" value={edicao.emissao.tamanhoBotaoPx} onChange={(e) => emissao({ tamanhoBotaoPx: e.target.value })} />}
                </Campo>
                <Campo rotulo="Cor de fundo dos botões">
                  {(id) => <Input id={id} placeholder="#RRGGBB" value={edicao.emissao.corFundoBotao} onChange={(e) => emissao({ corFundoBotao: e.target.value })} />}
                </Campo>
                <Campo rotulo="Cor da fonte dos botões">
                  {(id) => <Input id={id} placeholder="#RRGGBB" value={edicao.emissao.corFonteBotao} onChange={(e) => emissao({ corFonteBotao: e.target.value })} />}
                </Campo>
                <Campo rotulo="Cor da descrição dos botões">
                  {(id) => <Input id={id} placeholder="#RRGGBB" value={edicao.emissao.corDescricaoBotao} onChange={(e) => emissao({ corDescricaoBotao: e.target.value })} />}
                </Campo>
                <Campo rotulo={`Prazo do ciclo de emissão (s, ${TEMPO_MINIMO_S} a ${TEMPO_MAXIMO_S})`}>
                  {(id) => (
                    <Input id={id} inputMode="numeric" value={edicao.emissao.prazoCicloEmissaoSegundos} onChange={(e) => emissao({ prazoCicloEmissaoSegundos: e.target.value })} />
                  )}
                </Campo>
              </div>
              <Avisos
                avisos={avisosDeCor(["Fundo", edicao.emissao.corFundoBotao], [
                  ["Fonte", edicao.emissao.corFonteBotao],
                  ["Descrição", edicao.emissao.corDescricaoBotao],
                ])}
              />
            </fieldset>
          )}

          {papeis.includes("Guiche") && (
            <fieldset className="space-y-3 rounded-lg border border-[var(--color-border)] p-4" data-bloco="Guiche">
              <legend className="px-1 font-medium">{ROTULO_PAPEL.Guiche}</legend>
              <div className="grid gap-3 sm:grid-cols-3">
                <Campo rotulo="Número do guichê">
                  {(id) => <Input id={id} inputMode="numeric" value={edicao.guiche.numero} onChange={(e) => guiche({ numero: e.target.value })} />}
                </Campo>
                <Campo rotulo="Nome de exibição">
                  {(id) => <Input id={id} value={edicao.guiche.nomeExibicao} maxLength={60} onChange={(e) => guiche({ nomeExibicao: e.target.value })} />}
                </Campo>
                <Campo rotulo="Lado da seta">
                  {(id) => (
                    <select
                      id={id}
                      className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-2"
                      value={edicao.guiche.ladoSeta}
                      onChange={(e) => guiche({ ladoSeta: e.target.value as GuicheForm["ladoSeta"] })}
                    >
                      <option value="">Sem seta</option>
                      <option value="Esquerda">Esquerda</option>
                      <option value="Direita">Direita</option>
                    </select>
                  )}
                </Campo>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={edicao.guiche.respeitarOrdemFilas} onChange={(e) => guiche({ respeitarOrdemFilas: e.target.checked })} />
                Respeitar ordem das filas
              </label>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={edicao.guiche.selecionarFicha} onChange={(e) => guiche({ selecionarFicha: e.target.checked })} />
                Selecionar ficha para atendimento
              </label>
              <Lista titulo="Filas a atender" itens={itensFila} selecionados={edicao.filas.Guiche} onChange={(ids) => alterar({ filas: { ...edicao.filas, Guiche: ids } })} />
              <Lista titulo="Prioridades de atendimento" itens={itensPrioridade} selecionados={edicao.prioridades} onChange={(ids) => alterar({ prioridades: ids })} />
            </fieldset>
          )}

          {papeis.includes("Painel") && (
            <fieldset className="space-y-3 rounded-lg border border-[var(--color-border)] p-4" data-bloco="Painel">
              <legend className="px-1 font-medium">{ROTULO_PAPEL.Painel}</legend>
              <Lista titulo="Filas a chamar" itens={itensFila} selecionados={edicao.filas.Painel} onChange={(ids) => alterar({ filas: { ...edicao.filas, Painel: ids } })} />
              <div className="grid gap-3 sm:grid-cols-3">
                <Campo rotulo={`Tempo de exibição da chamada (s, ${TEMPO_MINIMO_S} a ${TEMPO_MAXIMO_S})`}>
                  {(id) => (
                    <Input id={id} inputMode="numeric" value={edicao.painel.tempoExibicaoChamadaSegundos} onChange={(e) => painel({ tempoExibicaoChamadaSegundos: e.target.value })} />
                  )}
                </Campo>
                <Campo rotulo="Fonte (px)">
                  {(id) => <Input id={id} inputMode="numeric" value={edicao.painel.tamanhoFontePx} onChange={(e) => painel({ tamanhoFontePx: e.target.value })} />}
                </Campo>
                <Campo rotulo="Modo">
                  {(id) => (
                    <select
                      id={id}
                      className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-2"
                      value={edicao.painel.modo}
                      onChange={(e) => painel({ modo: e.target.value as PainelForm["modo"] })}
                    >
                      <option value="">Padrão</option>
                      <option value="Normal">Normal</option>
                      <option value="Informativo">Informativo</option>
                    </select>
                  )}
                </Campo>
                <Campo rotulo="Cor de fundo">
                  {(id) => <Input id={id} placeholder="#RRGGBB" value={edicao.painel.corFundo} onChange={(e) => painel({ corFundo: e.target.value })} />}
                </Campo>
                <Campo rotulo="Cor da fonte das filas">
                  {(id) => <Input id={id} placeholder="#RRGGBB" value={edicao.painel.corFonteFilas} onChange={(e) => painel({ corFonteFilas: e.target.value })} />}
                </Campo>
                <Campo rotulo="Cor da fonte da chamada">
                  {(id) => <Input id={id} placeholder="#RRGGBB" value={edicao.painel.corFonteChamada} onChange={(e) => painel({ corFonteChamada: e.target.value })} />}
                </Campo>
                <Campo rotulo="Cor da fonte alternada">
                  {(id) => <Input id={id} placeholder="#RRGGBB" value={edicao.painel.corFonteAlternada} onChange={(e) => painel({ corFonteAlternada: e.target.value })} />}
                </Campo>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={edicao.painel.mostrarDataHora} onChange={(e) => painel({ mostrarDataHora: e.target.checked })} />
                Mostrar data e hora
              </label>
              <Avisos
                avisos={avisosDeCor(["Fundo", edicao.painel.corFundo], [
                  ["Fonte das filas", edicao.painel.corFonteFilas],
                  ["Fonte da chamada", edicao.painel.corFonteChamada],
                  ["Fonte alternada", edicao.painel.corFonteAlternada],
                ])}
              />
            </fieldset>
          )}

          <div className="flex gap-2">
            <Button disabled={ocupado || edicao.nome.trim() === ""} onClick={() => void salvar()}>
              Salvar
            </Button>
            <Button variant="outline" disabled={ocupado} onClick={() => setEdicao(null)}>
              Cancelar
            </Button>
          </div>
        </section>
      )}

      {estacoes.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {estacoes.isError && <p role="alert">{mensagemAmigavel(estacoes.error)}</p>}
      {estacoes.data?.length === 0 && <p>Nenhuma estação nesta unidade.</p>}

      <ul className="space-y-2">
        {(estacoes.data ?? []).map((e) => (
          <li key={e.id} data-estacao={e.id} className="flex flex-wrap items-center gap-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4">
            <span className="font-medium">{e.nome}</span>
            <span className="text-sm text-[var(--color-muted-foreground)]">{ROTULO_TIPO[e.tipo]}</span>
            {e.configuracao?.guiche && <span className="text-sm">Guichê {e.configuracao.guiche.numero}</span>}
            {e.ramal && <span className="text-sm">Ramal {e.ramal}</span>}
            {!e.ativa && <span className="text-sm text-[var(--color-destructive)]">Inativa</span>}
            <Button
              size="sm"
              variant="outline"
              className="ml-auto"
              disabled={ocupado}
              onClick={() => {
                setErros([]);
                setEdicao(edicaoDe(e));
              }}
            >
              Editar
            </Button>
            {e.ativa && podeLiberar && papeisDoTipo(e.tipo).includes("Guiche") && (
              <Button size="sm" variant="outline" disabled={ocupado} onClick={() => void liberar(e)}>
                Liberar guichê
              </Button>
            )}
            {e.ativa && (
              <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setDesativando(e)}>
                Desativar
              </Button>
            )}
          </li>
        ))}
      </ul>

      {desativando && (
        <DialogoDesativacao
          estacao={desativando}
          onFechar={() => setDesativando(null)}
          onDesativada={async (avisoDesativacao) => {
            setErros([]);
            setAviso(avisoDesativacao ?? `Estação ${desativando.nome} desativada.`);
            setDesativando(null);
            await queryClient.invalidateQueries({ queryKey: CHAVE_ESTACOES });
          }}
        />
      )}
    </div>
  );
}
