import { useId, useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useAuth } from "@/auth/use-auth";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  cancelarAtendimentoDaFicha,
  consultarFicha,
  reabilitarAtendimentoDaFicha,
  type AtendimentoDaFichaDto,
  type FichaConsultadaDto,
} from "@/features/paf/consultar-ficha/consultar-ficha-api";
import {
  acoesDoAtendimento,
  filaDeOrigem,
  formatarDuracao,
  formatarInstante,
  hojeEmSaoPaulo,
  normalizarSenha,
  rotuloEstado,
  SENHA_MAX,
  senhaValida,
} from "@/features/paf/consultar-ficha/consultar-ficha-regras";

type Consulta = { senha: string; data: string };
type Permitido = { cancelar: boolean; reabilitar: boolean };

const CANCELAR = "Permissions.PAF.FichasDoDia.Cancelar";
const REABILITAR = "Permissions.PAF.FichasDoDia.Reabilitar";

/** RF18: one ficha by printed senha and day, every atendimento it had, with the RF12 cancel and reabilitar. */
export function ConsultarFichaPage() {
  const [senha, setSenha] = useState("");
  const [data, setData] = useState(() => hojeEmSaoPaulo(new Date()));
  const [consulta, setConsulta] = useState<Consulta | null>(null);
  const [erroAcao, setErroAcao] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const idSenha = useId();
  const idData = useId();
  const { user, permissionsHydrated } = useAuth();
  // The buttons follow the F04/F05 permissions; viewing (FichasDoDia.View) does not imply either.
  const permitido: Permitido = {
    cancelar: permissionsHydrated && (user?.permissions.includes(CANCELAR) ?? false),
    reabilitar: permissionsHydrated && (user?.permissions.includes(REABILITAR) ?? false),
  };

  const resultado = useQuery({
    queryKey: ["paf", "fichas", "consulta", consulta?.senha, consulta?.data],
    queryFn: () => consultarFicha(consulta!.senha, consulta!.data),
    enabled: consulta !== null,
    // A 404 is an answer ("no such ficha"), not a transient failure worth repeating.
    retry: false,
  });

  const podeConsultar = senhaValida(senha) && data !== "";

  const consultar = (e: FormEvent) => {
    e.preventDefault();
    if (!podeConsultar) return;
    setErroAcao(null);
    const nova = { senha: normalizarSenha(senha), data };
    setSenha(nova.senha);
    if (consulta?.senha === nova.senha && consulta.data === nova.data) {
      void resultado.refetch();
      return;
    }
    setConsulta(nova);
  };

  const agir = async (acao: () => Promise<unknown>) => {
    setOcupado(true);
    setErroAcao(null);
    try {
      await acao();
    } catch (e) {
      setErroAcao(mensagemAmigavel(e));
    } finally {
      // Re-read after success and after a 409 alike: the refusal usually means the state moved under the screen.
      await resultado.refetch();
      setOcupado(false);
    }
  };

  return (
    <div className="mx-auto max-w-5xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Consultar ficha</h1>
      </header>

      <form
        onSubmit={consultar}
        className="flex flex-wrap items-end gap-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5"
        data-consulta-ficha
      >
        <div className="space-y-1">
          <Label htmlFor={idSenha}>Senha impressa</Label>
          <Input
            id={idSenha}
            value={senha}
            maxLength={SENHA_MAX + 10}
            autoComplete="off"
            placeholder="A12"
            onChange={(e) => setSenha(e.target.value)}
            aria-describedby={`${idSenha}-ajuda`}
          />
          <p id={`${idSenha}-ajuda`} className="text-xs text-[var(--color-muted-foreground)]">
            Letra seguida do número, como na ficha.
          </p>
        </div>
        <div className="space-y-1">
          <Label htmlFor={idData}>Data</Label>
          <Input id={idData} type="date" value={data} onChange={(e) => setData(e.target.value)} />
          <p className="text-xs">&nbsp;</p>
        </div>
        <div className="space-y-1">
          <Button type="submit" disabled={!podeConsultar || resultado.isFetching}>
            Consultar
          </Button>
          <p className="text-xs">&nbsp;</p>
        </div>
      </form>

      {erroAcao && (
        <p role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erroAcao}
        </p>
      )}

      {consulta !== null && resultado.isPending && <p className="text-[var(--color-muted-foreground)]">Consultando…</p>}
      {resultado.isError && <p role="alert">{mensagemAmigavel(resultado.error)}</p>}

      {(resultado.data ?? []).map((ficha) => (
        <FichaConsultada key={ficha.fichaId} ficha={ficha} permitido={permitido} ocupado={ocupado} agir={agir} />
      ))}
    </div>
  );
}

function FichaConsultada({
  ficha,
  permitido,
  ocupado,
  agir,
}: {
  ficha: FichaConsultadaDto;
  permitido: Permitido;
  ocupado: boolean;
  agir: (acao: () => Promise<unknown>) => Promise<void>;
}) {
  return (
    <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-ficha={ficha.fichaId}>
      <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
        <h2 className="text-xl font-semibold">{ficha.senha}</h2>
        <span className="text-sm text-[var(--color-muted-foreground)]">
          Emitida em {formatarInstante(ficha.criadoEm)} na fila {ficha.fila}, prioridade {ficha.prioridade}
        </span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="text-left text-[var(--color-muted-foreground)]">
            <tr>
              <th scope="col" className="py-2 pr-3 font-medium">Fila</th>
              <th scope="col" className="py-2 pr-3 font-medium">Estado</th>
              <th scope="col" className="py-2 pr-3 font-medium">Guichê</th>
              <th scope="col" className="py-2 pr-3 font-medium">Início</th>
              <th scope="col" className="py-2 pr-3 font-medium">Fim</th>
              <th scope="col" className="py-2 pr-3 font-medium">Tempo total</th>
              <th scope="col" className="py-2 font-medium">Ações</th>
            </tr>
          </thead>
          <tbody>
            {ficha.atendimentos.map((a) => (
              <LinhaAtendimento key={a.atendimentoId} atendimento={a} todos={ficha.atendimentos} permitido={permitido} ocupado={ocupado} agir={agir} />
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function LinhaAtendimento({
  atendimento: a,
  todos,
  permitido,
  ocupado,
  agir,
}: {
  atendimento: AtendimentoDaFichaDto;
  todos: AtendimentoDaFichaDto[];
  permitido: Permitido;
  ocupado: boolean;
  agir: (acao: () => Promise<unknown>) => Promise<void>;
}) {
  const acoes = acoesDoAtendimento(a.estado);
  const origem = filaDeOrigem(todos, a);
  return (
    <tr className="border-t border-[var(--color-border)] align-top" data-atendimento={a.atendimentoId} data-estado={a.estado}>
      <td className="py-2 pr-3">
        <div>{a.fila}</div>
        <div className="text-xs text-[var(--color-muted-foreground)]">
          {a.prioridade}
          {origem && <> · encaminhada de {origem}</>}
        </div>
      </td>
      <td className="py-2 pr-3">{rotuloEstado(a.estado)}</td>
      <td className="py-2 pr-3">{a.guiche ?? "-"}</td>
      <td className="py-2 pr-3">{formatarInstante(a.iniciadoEm)}</td>
      <td className="py-2 pr-3">{formatarInstante(a.fimEm)}</td>
      <td className="py-2 pr-3 tabular-nums" data-tempo-total>
        {formatarDuracao(a.tempoTotalSegundos)}
      </td>
      <td className="py-2">
        <div className="flex flex-wrap gap-2">
          {acoes.cancelar && permitido.cancelar && (
            <Button
              size="sm"
              variant="destructive"
              disabled={ocupado}
              onClick={() => void agir(() => cancelarAtendimentoDaFicha(a.atendimentoId))}
            >
              Cancelar
            </Button>
          )}
          {acoes.reabilitar && permitido.reabilitar && (
            <Button
              size="sm"
              variant="secondary"
              disabled={ocupado}
              onClick={() => void agir(() => reabilitarAtendimentoDaFicha(a.atendimentoId))}
            >
              Reabilitar
            </Button>
          )}
        </div>
      </td>
    </tr>
  );
}
