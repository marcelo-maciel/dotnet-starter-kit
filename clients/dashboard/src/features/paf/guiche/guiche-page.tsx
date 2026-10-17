import { useId, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  chamarProxima,
  definirGuiche,
  definirUnidadeCorrente,
  listarGuiches,
  listarUnidades,
  obterPosto,
  operarAtendimento,
  type GuicheDto,
  type OperacaoAtendimento,
} from "@/features/paf/guiche/guiche-api";

type Ultimo = { senha: string; estado: string };

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

const SELECT_CLASSES =
  "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

function rotuloGuiche(g: GuicheDto): string {
  if (g.meu) return `${g.nome} (seu)`;
  return g.ocupante ? `${g.nome} (em uso por ${g.ocupante})` : g.nome;
}

export function GuichePage() {
  const queryClient = useQueryClient();
  const [troca, setTroca] = useState<Etapa | null>(null);
  const [ultimo, setUltimo] = useState<Ultimo | null>(null);
  const [unidadeEscolhida, setUnidadeEscolhida] = useState("");
  const [guicheEscolhido, setGuicheEscolhido] = useState("");
  const [ocupado, setOcupado] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const idUnidade = useId();
  const idGuiche = useId();

  const posto = useQuery({ queryKey: CHAVE_POSTO, queryFn: obterPosto });
  const etapa: Etapa | null = posto.data
    ? (troca ?? (posto.data.unidadeId === null ? "unidade" : posto.data.estacaoId === null ? "guiche" : "operar"))
    : null;

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
      setErro(mensagemAmigavel(e));
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

  const escolherGuiche = () =>
    executar(async () => {
      if (guicheEscolhido === "") return;
      await definirGuiche(guicheEscolhido);
      setTroca(null);
    });

  const chamar = () =>
    executar(async () => {
      await chamarProxima();
    });

  const operar = (operacao: OperacaoAtendimento) =>
    executar(async () => {
      const atual = posto.data?.atendimento;
      if (!atual) return;
      const alterado = await operarAtendimento(atual.atendimentoId, operacao);
      if (ESTADOS_FINAIS.has(alterado.estado)) setUltimo({ senha: atual.senha, estado: alterado.estado });
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
          <Button disabled={ocupado || guicheEscolhido === ""} onClick={() => void escolherGuiche()}>
            Usar este guichê
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
