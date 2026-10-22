import { useId, useLayoutEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useAuth } from "@/auth/use-auth";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { FichaImpressa } from "@/features/paf/totem/ficha-impressa";
import { ApiRequestError } from "@/lib/api-client";
import { obterPosto, operarAtendimento } from "@/features/paf/guiche/guiche-api";
import { chamarProxima, type AtendimentoNaTela } from "@/features/paf/guiche/guiche-fluxo";
import { chamarFichaEscolhida, listarFichasEmEspera } from "@/features/paf/fichas-do-dia/fichas-do-dia-api";
import {
  passosParaEscolhida,
  PERMISSAO_SELECIONAR_FICHA,
  posicaoNaChamada,
  selecaoIndisponivel,
} from "@/features/paf/fichas-do-dia/selecionar-ficha";
import { ajustarFonteSenha, definirPagina, imprimir, removerPagina } from "@/features/paf/totem/impressao";
import {
  cancelarAtendimento,
  listarFichasDoDia,
  obterImpressao,
  reabilitarAtendimento,
  type AtendimentoDoDiaDto,
} from "@/features/paf/fichas-do-dia/fichas-do-dia-api";
import {
  acoesDoAtendimento,
  descricaoGuiche,
  ESTADOS,
  formatarEspera,
  rotuloEstado,
  type Estado,
  type FiltroFichasDoDia,
} from "@/features/paf/fichas-do-dia/fichas-do-dia-regras";

// The print dialog blocks without --kiosk-printing; past this the screen frees the reprint button again.
const LIMITE_IMPRESSAO_MS = 60_000;

const formatoHora = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

export function FichasDoDiaPage() {
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const permissoes = user?.permissions ?? [];
  const [filtro, setFiltro] = useState<FiltroFichasDoDia>({ data: null, filaId: null, estado: null });
  const chave = ["paf", "fichas-do-dia", filtro] as const;
  // ponytail: no realtime here; the operator refreshes. Subscribe to FilaAtualizada/AtendimentoAtualizado if asked.
  const consulta = useQuery({ queryKey: chave, queryFn: () => listarFichasDoDia(filtro) });

  // F21: "Chamar esta" exists only on the session's guichê with the selecionarFicha option (REQ seção 4). No retry:
  // the 409 of an option that is off is an answer, not a transient failure.
  const podeSelecionar = permissoes.includes(PERMISSAO_SELECIONAR_FICHA);
  const posto = useQuery({ queryKey: ["paf", "guiche", "posto"], queryFn: obterPosto, enabled: podeSelecionar, retry: false });
  const estacaoDoPosto = posto.data?.estacaoId ?? null;
  const emEspera = useQuery({
    queryKey: ["paf", "fichas-do-dia", "em-espera", estacaoDoPosto],
    queryFn: listarFichasEmEspera,
    enabled: podeSelecionar && estacaoDoPosto !== null,
    retry: false,
  });
  const posicoes = posicaoNaChamada(emEspera.data ?? []);
  const falhaSelecao = posto.error ?? emEspera.error;
  const mostrarFalhaSelecao =
    falhaSelecao !== null && !selecaoIndisponivel(falhaSelecao instanceof ApiRequestError ? falhaSelecao.status : null);
  const [aviso, setAviso] = useState<string | null>(null);

  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [paraImprimir, setParaImprimir] = useState<FichaEmitidaDto | null>(null);
  const fichaRef = useRef<HTMLDivElement>(null);
  const idData = useId();
  const idFila = useId();
  const idEstado = useId();

  const executar = async (acao: () => Promise<unknown>) => {
    setOcupado(true);
    setErro(null);
    try {
      await acao();
    } catch (e) {
      setErro(mensagemAmigavel(e));
    } finally {
      setOcupado(false);
      // Refetch after a refusal too: a 409 usually means the line changed under the operator.
      await queryClient.invalidateQueries({ queryKey: ["paf", "fichas-do-dia"] });
      await queryClient.invalidateQueries({ queryKey: ["paf", "guiche", "posto"] });
    }
  };

  const cancelar = (linha: AtendimentoDoDiaDto) => {
    if (!window.confirm(`Cancelar o atendimento da senha ${linha.senha} na fila ${linha.fila}?`)) return;
    void executar(() => cancelarAtendimento(linha.atendimentoId));
  };

  // RF15 questions through F13's composition; the guichê's atendimento is read fresh at click time, so a question
  // never rests on a stale screen. A 409 from another guichê lands in `erro` and the refetch drops the line.
  const chamarEsta = (linha: AtendimentoDoDiaDto) => {
    setAviso(null);
    void executar(async () => {
      const atual: AtendimentoNaTela | null = (await obterPosto()).atendimento;
      await chamarProxima(
        atual,
        passosParaEscolhida<AtendimentoNaTela>(linha.atendimentoId, {
          confirmar: (pergunta) => window.confirm(pergunta),
          operar: (emCurso, operacao) => operarAtendimento(emCurso.atendimentoId, operacao),
          chamarEscolhida: async (atendimentoId) => {
            const chamada = await chamarFichaEscolhida(atendimentoId);
            setAviso(`Senha ${chamada.senha} chamada neste guichê.`);
          },
        }),
      );
    });
  };

  const reimprimir = async (linha: AtendimentoDoDiaDto) => {
    setOcupado(true);
    setErro(null);
    try {
      setParaImprimir(await obterImpressao(linha.fichaId));
    } catch (e) {
      setErro(mensagemAmigavel(e));
      setOcupado(false);
    }
  };

  // Same measuring as the totem: the senha font and the page height depend on the rendered ticket.
  useLayoutEffect(() => {
    if (paraImprimir === null || fichaRef.current === null) return;
    const senha = fichaRef.current.querySelector<HTMLElement>("[data-senha]");
    if (senha) ajustarFonteSenha(senha);
    definirPagina(fichaRef.current);
    let ativo = true;
    const concluir = () => {
      removerPagina();
      if (!ativo) return;
      setParaImprimir(null);
      setOcupado(false);
    };
    void imprimir(LIMITE_IMPRESSAO_MS).then(concluir, (falha: unknown) => {
      console.warn("[paf-fichas-do-dia] falha ao imprimir a ficha", falha);
      concluir();
    });
    return () => {
      ativo = false;
    };
  }, [paraImprimir]);

  const dados = consulta.data;

  return (
    <div className="mx-auto max-w-6xl space-y-6">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-2xl font-semibold">Fichas do dia</h1>
        <Button variant="outline" disabled={consulta.isFetching} onClick={() => void consulta.refetch()}>
          {consulta.isFetching ? "Atualizando…" : "Atualizar"}
        </Button>
      </header>

      <section className="grid gap-3 sm:grid-cols-3" aria-label="Filtros">
        <div className="space-y-1">
          <Label htmlFor={idData}>Data</Label>
          <Input
            id={idData}
            type="date"
            value={filtro.data ?? dados?.data ?? ""}
            onChange={(e) => setFiltro({ ...filtro, data: e.target.value === "" ? null : e.target.value })}
          />
        </div>
        <div className="space-y-1">
          <Label htmlFor={idFila}>Fila</Label>
          <select
            id={idFila}
            className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-2 text-sm"
            value={filtro.filaId ?? ""}
            onChange={(e) => setFiltro({ ...filtro, filaId: e.target.value === "" ? null : e.target.value })}
          >
            <option value="">Todas</option>
            {(dados?.filas ?? []).map((f) => (
              <option key={f.id} value={f.id}>
                {f.ativa ? f.nomeExibicao : `${f.nomeExibicao} (inativa)`}
              </option>
            ))}
          </select>
        </div>
        <div className="space-y-1">
          <Label htmlFor={idEstado}>Status</Label>
          <select
            id={idEstado}
            className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-2 text-sm"
            value={filtro.estado ?? ""}
            onChange={(e) => setFiltro({ ...filtro, estado: e.target.value === "" ? null : (e.target.value as Estado) })}
          >
            <option value="">Todos</option>
            {ESTADOS.map((estado) => (
              <option key={estado} value={estado}>
                {rotuloEstado(estado)}
              </option>
            ))}
          </select>
        </div>
      </section>

      {erro && (
        <p role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erro}
        </p>
      )}

      {aviso && (
        <p role="status" data-aviso-selecao className="rounded-md border border-[var(--color-border)] px-4 py-3">
          {aviso}
        </p>
      )}
      {mostrarFalhaSelecao && <p role="alert">{mensagemAmigavel(falhaSelecao)}</p>}
      {emEspera.data && (
        <p data-selecao-ficha className="text-sm text-[var(--color-muted-foreground)]">
          {posto.data?.guiche ?? "Este guichê"} pode chamar uma ficha fora da ordem. O número em "Chamar esta" é a posição dela na
          chamada: a 1ª é a que "Chamar próxima" chamaria.
        </p>
      )}

      {consulta.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {consulta.isError && <p role="alert">{mensagemAmigavel(consulta.error)}</p>}

      {dados && (
        <>
          <p data-total-aguardando className="font-medium">
            Aguardando: {dados.totalAguardando}
          </p>
          {dados.truncada && (
            <p role="note" className="text-sm text-[var(--color-muted-foreground)]">
              Mostrando só as {dados.atendimentos.length} mais recentes. Filtre por fila ou status para ver as demais.
            </p>
          )}
          {dados.atendimentos.length === 0 ? (
            <p>Nenhuma ficha neste filtro.</p>
          ) : (
            <div className="overflow-x-auto rounded-xl border border-[var(--color-border)]">
              <table className="w-full text-sm">
                <thead className="bg-[var(--color-card)] text-left">
                  <tr>
                    <th className="px-3 py-2">Senha</th>
                    <th className="px-3 py-2">Fila</th>
                    <th className="px-3 py-2">Tipo</th>
                    <th className="px-3 py-2">Guichê</th>
                    <th className="px-3 py-2">Chegada</th>
                    <th className="px-3 py-2">Espera</th>
                    <th className="px-3 py-2">Status</th>
                    <th className="px-3 py-2">
                      <span className="sr-only">Ações</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {dados.atendimentos.map((linha) => {
                    const acoes = acoesDoAtendimento(linha.estado, permissoes);
                    const posicao = posicoes.get(linha.atendimentoId);
                    return (
                      <tr key={linha.atendimentoId} data-atendimento={linha.atendimentoId} className="border-t border-[var(--color-border)]">
                        <td className="px-3 py-2 font-semibold">
                          {linha.senha}
                          {linha.encaminhado && <span className="ml-2 text-xs text-[var(--color-muted-foreground)]">encaminhada</span>}
                        </td>
                        <td className="px-3 py-2">{linha.fila}</td>
                        <td className="px-3 py-2">{linha.prioridade}</td>
                        <td className="px-3 py-2">{descricaoGuiche(linha)}</td>
                        <td className="px-3 py-2">{formatoHora.format(new Date(linha.chegadaEm))}</td>
                        <td className="px-3 py-2 tabular-nums">{formatarEspera(linha.esperaSegundos)}</td>
                        <td className="px-3 py-2" data-estado={linha.estado}>
                          {rotuloEstado(linha.estado)}
                        </td>
                        <td className="px-3 py-2">
                          <div className="flex flex-wrap justify-end gap-2">
                            <Button size="sm" variant="outline" disabled={ocupado} onClick={() => void reimprimir(linha)}>
                              Reimprimir
                            </Button>
                            {posicao !== undefined && (
                              <Button size="sm" disabled={ocupado} data-chamar-esta onClick={() => chamarEsta(linha)}>
                                Chamar esta ({posicao}ª)
                              </Button>
                            )}
                            {acoes.cancelar && (
                              <Button size="sm" variant="destructive" disabled={ocupado} onClick={() => cancelar(linha)}>
                                Cancelar
                              </Button>
                            )}
                            {acoes.reabilitar && (
                              <Button
                                size="sm"
                                variant="secondary"
                                disabled={ocupado}
                                onClick={() => void executar(() => reabilitarAtendimento(linha.atendimentoId))}
                              >
                                Reabilitar
                              </Button>
                            )}
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}

      {paraImprimir && <FichaImpressa ref={fichaRef} ficha={paraImprimir} />}
    </div>
  );
}
