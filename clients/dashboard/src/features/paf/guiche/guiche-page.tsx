import { useEffect, useId, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  chamarProxima,
  definirGuiche,
  definirUnidadeCorrente,
  listarGuiches,
  listarUnidades,
  operarAtendimento,
  type OperacaoAtendimento,
} from "@/features/paf/guiche/guiche-api";
import { conectarHubGuiche, type HubGuiche } from "@/features/paf/guiche/hub-guiche";
import type { EstadoConexao } from "@/features/paf/paf-hub";

type Selecionado = { id: string; nome: string };
/** versao: the atendimento's Versao last applied; a notice with a lower or equal one is stale (seção 7). */
type AtendimentoAtual = { atendimentoId: string; senha: string; estado: string; versao: number };

/** What SenhaChamada, SenhaRechamada and AtendimentoAtualizado share, as PafHub sends them (camelCase). */
type AvisoAtendimento = { atendimentoId: string; versao: number };

type Posto = {
  unidade: Selecionado | null;
  guiche: Selecionado | null;
  atendimento: AtendimentoAtual | null;
  ultimo: AtendimentoAtual | null;
};

// ponytail: the server has no read of the session's unit, guichê or atendimento em curso yet, so a
// reload restores them from this tab's sessionStorage. Upgrade: a GET of the session state replaces it.
const CHAVE_POSTO = "paf.guiche.posto";
const POSTO_VAZIO: Posto = { unidade: null, guiche: null, atendimento: null, ultimo: null };

function lerPosto(): Posto {
  const bruto = sessionStorage.getItem(CHAVE_POSTO);
  if (bruto === null) return POSTO_VAZIO;
  try {
    const posto = { ...POSTO_VAZIO, ...(JSON.parse(bruto) as Partial<Posto>) };
    // Entries saved before versao existed: 0 lets the next notice through, the server state wins.
    if (posto.atendimento) posto.atendimento = { ...posto.atendimento, versao: posto.atendimento.versao ?? 0 };
    return posto;
  } catch {
    // Corrupted entry: start from the selection, the server still holds the truth.
    return POSTO_VAZIO;
  }
}

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

function comEstado(posto: Posto, atual: AtendimentoAtual, estado: string, versao: number): Posto {
  const novo = { ...atual, estado, versao };
  return ESTADOS_FINAIS.has(estado) ? { ...posto, atendimento: null, ultimo: novo } : { ...posto, atendimento: novo };
}

// Only the atendimento on screen is touched: a change made elsewhere (manager cancel, another window) reaches
// it, and a duplicate or late notice, with a version already applied, is dropped.
function aplicarAviso(posto: Posto, aviso: AvisoAtendimento, estado: string): Posto {
  const atual = posto.atendimento;
  if (atual?.atendimentoId !== aviso.atendimentoId || aviso.versao <= atual.versao) return posto;
  return comEstado(posto, atual, estado, aviso.versao);
}

const SELECT_CLASSES =
  "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

export function GuichePage() {
  const [posto, setPosto] = useState<Posto>(lerPosto);
  const [unidadeEscolhida, setUnidadeEscolhida] = useState("");
  const [guicheEscolhido, setGuicheEscolhido] = useState("");
  const [ocupado, setOcupado] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [conexao, setConexao] = useState<EstadoConexao>("conectando");
  const hub = useRef<HubGuiche | null>(null);
  const idUnidade = useId();
  const idGuiche = useId();

  useEffect(() => {
    sessionStorage.setItem(CHAVE_POSTO, JSON.stringify(posto));
  }, [posto]);

  useEffect(() => {
    const atual = conectarHubGuiche({
      handlers: {
        SenhaChamada: (p) => setPosto((s) => aplicarAviso(s, p as AvisoAtendimento, "Chamado")),
        SenhaRechamada: (p) => setPosto((s) => aplicarAviso(s, p as AvisoAtendimento, "Chamado")),
        AtendimentoAtualizado: (p) => {
          const aviso = p as AvisoAtendimento & { estado: string };
          setPosto((s) => aplicarAviso(s, aviso, aviso.estado));
        },
      },
      aoMudarEstado: setConexao,
    });
    hub.current = atual;
    return () => {
      hub.current = null;
      atual.parar();
    };
  }, []);

  const unidades = useQuery({ queryKey: ["paf", "guiche", "unidades"], queryFn: listarUnidades, enabled: posto.unidade === null });
  const guiches = useQuery({
    queryKey: ["paf", "guiche", "guiches", posto.unidade?.id],
    queryFn: listarGuiches,
    enabled: posto.unidade !== null && posto.guiche === null,
  });

  const executar = async (acao: () => Promise<void>) => {
    setOcupado(true);
    setErro(null);
    try {
      await acao();
    } catch (e) {
      setErro(mensagemAmigavel(e));
    } finally {
      setOcupado(false);
    }
  };

  const escolherUnidade = () =>
    executar(async () => {
      const unidade = unidades.data?.find((u) => u.id === unidadeEscolhida);
      if (!unidade) return;
      await definirUnidadeCorrente(unidade.id);
      setPosto({ ...POSTO_VAZIO, unidade: { id: unidade.id, nome: unidade.nome } });
    });

  const escolherGuiche = () =>
    executar(async () => {
      const guiche = guiches.data?.find((g) => g.id === guicheEscolhido);
      if (!guiche) return;
      await definirGuiche(guiche.id);
      hub.current?.entrarNoGuiche();
      setPosto((p) => ({ ...p, guiche: { id: guiche.id, nome: guiche.nome }, atendimento: null }));
    });

  const chamar = () =>
    executar(async () => {
      const chamada = await chamarProxima();
      setPosto((p) => ({
        ...p,
        atendimento: { atendimentoId: chamada.atendimentoId, senha: chamada.senha, estado: "Chamado", versao: chamada.versao },
      }));
    });

  const operar = (operacao: OperacaoAtendimento) =>
    executar(async () => {
      const atual = posto.atendimento;
      if (!atual) return;
      const alterado = await operarAtendimento(atual.atendimentoId, operacao);
      setPosto((p) => comEstado(p, atual, alterado.estado, alterado.versao));
    });

  const trocarPosto = () => {
    setErro(null);
    setUnidadeEscolhida("");
    setGuicheEscolhido("");
    setPosto(POSTO_VAZIO);
  };

  const atendimento = posto.atendimento;

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Guichê</h1>
        {posto.unidade && (
          <p className="text-sm text-[var(--color-muted-foreground)]">
            {posto.unidade.nome}
            {posto.guiche && ` · ${posto.guiche.nome}`}{" "}
            <Button variant="link" size="xs" onClick={trocarPosto}>
              Trocar
            </Button>
          </p>
        )}
      </header>

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

      {posto.unidade === null && (
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
            {(unidades.data ?? [])
              .filter((u) => u.ativa)
              .map((u) => (
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

      {posto.unidade !== null && posto.guiche === null && (
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
                {g.nome}
              </option>
            ))}
          </select>
          <Button disabled={ocupado || guicheEscolhido === ""} onClick={() => void escolherGuiche()}>
            Usar este guichê
          </Button>
        </section>
      )}

      {posto.guiche !== null && (
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
                {posto.ultimo && ` Último: ${posto.ultimo.senha} (${ROTULO_ESTADO[posto.ultimo.estado] ?? posto.ultimo.estado}).`}
              </p>
            )}
          </div>

          <div className="flex flex-wrap justify-center gap-3">
            <Button size="lg" disabled={ocupado || atendimento !== null} onClick={() => void chamar()}>
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
