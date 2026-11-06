import { useId, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { listarFilas, listarPrioridades, type FilaDto, type PrioridadeDto } from "@/features/paf/filas/filas-api";
import {
  atualizarPrioridade,
  criarPrioridade,
  desativarPrioridade,
  reativarPrioridade,
} from "@/features/paf/prioridades/prioridades-api";
import {
  combinacoesQueSomemDoTotem,
  combinacoesSemGuiche,
  estaEmAlgumaFilaAtiva,
  EXPLICACAO_VALOR,
  NOME_MAXIMO,
  validarPrioridade,
} from "@/features/paf/prioridades/prioridades-regras";

// Same keys as the fila screen: a prioridade written here shows in its grid without a reload, and the other way round.
const CHAVES = { prioridades: ["paf", "prioridades"], filas: ["paf", "filas"] } as const;

type Edicao = { id: string; nome: string; valor: string };

export function PrioridadesPage() {
  const queryClient = useQueryClient();
  const prioridades = useQuery({ queryKey: CHAVES.prioridades, queryFn: listarPrioridades });
  // Only feeds the totem warnings: without Filas.View (403) the cadastro still works, the warnings just say so.
  const filas = useQuery({ queryKey: CHAVES.filas, queryFn: listarFilas });

  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [nome, setNome] = useState("");
  const [valor, setValor] = useState("0");
  const [edicao, setEdicao] = useState<Edicao | null>(null);
  const [confirmandoDesativar, setConfirmandoDesativar] = useState<string | null>(null);
  const idNome = useId();
  const idValor = useId();

  const executar = async (acao: () => Promise<unknown>) => {
    setOcupado(true);
    setErro(null);
    try {
      await acao();
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: CHAVES.prioridades }),
        queryClient.invalidateQueries({ queryKey: CHAVES.filas }),
      ]);
      return true;
    } catch (e) {
      setErro(mensagemAmigavel(e));
      return false;
    } finally {
      setOcupado(false);
    }
  };

  const criar = async () => {
    const validada = validarPrioridade(nome, valor);
    if (!validada.ok) {
      setErro(validada.mensagem);
      return;
    }
    if (await executar(() => criarPrioridade(validada.nome, validada.valor))) {
      setNome("");
      setValor("0");
    }
  };

  const salvar = async (e: Edicao) => {
    const validada = validarPrioridade(e.nome, e.valor);
    if (!validada.ok) {
      setErro(validada.mensagem);
      return;
    }
    if (await executar(() => atualizarPrioridade(e.id, validada.nome, validada.valor))) {
      setEdicao(null);
    }
  };

  const desativar = async (id: string) => {
    if (await executar(() => desativarPrioridade(id))) {
      setConfirmandoDesativar(null);
    }
  };

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Prioridades</h1>
        <p className="text-sm text-[var(--color-muted-foreground)]">Prioridades de atendimento da unidade corrente.</p>
      </header>

      <p role="note" data-explicacao-valor className="rounded-md border border-[var(--color-border)] px-4 py-3 text-sm">
        {EXPLICACAO_VALOR}
      </p>

      {erro && (
        <p role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erro}
        </p>
      )}

      <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-nova-prioridade>
        <h2 className="font-medium">Nova prioridade</h2>
        <div className="grid gap-3 sm:grid-cols-[1fr_10rem]">
          <div className="space-y-1">
            <Label htmlFor={idNome}>Nome</Label>
            <Input id={idNome} value={nome} maxLength={NOME_MAXIMO} onChange={(e) => setNome(e.target.value)} />
          </div>
          <div className="space-y-1">
            <Label htmlFor={idValor}>Valor</Label>
            <Input id={idValor} type="number" step={1} value={valor} onChange={(e) => setValor(e.target.value)} />
          </div>
        </div>
        <Button disabled={ocupado} onClick={() => void criar()}>
          Cadastrar prioridade
        </Button>
      </section>

      {prioridades.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {prioridades.isError && <p role="alert">{mensagemAmigavel(prioridades.error)}</p>}
      {prioridades.data?.length === 0 && <p>Nenhuma prioridade nesta unidade.</p>}
      {filas.isError && (
        <p role="note" className="text-sm text-[var(--color-muted-foreground)]">
          Não foi possível ler as filas ({mensagemAmigavel(filas.error)}): os avisos de botões do totem não estão disponíveis.
        </p>
      )}

      <ul className="space-y-3">
        {(prioridades.data ?? []).map((p) =>
          edicao?.id === p.id ? (
            <LinhaEmEdicao
              key={p.id}
              prioridade={p}
              edicao={edicao}
              ocupado={ocupado}
              onMudar={setEdicao}
              onSalvar={() => void salvar(edicao)}
              onCancelar={() => setEdicao(null)}
            />
          ) : (
            <LinhaPrioridade
              key={p.id}
              prioridade={p}
              filas={filas.data}
              ocupado={ocupado}
              confirmando={confirmandoDesativar === p.id}
              onEditar={() => {
                setErro(null);
                setEdicao({ id: p.id, nome: p.nome, valor: String(p.valor) });
              }}
              onPedirDesativar={() => setConfirmandoDesativar(p.id)}
              onConfirmarDesativar={() => void desativar(p.id)}
              onVoltar={() => setConfirmandoDesativar(null)}
              onReativar={() => void executar(() => reativarPrioridade(p.id))}
            />
          ),
        )}
      </ul>
    </div>
  );
}

function LinhaEmEdicao(props: {
  prioridade: PrioridadeDto;
  edicao: Edicao;
  ocupado: boolean;
  onMudar: (e: Edicao) => void;
  onSalvar: () => void;
  onCancelar: () => void;
}) {
  const { prioridade: p, edicao } = props;
  return (
    <li data-prioridade={p.id} className="space-y-2 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4">
      <div className="flex flex-wrap items-center gap-2">
        <Input
          aria-label={`Novo nome de ${p.nome}`}
          className="max-w-sm"
          value={edicao.nome}
          maxLength={NOME_MAXIMO}
          onChange={(e) => props.onMudar({ ...edicao, nome: e.target.value })}
        />
        <Input
          aria-label={`Novo valor de ${p.nome}`}
          className="w-32"
          type="number"
          step={1}
          value={edicao.valor}
          onChange={(e) => props.onMudar({ ...edicao, valor: e.target.value })}
        />
        <Button size="sm" disabled={props.ocupado} onClick={props.onSalvar}>
          Salvar
        </Button>
        <Button size="sm" variant="outline" disabled={props.ocupado} onClick={props.onCancelar}>
          Cancelar
        </Button>
      </div>
    </li>
  );
}

function LinhaPrioridade(props: {
  prioridade: PrioridadeDto;
  filas: FilaDto[] | undefined;
  ocupado: boolean;
  confirmando: boolean;
  onEditar: () => void;
  onPedirDesativar: () => void;
  onConfirmarDesativar: () => void;
  onVoltar: () => void;
  onReativar: () => void;
}) {
  const { prioridade: p, filas } = props;
  const somem = filas === undefined ? [] : combinacoesQueSomemDoTotem(p.id, filas);
  const semGuiche = filas === undefined ? [] : combinacoesSemGuiche(p.id, filas);
  const foraDasFilas = filas !== undefined && p.ativa && !estaEmAlgumaFilaAtiva(p.id, filas);

  return (
    <li data-prioridade={p.id} className="space-y-2 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium" data-nome>{p.nome}</span>
        <span className="text-sm text-[var(--color-muted-foreground)]" data-valor>
          Valor {p.valor}
        </span>
        {!p.ativa && <span className="text-sm text-[var(--color-destructive)]">Inativa</span>}
      </div>

      {foraDasFilas && (
        <p className="text-sm text-[var(--color-muted-foreground)]" data-aviso-fora-das-filas>
          Não está na grade de nenhuma fila ativa: não aparece no totem até entrar numa fila.
        </p>
      )}
      {p.ativa && semGuiche.length > 0 && (
        <p className="text-sm text-[var(--color-muted-foreground)]" data-aviso-sem-guiche>
          Sem guichê que atenda: {semGuiche.join(", ")}. Esses botões ficam fora do totem até um guichê ativo cobrir a combinação.
        </p>
      )}

      {props.confirmando ? (
        <div role="alertdialog" aria-label={`Desativar ${p.nome}`} className="space-y-2 rounded-md border border-[var(--color-destructive)] p-3" data-confirmar-desativar>
          <p className="text-sm">
            {filas === undefined
              ? "Não foi possível conferir quais botões do totem usam esta prioridade."
              : somem.length > 0
                ? `Somem do totem os botões: ${somem.join(", ")}.`
                : "Nenhum botão do totem usa esta prioridade hoje."}{" "}
            Fichas já emitidas nesta prioridade continuam na fila e podem ser chamadas.
          </p>
          <div className="flex flex-wrap gap-2">
            <Button size="sm" variant="destructive" disabled={props.ocupado} onClick={props.onConfirmarDesativar}>
              Confirmar desativação
            </Button>
            <Button size="sm" variant="outline" disabled={props.ocupado} onClick={props.onVoltar}>
              Voltar
            </Button>
          </div>
        </div>
      ) : (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="outline" disabled={props.ocupado} onClick={props.onEditar}>
            Editar
          </Button>
          {p.ativa ? (
            <Button size="sm" variant="destructive" disabled={props.ocupado} onClick={props.onPedirDesativar}>
              Desativar
            </Button>
          ) : (
            <Button size="sm" variant="secondary" disabled={props.ocupado} onClick={props.onReativar}>
              Reativar
            </Button>
          )}
        </div>
      )}
    </li>
  );
}
