import { useId, useState, type ReactNode } from "react";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ApiRequestError } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { hojeEmSaoPaulo } from "@/features/paf/estatistica/estatistica-filtros";
import { listarMidias, type MidiaDto } from "@/features/paf/midias/midias-api";
import {
  ativarProgramacao,
  atualizarProgramacao,
  criarProgramacao,
  desativarProgramacao,
  excluirProgramacao,
  pesquisarProgramacoes,
  type ProgramacaoDto,
} from "@/features/paf/programacoes/programacoes-api";
import {
  alternarDia,
  calcular,
  consultaDaPesquisa,
  DIAS,
  duracaoDaMidia,
  duracaoLegivel,
  FILTROS_VAZIOS,
  horaDosSegundos,
  MENSAGEM_SEM_LISTA_DE_MIDIAS,
  NOME_MAXIMO,
  periodoInvertido,
  rotuloDosDias,
  TODOS_OS_DIAS,
  validarProgramacao,
  type FiltrosPesquisa,
  type FormProgramacao,
  type MidiaAtual,
} from "@/features/paf/programacoes/programacoes-regras";

const CHAVE = ["paf", "programacoes-midia"] as const;
const CHAVE_MIDIAS = ["paf", "midias"] as const;
const TAMANHO_PAGINA = 20;
const PERMISSAO_CRIAR = "Permissions.PAF.Programacoes.Create";
const PERMISSAO_ALTERAR = "Permissions.PAF.Programacoes.Update";
const PERMISSAO_EXCLUIR = "Permissions.PAF.Programacoes.Delete";
const SELECT_CLASSES = "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

type Edicao = { id: string | null; versao: number; form: FormProgramacao; midiaAtual: MidiaAtual | null };

function mensagensDoErro(erro: unknown): string[] {
  if (erro instanceof ApiRequestError && erro.problem?.errors !== undefined) {
    const errors = erro.problem.errors;
    const linhas = Array.isArray(errors) ? errors : Object.values(errors).flat();
    if (linhas.length > 0) return linhas;
  }
  return [mensagemAmigavel(erro)];
}

function novaEdicao(): Edicao {
  const hoje = hojeEmSaoPaulo(new Date());
  return { id: null, versao: 0, midiaAtual: null, form: { midiaId: "", nome: "", dataInicial: hoje, dataFinal: hoje, horaInicial: "08:00", repeticoes: "1", dias: TODOS_OS_DIAS } };
}

function edicaoDe(p: ProgramacaoDto): Edicao {
  return {
    id: p.id,
    versao: p.versao,
    // tempoMidiaSegundos is the mídia's own duration (AgendaMidia): the edit keeps it without the mídia list.
    midiaAtual: { id: p.midiaId, descricao: p.midiaDescricao, duracaoSegundos: p.tempoMidiaSegundos },
    form: {
      midiaId: p.midiaId,
      nome: p.nome,
      dataInicial: p.dataInicial,
      dataFinal: p.dataFinal,
      horaInicial: p.horaInicial,
      repeticoes: String(p.repeticoes),
      dias: p.dias,
    },
  };
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

function SeletorDias({ legenda, mascara, onChange }: { legenda: string; mascara: number; onChange: (m: number) => void }) {
  return (
    <fieldset className="space-y-1">
      <legend className="text-sm font-medium">{legenda}</legend>
      <div className="flex flex-wrap gap-3 text-sm">
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={mascara === TODOS_OS_DIAS} onChange={(e) => onChange(e.target.checked ? TODOS_OS_DIAS : 0)} />
          Todos os dias
        </label>
        {DIAS.map((d) => (
          <label key={d.bit} className="flex items-center gap-1">
            <input type="checkbox" aria-label={d.nome} checked={(mascara & d.bit) !== 0} onChange={() => onChange(alternarDia(mascara, d.bit))} />
            {d.curto}
          </label>
        ))}
      </div>
    </fieldset>
  );
}

export function ProgramacoesPage() {
  const queryClient = useQueryClient();
  const { user, permissionsHydrated } = useAuth();
  const tem = (p: string) => permissionsHydrated && (user?.permissions.includes(p) ?? false);
  const podeCriar = tem(PERMISSAO_CRIAR);
  const podeAlterar = tem(PERMISSAO_ALTERAR);
  const podeExcluir = tem(PERMISSAO_EXCLUIR);

  const [filtros, setFiltros] = useState<FiltrosPesquisa>(FILTROS_VAZIOS);
  const [aplicados, setAplicados] = useState<FiltrosPesquisa>(FILTROS_VAZIOS);
  const [pagina, setPagina] = useState(1);
  const consulta = consultaDaPesquisa(aplicados, pagina, TAMANHO_PAGINA);
  const programacoes = useQuery({ queryKey: [...CHAVE, consulta], queryFn: () => pesquisarProgramacoes(consulta), placeholderData: keepPreviousData });
  // Without Midias.View the form cannot offer a mídia: the search still works, the form says why it is missing.
  const midias = useQuery({ queryKey: CHAVE_MIDIAS, queryFn: listarMidias, retry: false });

  const [edicao, setEdicao] = useState<Edicao | null>(null);
  const [erros, setErros] = useState<string[]>([]);
  const [aviso, setAviso] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [excluindo, setExcluindo] = useState<string | null>(null);

  const executar = async (acao: () => Promise<unknown>, sucesso: string) => {
    setOcupado(true);
    setErros([]);
    setAviso(null);
    try {
      await acao();
      setAviso(sucesso);
      return true;
    } catch (e) {
      setErros(mensagensDoErro(e));
      return false;
    } finally {
      await queryClient.invalidateQueries({ queryKey: CHAVE });
      setOcupado(false);
    }
  };

  const salvar = async () => {
    if (!edicao) return;
    const validada = validarProgramacao(edicao.form, duracaoDaMidia(midias.isError ? null : (midias.data ?? null), edicao.midiaAtual, edicao.form.midiaId));
    if (!validada.ok) {
      setErros([validada.mensagem]);
      return;
    }
    const ok = await executar(
      () => (edicao.id === null ? criarProgramacao(validada.corpo) : atualizarProgramacao(edicao.id, validada.corpo, edicao.versao)),
      `Programação ${validada.corpo.nome} salva.`,
    );
    if (ok) setEdicao(null);
  };

  const filtrosInvalidos = periodoInvertido(filtros);
  const dados = programacoes.data;

  return (
    <div className="mx-auto max-w-6xl space-y-6">
      <header className="flex flex-wrap items-center justify-between gap-2">
        <div className="space-y-1">
          <h1 className="text-2xl font-semibold">Programação de mídias</h1>
          <p className="text-sm text-[var(--color-muted-foreground)]">
            Mídias exibidas no painel em dias e horários definidos (horário de Brasília). Na janela da programação ela se sobrepõe à lista de exibição da estação.
          </p>
        </div>
        {podeCriar && !edicao && (
          <Button
            onClick={() => {
              setErros([]);
              setAviso(null);
              setEdicao(novaEdicao());
            }}
          >
            Nova programação
          </Button>
        )}
      </header>

      {erros.length > 0 && (
        <ul role="alert" data-erros-programacao className="list-disc rounded-md border border-[var(--color-destructive)] py-3 pl-8 pr-4 text-[var(--color-destructive)]">
          {erros.map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      )}
      {aviso && (
        <p role="status" data-aviso-programacao className="rounded-md border border-[var(--color-border)] px-4 py-3 text-sm">
          {aviso}
        </p>
      )}

      {edicao && (
        <FormularioProgramacao
          edicao={edicao}
          midias={midias.isError ? null : (midias.data ?? [])}
          ocupado={ocupado}
          onMudar={(form) => setEdicao({ ...edicao, form })}
          onSalvar={() => void salvar()}
          onCancelar={() => setEdicao(null)}
        />
      )}

      <form
        className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5"
        data-pesquisa-programacao
        onSubmit={(e) => {
          e.preventDefault();
          if (filtrosInvalidos) return;
          setPagina(1);
          setAplicados(filtros);
        }}
      >
        <h2 className="font-medium">Pesquisar</h2>
        <div className="grid gap-3 sm:grid-cols-4">
          <Campo rotulo="Nome contém">{(id) => <Input id={id} value={filtros.nome} onChange={(e) => setFiltros({ ...filtros, nome: e.target.value })} />}</Campo>
          <Campo rotulo="No ar à hora">{(id) => <Input id={id} type="time" step={1} value={filtros.hora} onChange={(e) => setFiltros({ ...filtros, hora: e.target.value })} />}</Campo>
          <Campo rotulo="Período de">{(id) => <Input id={id} type="date" value={filtros.de} onChange={(e) => setFiltros({ ...filtros, de: e.target.value })} />}</Campo>
          <Campo rotulo="Período até">{(id) => <Input id={id} type="date" value={filtros.ate} onChange={(e) => setFiltros({ ...filtros, ate: e.target.value })} />}</Campo>
        </div>
        <SeletorDias legenda="Em algum destes dias (nenhum marcado = qualquer dia)" mascara={filtros.dias} onChange={(dias) => setFiltros({ ...filtros, dias })} />
        {filtrosInvalidos && (
          <p role="alert" className="text-sm text-[var(--color-destructive)]">
            A data final do período não pode ser anterior à inicial.
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={filtrosInvalidos}>
            Pesquisar
          </Button>
          <Button
            type="button"
            variant="outline"
            onClick={() => {
              setFiltros(FILTROS_VAZIOS);
              setAplicados(FILTROS_VAZIOS);
              setPagina(1);
            }}
          >
            Limpar filtros
          </Button>
        </div>
      </form>

      {programacoes.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {programacoes.isError && <p role="alert">{mensagemAmigavel(programacoes.error)}</p>}
      {dados?.items.length === 0 && <p>Nenhuma programação encontrada.</p>}

      {dados && dados.items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--color-border)]">
          <table className="w-full text-sm" data-tabela-programacoes>
            <thead className="bg-[var(--color-muted)] text-left">
              <tr>
                <th className="px-3 py-2">Nome</th>
                <th className="px-3 py-2">Mídia</th>
                <th className="px-3 py-2">Período</th>
                <th className="px-3 py-2">Dias</th>
                <th className="px-3 py-2">Início</th>
                <th className="px-3 py-2">Repetições</th>
                <th className="px-3 py-2">Tempo total</th>
                <th className="px-3 py-2">Fim</th>
                <th className="px-3 py-2">Situação</th>
                <th className="px-3 py-2">
                  <span className="sr-only">Ações</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {dados.items.map((p) => (
                <tr key={p.id} data-programacao={p.id} className="border-t border-[var(--color-border)] align-top">
                  <td className="px-3 py-2 font-medium">{p.nome}</td>
                  <td className="px-3 py-2">
                    {p.midiaDescricao}
                    {!p.midiaAtiva && <span className="block text-xs text-[var(--color-destructive)]">mídia inativa: não vai ao ar</span>}
                  </td>
                  <td className="px-3 py-2 whitespace-nowrap">
                    {p.dataInicial.split("-").reverse().join("/")} a {p.dataFinal.split("-").reverse().join("/")}
                  </td>
                  <td className="px-3 py-2">{rotuloDosDias(p.dias)}</td>
                  <td className="px-3 py-2">{p.horaInicial}</td>
                  <td className="px-3 py-2">{p.repeticoes}</td>
                  <td className="px-3 py-2 whitespace-nowrap">{duracaoLegivel(p.tempoTotalSegundos)}</td>
                  <td className="px-3 py-2" data-hora-final>{horaDosSegundos(p.horaFinalSegundos)}</td>
                  <td className="px-3 py-2">{p.ativa ? "Ativa" : "Inativa"}</td>
                  <td className="px-3 py-2">
                    {excluindo === p.id ? (
                      <div role="alertdialog" aria-label={`Excluir ${p.nome}`} className="space-y-2" data-confirmar-exclusao>
                        <p>Excluir {p.nome}? Ela some do painel e não pode ser recuperada.</p>
                        <div className="flex gap-2">
                          <Button
                            size="sm"
                            variant="destructive"
                            disabled={ocupado}
                            onClick={() => void executar(() => excluirProgramacao(p.id), `Programação ${p.nome} excluída.`).then(() => setExcluindo(null))}
                          >
                            Confirmar exclusão
                          </Button>
                          <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setExcluindo(null)}>
                            Voltar
                          </Button>
                        </div>
                      </div>
                    ) : (
                      <div className="flex flex-wrap gap-2">
                        {podeAlterar && (
                          <Button
                            size="sm"
                            variant="outline"
                            disabled={ocupado}
                            onClick={() => {
                              setErros([]);
                              setAviso(null);
                              setEdicao(edicaoDe(p));
                            }}
                          >
                            Editar
                          </Button>
                        )}
                        {podeAlterar &&
                          (p.ativa ? (
                            <Button size="sm" variant="outline" disabled={ocupado} onClick={() => void executar(() => desativarProgramacao(p.id), `Programação ${p.nome} desativada.`)}>
                              Desativar
                            </Button>
                          ) : (
                            <Button size="sm" variant="secondary" disabled={ocupado} onClick={() => void executar(() => ativarProgramacao(p.id), `Programação ${p.nome} ativada.`)}>
                              Ativar
                            </Button>
                          ))}
                        {podeExcluir && (
                          <Button size="sm" variant="destructive" disabled={ocupado} onClick={() => setExcluindo(p.id)}>
                            Excluir
                          </Button>
                        )}
                      </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {dados && dados.totalPages > 1 && (
        <nav aria-label="Páginas" className="flex items-center gap-3 text-sm">
          <Button size="sm" variant="outline" disabled={pagina <= 1} onClick={() => setPagina(pagina - 1)}>
            Anterior
          </Button>
          <span>
            Página {dados.pageNumber} de {dados.totalPages} ({dados.totalCount} programações)
          </span>
          <Button size="sm" variant="outline" disabled={pagina >= dados.totalPages} onClick={() => setPagina(pagina + 1)}>
            Próxima
          </Button>
        </nav>
      )}
    </div>
  );
}

function FormularioProgramacao(props: {
  edicao: Edicao;
  midias: MidiaDto[] | null;
  ocupado: boolean;
  onMudar: (form: FormProgramacao) => void;
  onSalvar: () => void;
  onCancelar: () => void;
}) {
  const { form } = props.edicao;
  const mudar = (parcial: Partial<FormProgramacao>) => props.onMudar({ ...form, ...parcial });
  const calculo = calcular(duracaoDaMidia(props.midias, props.edicao.midiaAtual, form.midiaId), form.horaInicial, form.repeticoes);
  // Only active mídias are programmable; the current one stays listed when editing so the select does not go blank,
  // and without the mídia list (no Midias.View) it is the only option.
  const atual = props.edicao.midiaAtual;
  const opcoes =
    props.midias !== null
      ? props.midias.filter((m) => m.ativa || m.id === form.midiaId).map((m) => ({ id: m.id, rotulo: `${m.descricao} (${m.duracaoSegundos} s)${m.ativa ? "" : " (inativa)"}` }))
      : atual !== null
        ? [{ id: atual.id, rotulo: `${atual.descricao} (${atual.duracaoSegundos} s)` }]
        : [];

  return (
    <section className="space-y-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-edicao-programacao>
      <h2 className="font-medium">{props.edicao.id === null ? "Nova programação" : `Editar ${form.nome}`}</h2>
      {props.midias === null && (
        <p role="note" className="text-sm text-[var(--color-destructive)]">
          {MENSAGEM_SEM_LISTA_DE_MIDIAS}
        </p>
      )}
      <div className="grid gap-3 sm:grid-cols-3">
        <Campo rotulo="Nome">{(id) => <Input id={id} value={form.nome} maxLength={NOME_MAXIMO} onChange={(e) => mudar({ nome: e.target.value })} />}</Campo>
        <Campo rotulo="Mídia">
          {(id) => (
            <select id={id} className={SELECT_CLASSES} value={form.midiaId} onChange={(e) => mudar({ midiaId: e.target.value })}>
              <option value="">Escolha…</option>
              {opcoes.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.rotulo}
                </option>
              ))}
            </select>
          )}
        </Campo>
        <Campo rotulo="Repetições">{(id) => <Input id={id} inputMode="numeric" value={form.repeticoes} onChange={(e) => mudar({ repeticoes: e.target.value })} />}</Campo>
        <Campo rotulo="Data inicial">{(id) => <Input id={id} type="date" value={form.dataInicial} onChange={(e) => mudar({ dataInicial: e.target.value })} />}</Campo>
        <Campo rotulo="Data final">{(id) => <Input id={id} type="date" value={form.dataFinal} onChange={(e) => mudar({ dataFinal: e.target.value })} />}</Campo>
        <Campo rotulo="Hora inicial">{(id) => <Input id={id} type="time" step={1} value={form.horaInicial} onChange={(e) => mudar({ horaInicial: e.target.value })} />}</Campo>
      </div>
      <SeletorDias legenda="Dias da semana" mascara={form.dias} onChange={(dias) => mudar({ dias })} />

      <dl className="grid gap-2 rounded-md border border-[var(--color-border)] p-3 text-sm sm:grid-cols-3" data-calculo>
        <div>
          <dt className="text-[var(--color-muted-foreground)]">Tempo da mídia</dt>
          <dd data-tempo-midia>{calculo ? duracaoLegivel(calculo.tempoMidiaSegundos) : "—"}</dd>
        </div>
        <div>
          <dt className="text-[var(--color-muted-foreground)]">Tempo total</dt>
          <dd data-tempo-total>{calculo ? duracaoLegivel(calculo.tempoTotalSegundos) : "—"}</dd>
        </div>
        <div>
          <dt className="text-[var(--color-muted-foreground)]">Hora final</dt>
          <dd data-hora-final-calculada>{calculo ? horaDosSegundos(calculo.horaFinalSegundos) : "—"}</dd>
        </div>
      </dl>
      {calculo?.passaDaMeiaNoite && (
        <p role="alert" className="text-sm text-[var(--color-destructive)]">
          Termina depois da meia-noite: reduza as repetições ou antecipe a hora inicial.
        </p>
      )}

      <div className="flex gap-2">
        <Button disabled={props.ocupado || calculo?.passaDaMeiaNoite === true} onClick={props.onSalvar}>
          Salvar
        </Button>
        <Button variant="outline" disabled={props.ocupado} onClick={props.onCancelar}>
          Cancelar
        </Button>
      </div>
    </section>
  );
}
