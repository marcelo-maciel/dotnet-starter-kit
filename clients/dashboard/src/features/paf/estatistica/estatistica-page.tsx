import { useId, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ApiRequestError } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { listarPrioridades } from "@/features/paf/filas/filas-api";
import { listarUnidades } from "@/features/paf/guiche/guiche-api";
import { consultarEstatistica, listarFilasDoEscopo } from "@/features/paf/estatistica/estatistica-api";
import {
  aceitaAgrupamento,
  aceitaSomenteEstacoes,
  hojeEmSaoPaulo,
  mensagensDeValidacao,
  problemaDoPeriodo,
  TIPOS_ESTATISTICA,
  type Agrupamento,
  type FiltrosEstatistica,
} from "@/features/paf/estatistica/estatistica-filtros";
import { paraCsvBytes, paraTsv, type ResultadoEstatistica } from "@/features/paf/estatistica/estatistica-exportacao";
import { TabelaEstatistica } from "@/features/paf/estatistica/tabela-estatistica";

const SELECT_CLASSES = "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

type Consultado = { filtros: FiltrosEstatistica; resultado: ResultadoEstatistica };

// The 400 of a filter carries the rule in `errors`; the friendly line alone would hide which filter was wrong.
function mensagensDoErro(erro: unknown): string[] {
  const linhas = erro instanceof ApiRequestError ? mensagensDeValidacao(erro.problem?.errors) : [];
  return [mensagemAmigavel(erro), ...linhas];
}

function baixar(bytes: Uint8Array<ArrayBuffer>, nome: string) {
  const url = URL.createObjectURL(new Blob([bytes], { type: "text/csv;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = nome;
  link.click();
  URL.revokeObjectURL(url);
}

export function EstatisticaPage() {
  // Every filter source is optional: a 403 on one of them only removes that filter, the report still runs.
  const unidades = useQuery({ queryKey: ["paf", "estatistica", "unidades"], queryFn: listarUnidades });
  const filas = useQuery({ queryKey: ["paf", "estatistica", "filas"], queryFn: listarFilasDoEscopo });
  const prioridades = useQuery({ queryKey: ["paf", "estatistica", "prioridades"], queryFn: listarPrioridades });

  const [filtros, setFiltros] = useState<FiltrosEstatistica>(() => {
    const hoje = hojeEmSaoPaulo(new Date());
    return {
      tipo: 1,
      dataInicial: hoje,
      dataFinal: hoje,
      agrupamento: "Fila",
      unidadeId: null,
      filaId: null,
      prioridadeId: null,
      somenteEstacoesComAtendimento: false,
    };
  });
  const [consultado, setConsultado] = useState<Consultado | null>(null);
  const [erro, setErro] = useState<string[] | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [aviso, setAviso] = useState<string | null>(null);
  const ids = { tipo: useId(), inicio: useId(), fim: useId(), agrupamento: useId(), unidade: useId(), fila: useId(), prioridade: useId() };

  const mudar = (parcial: Partial<FiltrosEstatistica>) => setFiltros((f) => ({ ...f, ...parcial }));
  const problemaPeriodo = problemaDoPeriodo(filtros.dataInicial, filtros.dataFinal);

  const nomeUnidade = new Map((unidades.data ?? []).map((u) => [u.id, u.nome]));
  const variasUnidades = (unidades.data?.length ?? 0) > 1;
  // A fila or prioridade of another unidade than the chosen one is a 400 on the server: never offered.
  const dentroDaUnidade = (unidadeId: string) => filtros.unidadeId === null || filtros.unidadeId === unidadeId;
  const filasOferecidas = (filas.data ?? []).filter((f) => dentroDaUnidade(f.unidadeId));
  const prioridadesOferecidas = (prioridades.data ?? []).filter((p) => dentroDaUnidade(p.unidadeId));

  const mudarUnidade = (unidadeId: string | null) =>
    setFiltros((f) => ({
      ...f,
      unidadeId,
      filaId: unidadeId !== null && filas.data?.find((x) => x.id === f.filaId)?.unidadeId !== unidadeId ? null : f.filaId,
      prioridadeId:
        unidadeId !== null && prioridades.data?.find((x) => x.id === f.prioridadeId)?.unidadeId !== unidadeId ? null : f.prioridadeId,
    }));

  const consultar = async () => {
    if (problemaPeriodo !== null) return;
    setOcupado(true);
    setErro(null);
    setAviso(null);
    try {
      const pedido = filtros;
      setConsultado({ filtros: pedido, resultado: await consultarEstatistica(pedido) });
    } catch (e) {
      setConsultado(null);
      setErro(mensagensDoErro(e));
    } finally {
      setOcupado(false);
    }
  };

  const copiar = async (resultado: ResultadoEstatistica) => {
    try {
      await navigator.clipboard.writeText(paraTsv(resultado));
      setAviso("Resultado copiado. Cole na planilha.");
    } catch {
      // The browser refuses the clipboard without focus or permission; the file export still works.
      setAviso("O navegador não liberou a área de transferência. Use Exportar CSV.");
    }
  };

  const exportar = ({ filtros: f, resultado }: Consultado) =>
    baixar(paraCsvBytes(resultado), `estatistica-tipo-${f.tipo}-${f.dataInicial}-a-${f.dataFinal}.csv`);

  return (
    <div className="mx-auto max-w-6xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Estatística</h1>
        <p role="note" data-aviso-escopo className="text-sm text-[var(--color-muted-foreground)]">
          Os números são a soma das unidades do seu escopo; cada linha mostra a sua unidade.
        </p>
      </header>

      <section className="space-y-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-filtros>
        <div className="grid gap-3 sm:grid-cols-3">
          <div className="space-y-1 sm:col-span-3">
            <Label htmlFor={ids.tipo}>Tipo</Label>
            <select id={ids.tipo} className={SELECT_CLASSES} value={filtros.tipo} onChange={(e) => mudar({ tipo: Number(e.target.value) })}>
              {TIPOS_ESTATISTICA.map((t) => (
                <option key={t.valor} value={t.valor}>
                  {t.valor}. {t.nome}
                </option>
              ))}
            </select>
          </div>
          <div className="space-y-1">
            <Label htmlFor={ids.inicio}>Data inicial</Label>
            <Input id={ids.inicio} type="date" value={filtros.dataInicial} onChange={(e) => mudar({ dataInicial: e.target.value })} />
          </div>
          <div className="space-y-1">
            <Label htmlFor={ids.fim}>Data final</Label>
            <Input id={ids.fim} type="date" value={filtros.dataFinal} onChange={(e) => mudar({ dataFinal: e.target.value })} />
          </div>
          {aceitaAgrupamento(filtros.tipo) && (
            <div className="space-y-1">
              <Label htmlFor={ids.agrupamento}>Agrupar por</Label>
              <select
                id={ids.agrupamento}
                className={SELECT_CLASSES}
                value={filtros.agrupamento}
                onChange={(e) => mudar({ agrupamento: e.target.value as Agrupamento })}
              >
                <option value="Fila">Fila</option>
                <option value="Setor">Setor</option>
              </select>
            </div>
          )}
          {variasUnidades && (
            <div className="space-y-1">
              <Label htmlFor={ids.unidade}>Unidade</Label>
              <select id={ids.unidade} className={SELECT_CLASSES} value={filtros.unidadeId ?? ""} onChange={(e) => mudarUnidade(e.target.value || null)}>
                <option value="">Todas do meu escopo</option>
                {(unidades.data ?? []).map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.nome}
                  </option>
                ))}
              </select>
            </div>
          )}
          {filas.isSuccess && (
            <div className="space-y-1">
              <Label htmlFor={ids.fila}>Fila</Label>
              <select id={ids.fila} className={SELECT_CLASSES} value={filtros.filaId ?? ""} onChange={(e) => mudar({ filaId: e.target.value || null })}>
                <option value="">Todas</option>
                {filasOferecidas.map((f) => (
                  <option key={f.id} value={f.id}>
                    {variasUnidades ? `${f.nome} (${nomeUnidade.get(f.unidadeId) ?? "outra unidade"})` : f.nome}
                  </option>
                ))}
              </select>
            </div>
          )}
          {prioridades.isSuccess && (
            <div className="space-y-1">
              <Label htmlFor={ids.prioridade}>Prioridade</Label>
              <select
                id={ids.prioridade}
                className={SELECT_CLASSES}
                value={filtros.prioridadeId ?? ""}
                onChange={(e) => mudar({ prioridadeId: e.target.value || null })}
              >
                <option value="">Todas</option>
                {prioridadesOferecidas.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.ativa ? p.nome : `${p.nome} (inativa)`}
                  </option>
                ))}
              </select>
            </div>
          )}
        </div>
        {aceitaSomenteEstacoes(filtros.tipo) && (
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={filtros.somenteEstacoesComAtendimento}
              onChange={(e) => mudar({ somenteEstacoesComAtendimento: e.target.checked })}
            />
            Somente estações que tiveram atendimento
          </label>
        )}
        {(filas.isError || prioridades.isError) && (
          <p role="note" className="text-sm text-[var(--color-muted-foreground)]">
            {filas.isError && `Filtro de fila indisponível (${mensagemAmigavel(filas.error)}). `}
            {prioridades.isError && `Filtro de prioridade indisponível (${mensagemAmigavel(prioridades.error)}).`}
          </p>
        )}
        {variasUnidades && (
          <p className="text-xs text-[var(--color-muted-foreground)]">As prioridades listadas são as da unidade corrente.</p>
        )}
        {problemaPeriodo !== null && (
          <p role="alert" data-periodo-invalido className="text-sm text-[var(--color-destructive)]">
            {problemaPeriodo}
          </p>
        )}
        <Button disabled={ocupado || problemaPeriodo !== null} onClick={() => void consultar()}>
          {ocupado ? "Consultando…" : "Consultar"}
        </Button>
      </section>

      {erro && (
        <div role="alert" data-erro-consulta className="space-y-1 rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erro.map((linha) => (
            <p key={linha}>{linha}</p>
          ))}
        </div>
      )}

      {consultado && (
        <section className="space-y-3" data-resultado>
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="outline" onClick={() => void copiar(consultado.resultado)}>
              Copiar
            </Button>
            <Button variant="outline" onClick={() => exportar(consultado)}>
              Exportar CSV
            </Button>
            {aviso && (
              <p role="status" className="text-sm text-[var(--color-muted-foreground)]">
                {aviso}
              </p>
            )}
          </div>
          <TabelaEstatistica resultado={consultado.resultado} />
        </section>
      )}
    </div>
  );
}
