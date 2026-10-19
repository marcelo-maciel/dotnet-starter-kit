import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ApiRequestError } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  gravarPadraoDaEmpresa,
  listarParametros,
  sobrescreverNaUnidade,
  voltarAoHerdado,
  type ParametroDto,
} from "@/features/paf/parametros/parametros-api";
import {
  agruparPorGrupo,
  escolhasDoCampo,
  rotuloDoParametro,
  seloDaOrigem,
  valorExibido,
} from "@/features/paf/parametros/parametros-regras";

const CHAVE_PARAMETROS = ["paf", "parametros"] as const;

const SELECT_CLASSES = "h-9 rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

type Edicao = { chave: string; valor: string };

// The 400 of a value carries the rule in `errors` (a hotkey already taken names both actions).
function mensagensDoErro(erro: unknown): string[] {
  if (erro instanceof ApiRequestError && Array.isArray(erro.problem?.errors) && erro.problem.errors.length > 0) {
    return [mensagemAmigavel(erro), ...erro.problem.errors];
  }
  return [mensagemAmigavel(erro)];
}

function CampoDeValor({ item, valor, aoMudar }: { item: ParametroDto; valor: string; aoMudar: (v: string) => void }) {
  const escolhas = escolhasDoCampo(item);
  const rotulo = `Novo valor de ${rotuloDoParametro(item.chave)}`;
  if (escolhas.length > 0) {
    return (
      <select aria-label={rotulo} className={SELECT_CLASSES} value={valor} onChange={(e) => aoMudar(e.target.value)}>
        {escolhas.map(([v, texto]) => (
          <option key={v} value={v}>
            {texto}
          </option>
        ))}
      </select>
    );
  }
  return (
    <Input
      aria-label={rotulo}
      className="max-w-sm"
      inputMode={item.tipo === "Inteiro" ? "numeric" : undefined}
      value={valor}
      onChange={(e) => aoMudar(e.target.value)}
    />
  );
}

export function ParametrosPage() {
  const queryClient = useQueryClient();
  const parametros = useQuery({ queryKey: CHAVE_PARAMETROS, queryFn: listarParametros });
  const [edicao, setEdicao] = useState<Edicao | null>(null);
  const [erros, setErros] = useState<string[]>([]);
  const [ocupado, setOcupado] = useState(false);

  const executar = async (acao: () => Promise<unknown>) => {
    setOcupado(true);
    setErros([]);
    try {
      await acao();
      setEdicao(null);
      await queryClient.invalidateQueries({ queryKey: CHAVE_PARAMETROS });
    } catch (e) {
      setErros(mensagensDoErro(e));
    } finally {
      setOcupado(false);
    }
  };

  const dados = parametros.data;

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Parâmetros</h1>
        <p className="text-sm text-[var(--color-muted-foreground)]">
          A empresa define o padrão e a unidade sobrescreve o que precisar. O selo de cada linha diz de onde vem o
          valor em vigor nesta unidade.
        </p>
      </header>

      {parametros.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {parametros.isError && <p role="alert">{mensagemAmigavel(parametros.error)}</p>}
      {erros.length > 0 && (
        <div role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erros.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      )}

      {dados &&
        agruparPorGrupo(dados.itens).map(([grupo, itens]) => (
          <section key={grupo} className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5">
            <h2 className="text-lg font-medium">{grupo}</h2>
            <ul className="divide-y divide-[var(--color-border)]">
              {itens.map((item) => {
                const selo = seloDaOrigem(item.origem);
                const editando = edicao?.chave === item.chave ? edicao : null;
                return (
                  <li key={item.chave} className="space-y-2 py-3" data-parametro={item.chave}>
                    <div className="flex flex-wrap items-center gap-3">
                      <span className="min-w-64 font-medium">{rotuloDoParametro(item.chave)}</span>
                      <span data-valor-efetivo>{valorExibido(item, item.valor)}</span>
                      <span
                        data-selo={item.origem}
                        className={
                          selo.sobrescrito
                            ? "rounded-full bg-amber-200 px-2 py-0.5 text-xs text-amber-950"
                            : "rounded-full bg-[var(--color-muted)] px-2 py-0.5 text-xs"
                        }
                      >
                        {selo.texto}
                      </span>
                      {item.origem === "Unidade" && item.valorEmpresa !== null && (
                        <span className="text-xs text-[var(--color-muted-foreground)]">
                          Padrão da empresa: {valorExibido(item, item.valorEmpresa)}
                        </span>
                      )}
                      {(dados.alteraUnidade || dados.alteraEmpresa) && !editando && (
                        <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setEdicao({ chave: item.chave, valor: item.valor })}>
                          Alterar
                        </Button>
                      )}
                      {dados.alteraUnidade && item.origem === "Unidade" && !editando && (
                        <Button size="sm" variant="ghost" disabled={ocupado} onClick={() => void executar(() => voltarAoHerdado(item.chave))}>
                          Voltar ao herdado
                        </Button>
                      )}
                    </div>
                    {editando && (
                      <div className="flex flex-wrap items-center gap-2">
                        <CampoDeValor item={item} valor={editando.valor} aoMudar={(valor) => setEdicao({ chave: item.chave, valor })} />
                        {dados.alteraUnidade && (
                          <Button size="sm" disabled={ocupado} onClick={() => void executar(() => sobrescreverNaUnidade(item.chave, editando.valor))}>
                            Salvar nesta unidade
                          </Button>
                        )}
                        {dados.alteraEmpresa && (
                          <Button
                            size="sm"
                            variant="secondary"
                            disabled={ocupado}
                            onClick={() => void executar(() => gravarPadraoDaEmpresa(item.chave, editando.valor))}
                          >
                            Salvar como padrão da empresa
                          </Button>
                        )}
                        <Button size="sm" variant="ghost" disabled={ocupado} onClick={() => setEdicao(null)}>
                          Cancelar
                        </Button>
                      </div>
                    )}
                  </li>
                );
              })}
            </ul>
          </section>
        ))}
    </div>
  );
}
