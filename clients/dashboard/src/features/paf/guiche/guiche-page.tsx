import { useEffect, useId, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  chamarProxima as chamarProximaApi,
  definirGuiche,
  definirUnidadeCorrente,
  listarGuiches,
  listarUnidades,
  obterPosto,
  operarAtendimento,
  type GuicheDto,
  type OperacaoAtendimento,
  type PostoDto,
} from "@/features/paf/guiche/guiche-api";
import { conectarHubGuiche, type HubGuiche } from "@/features/paf/guiche/hub-guiche";
import {
  chamarProxima,
  encerrarEChamar,
  RecusaGuiche,
  type AtendimentoNaTela,
  type PassosGuiche,
} from "@/features/paf/guiche/guiche-fluxo";
import type { EstadoConexao } from "@/features/paf/paf-hub";

type Ultimo = { senha: string; estado: string };

/** What SenhaChamada, SenhaRechamada and AtendimentoAtualizado share, as PafHub sends them (camelCase). */
type AvisoAtendimento = { atendimentoId: string; versao: number };

// The server holds unit, guichê and atendimento (GET posto): a reload asks it again. Only the step of a
// "Trocar" in progress lives here, because until the new choice is made the server still has the old one.
type Etapa = "unidade" | "guiche" | "operar";

const CHAVE_POSTO = ["paf", "guiche", "posto"] as const;

const ROTULO_ESTADO: Record<string, string> = {
  Chamado: "Chamada",
  EmAtendimento: "Em atendimento",
  Encerrado: "Encerrado",
  Cancelado: "Cancelado",
};

const ESTADOS_FINAIS = new Set(["Encerrado", "Cancelado"]);

const ROTULO_CONEXAO: Record<EstadoConexao, string> = {
  conectando: "Conectando ao tempo real…",
  conectado: "",
  reconectando: "Tempo real reconectando…",
  desconectado: "Tempo real desconectado.",
};

const SELECT_CLASSES =
  "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

function rotuloGuiche(g: GuicheDto): string {
  if (g.meu) return `${g.nome} (seu)`;
  if (g.ocupante) return `${g.nome} (em uso por ${g.ocupante})`;
  return g.pendencia ? `${g.nome} (senha ${g.pendencia} pendente de outro atendente)` : g.nome;
}

export function GuichePage() {
  const queryClient = useQueryClient();
  const [troca, setTroca] = useState<Etapa | null>(null);
  const [ultimo, setUltimo] = useState<Ultimo | null>(null);
  const [unidadeEscolhida, setUnidadeEscolhida] = useState("");
  const [guicheEscolhido, setGuicheEscolhido] = useState("");
  const [ocupado, setOcupado] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [conexao, setConexao] = useState<EstadoConexao>("conectando");
  const hub = useRef<HubGuiche | null>(null);
  const idUnidade = useId();
  const idGuiche = useId();

  const posto = useQuery({ queryKey: CHAVE_POSTO, queryFn: obterPosto });
  const etapa: Etapa | null = posto.data
    ? (troca ?? (posto.data.unidadeId === null ? "unidade" : posto.data.estacaoId === null ? "guiche" : "operar"))
    : null;

  useEffect(() => {
    // A notice only matters for the atendimento on screen with a newer Versao (seção 7): the server's post is
    // re-read, so a change made elsewhere (manager cancel, another window) reaches the screen.
    const aoAviso = (aviso: AvisoAtendimento, estado: string | null) => {
      const atual = queryClient.getQueryData<PostoDto>(CHAVE_POSTO)?.atendimento;
      if (atual?.atendimentoId !== aviso.atendimentoId || aviso.versao <= atual.versao) return;
      if (estado !== null && ESTADOS_FINAIS.has(estado)) setUltimo({ senha: atual.senha, estado });
      void queryClient.invalidateQueries({ queryKey: CHAVE_POSTO });
    };
    const atual = conectarHubGuiche({
      handlers: {
        SenhaChamada: (p) => aoAviso(p as AvisoAtendimento, null),
        SenhaRechamada: (p) => aoAviso(p as AvisoAtendimento, null),
        AtendimentoAtualizado: (p) => {
          const aviso = p as AvisoAtendimento & { estado: string };
          aoAviso(aviso, aviso.estado);
        },
      },
      aoMudarEstado: setConexao,
    });
    hub.current = atual;
    return () => {
      hub.current = null;
      atual.parar();
    };
  }, [queryClient]);

  const unidades = useQuery({ queryKey: ["paf", "guiche", "unidades"], queryFn: listarUnidades, enabled: etapa === "unidade" });
  const guiches = useQuery({
    queryKey: ["paf", "guiche", "guiches", posto.data?.unidadeId],
    queryFn: listarGuiches,
    enabled: etapa === "guiche",
  });

  const executar = async (acao: () => Promise<void>) => {
    setOcupado(true);
    setErro(null);
    try {
      await acao();
    } catch (e) {
      setErro(e instanceof RecusaGuiche ? e.message : mensagemAmigavel(e));
    } finally {
      // Success or refusal, the server's post is the truth: a guichê released elsewhere sends the screen
      // back to the selection on its own.
      await queryClient.invalidateQueries({ queryKey: CHAVE_POSTO });
      setOcupado(false);
    }
  };

  const escolherUnidade = () =>
    executar(async () => {
      if (unidadeEscolhida === "") return;
      await definirUnidadeCorrente(unidadeEscolhida);
      setUltimo(null);
      setTroca("guiche");
    });

  // Another user's pendência on the chosen guichê is released only by an explicit choice (RF04): the button says so.
  const pendenciaEscolhida = guiches.data?.find((g) => g.id === guicheEscolhido)?.pendencia ?? null;

  const escolherGuiche = () =>
    executar(async () => {
      if (guicheEscolhido === "") return;
      await definirGuiche(guicheEscolhido, pendenciaEscolhida !== null);
      hub.current?.entrarNoGuiche();
      setTroca(null);
    });

  const operarNaTela = async (atual: AtendimentoNaTela, operacao: OperacaoAtendimento) => {
    const alterado = await operarAtendimento(atual.atendimentoId, operacao);
    if (ESTADOS_FINAIS.has(alterado.estado)) setUltimo({ senha: atual.senha, estado: alterado.estado });
  };

  // The first step records its outcome before the call, so an empty queue on the second step still shows it.
  const passos: PassosGuiche = {
    confirmar: (pergunta) => window.confirm(pergunta),
    operar: operarNaTela,
    chamar: chamarProximaApi,
  };

  const chamar = () =>
    executar(async () => {
      await chamarProxima(posto.data?.atendimento ?? null, passos);
    });

  const encerrarEChamarProxima = () =>
    executar(async () => {
      await encerrarEChamar(posto.data?.atendimento ?? null, passos);
    });

  const operar = (operacao: OperacaoAtendimento) =>
    executar(async () => {
      const atual = posto.data?.atendimento;
      if (atual) await operarNaTela(atual, operacao);
    });

  const trocarPosto = () => {
    setErro(null);
    setUnidadeEscolhida("");
    setGuicheEscolhido("");
    setTroca("unidade");
  };

  const atendimento = posto.data?.atendimento ?? null;

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Guichê</h1>
        {posto.data?.unidade && (
          <p className="text-sm text-[var(--color-muted-foreground)]">
            {posto.data.unidade}
            {posto.data.guiche && ` · ${posto.data.guiche}`}{" "}
            {etapa !== "unidade" && (
              <Button variant="link" size="xs" onClick={trocarPosto}>
                Trocar
              </Button>
            )}
          </p>
        )}
      </header>

      {posto.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {posto.isError && <p role="alert">{mensagemAmigavel(posto.error)}</p>}
      {ROTULO_CONEXAO[conexao] && (
        <p role="status" className="text-sm text-[var(--color-muted-foreground)]">
          {ROTULO_CONEXAO[conexao]}
        </p>
      )}

      {erro && (
        <p role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erro}
        </p>
      )}

      {etapa === "unidade" && (
        <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5">
          <Label htmlFor={idUnidade}>Unidade</Label>
          {unidades.isError && <p role="alert">{mensagemAmigavel(unidades.error)}</p>}
          <select
            id={idUnidade}
            className={SELECT_CLASSES}
            value={unidadeEscolhida}
            onChange={(e) => setUnidadeEscolhida(e.target.value)}
          >
            <option value="">{unidades.isPending ? "Carregando…" : "Selecione a unidade"}</option>
            {(unidades.data ?? []).map((u) => (
              <option key={u.id} value={u.id}>
                {u.nome}
              </option>
            ))}
          </select>
          <Button disabled={ocupado || unidadeEscolhida === ""} onClick={() => void escolherUnidade()}>
            Usar esta unidade
          </Button>
        </section>
      )}

      {etapa === "guiche" && (
        <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5">
          <Label htmlFor={idGuiche}>Guichê</Label>
          {guiches.isError && <p role="alert">{mensagemAmigavel(guiches.error)}</p>}
          {guiches.data?.length === 0 && <p>Nenhum guichê ativo nesta unidade.</p>}
          <select
            id={idGuiche}
            className={SELECT_CLASSES}
            value={guicheEscolhido}
            onChange={(e) => setGuicheEscolhido(e.target.value)}
          >
            <option value="">{guiches.isPending ? "Carregando…" : "Selecione o guichê"}</option>
            {(guiches.data ?? []).map((g) => (
              <option key={g.id} value={g.id}>
                {rotuloGuiche(g)}
              </option>
            ))}
          </select>
          {pendenciaEscolhida && (
            <p className="text-sm text-[var(--color-muted-foreground)]">
              A senha {pendenciaEscolhida} ficou chamada ou em atendimento neste guichê por outro atendente. Usar o
              guichê cancela esse atendimento.
            </p>
          )}
          <Button
            variant={pendenciaEscolhida ? "destructive" : "default"}
            disabled={ocupado || guicheEscolhido === ""}
            onClick={() => void escolherGuiche()}
          >
            {pendenciaEscolhida ? `Liberar a senha ${pendenciaEscolhida} e usar este guichê` : "Usar este guichê"}
          </Button>
        </section>
      )}

      {etapa === "operar" && (
        <section className="space-y-5 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5">
          <div aria-live="polite" className="text-center">
            {atendimento ? (
              <>
                <div className="text-sm text-[var(--color-muted-foreground)]">Senha atual</div>
                <div className="text-6xl font-bold" data-senha-guiche>
                  {atendimento.senha}
                </div>
                <div data-estado-guiche>{ROTULO_ESTADO[atendimento.estado] ?? atendimento.estado}</div>
              </>
            ) : (
              <p className="text-[var(--color-muted-foreground)]">
                Nenhum atendimento em curso.
                {ultimo && ` Último: ${ultimo.senha} (${ROTULO_ESTADO[ultimo.estado] ?? ultimo.estado}).`}
              </p>
            )}
          </div>

          <div className="flex flex-wrap justify-center gap-3">
            <Button size="lg" disabled={ocupado} onClick={() => void chamar()}>
              Chamar próxima
            </Button>
            <Button
              size="lg"
              variant="outline"
              disabled={ocupado || atendimento?.estado !== "Chamado"}
              onClick={() => void operar("rechamar")}
            >
              Chamar novamente
            </Button>
            <Button
              size="lg"
              variant="secondary"
              disabled={ocupado || atendimento?.estado !== "Chamado"}
              onClick={() => void operar("iniciar")}
            >
              Iniciar atendimento
            </Button>
            <Button
              size="lg"
              variant="secondary"
              disabled={ocupado || atendimento?.estado !== "EmAtendimento"}
              onClick={() => void operar("encerrar")}
            >
              Encerrar atendimento
            </Button>
            <Button size="lg" variant="secondary" disabled={ocupado} onClick={() => void encerrarEChamarProxima()}>
              Encerrar e chamar
            </Button>
            <Button
              size="lg"
              variant="destructive"
              disabled={ocupado || atendimento?.estado !== "Chamado"}
              onClick={() => void operar("cancelar")}
            >
              Cancelar (não compareceu)
            </Button>
          </div>
        </section>
      )}
    </div>
  );
}
