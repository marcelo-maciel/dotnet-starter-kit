import { useId, useMemo, useState, type CSSProperties } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { formatarRazao } from "@/features/paf/aparencia/contraste";
import {
  adicionarLinha,
  ativarFila,
  atualizarFila,
  atualizarLinha,
  criarFila,
  criarSetor,
  listarFilas,
  listarPrioridades,
  listarSetores,
  renomearSetor,
  type DadosFila,
  type FilaDto,
  type PrioridadeDto,
  type SetorDto,
} from "@/features/paf/filas/filas-api";
import {
  filasPorSetor,
  normalizarAparencia,
  paresDeContraste,
  problemasDaFila,
  rotulosSemCobertura,
  type FilaForm,
  type LinhaForm,
} from "@/features/paf/filas/filas-formulario";

// Fila cadastro of the current unidade (RF02): grid of prioridades with letter and button color, appearance with a
// live preview and the RNF19 contrast floor, setor kept inside this screen (decided 24/08/2026) and the coverage warning.

const CHAVES = { filas: ["paf", "filas"], prioridades: ["paf", "prioridades"], setores: ["paf", "setores"] } as const;

type Edicao = { filaId: string | null; setorId: string | null; form: FilaForm; originais: Map<string, LinhaForm> };

function horaCurta(hora: string | null): string {
  return hora ? hora.slice(0, 5) : "";
}

function novaEdicao(): Edicao {
  return {
    filaId: null,
    setorId: null,
    originais: new Map(),
    form: {
      nome: "",
      nomeExibicao: "",
      semRestricao: true,
      horaInicial: "",
      horaFinal: "",
      aparencia: { fonte: "", corFonte: "", corBotaoSelecionado: "" },
      linhas: [],
    },
  };
}

function edicaoDe(fila: FilaDto, nomePrioridade: (id: string) => string): Edicao {
  const linhas: LinhaForm[] = fila.prioridades.map((p) => ({
    prioridadeId: p.prioridadeId,
    prioridade: nomePrioridade(p.prioridadeId),
    letra: p.letra,
    numeroInicial: p.numeroInicial,
    corBotao: p.corBotao ?? "",
  }));
  return {
    filaId: fila.id,
    setorId: fila.setorId,
    originais: new Map(linhas.map((l) => [l.prioridadeId, l])),
    form: {
      nome: fila.nome,
      nomeExibicao: fila.nomeExibicao,
      semRestricao: fila.horaInicial === null,
      horaInicial: horaCurta(fila.horaInicial),
      horaFinal: horaCurta(fila.horaFinal),
      aparencia: {
        fonte: fila.aparencia.fonte ?? "",
        corFonte: fila.aparencia.corFonte ?? "",
        corBotaoSelecionado: fila.aparencia.corBotaoSelecionado ?? "",
      },
      linhas,
    },
  };
}

function corOuNula(cor: string | null): string | null {
  return cor === null || cor.trim() === "" ? null : cor.trim();
}

async function salvar(edicao: Edicao): Promise<void> {
  const { form } = edicao;
  const dados: DadosFila = {
    nome: form.nome.trim(),
    nomeExibicao: form.nomeExibicao.trim(),
    horaInicial: form.semRestricao ? null : `${form.horaInicial}:00`,
    horaFinal: form.semRestricao ? null : `${form.horaFinal}:00`,
    setorId: edicao.setorId,
    aparencia: normalizarAparencia(form.aparencia),
  };
  const linhas = form.linhas.map((l, ordem) => ({
    prioridadeId: l.prioridadeId,
    letra: l.letra.trim(),
    numeroInicial: l.numeroInicial,
    ordem,
    corBotao: corOuNula(l.corBotao),
  }));
  if (edicao.filaId === null) {
    await criarFila(dados, linhas);
    return;
  }
  // The server checks each side of a contrast pair against the committed other side: changing the font and a
  // button color together may need two saves, and the message names the pair.
  await atualizarFila(edicao.filaId, dados);
  for (const linha of linhas) {
    const original = edicao.originais.get(linha.prioridadeId);
    if (!original) await adicionarLinha(edicao.filaId, linha);
    else await atualizarLinha(edicao.filaId, linha);
  }
}

export function FilasPage() {
  const queryClient = useQueryClient();
  const filas = useQuery({ queryKey: CHAVES.filas, queryFn: listarFilas });
  const prioridades = useQuery({ queryKey: CHAVES.prioridades, queryFn: listarPrioridades });
  const setores = useQuery({ queryKey: CHAVES.setores, queryFn: listarSetores });
  const [edicao, setEdicao] = useState<Edicao | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);

  const nomePrioridade = useMemo(() => {
    const nomes = new Map((prioridades.data ?? []).map((p) => [p.id, p.nome]));
    return (id: string) => nomes.get(id) ?? "?";
  }, [prioridades.data]);

  async function executar(acao: () => Promise<unknown>, depois: () => void) {
    setOcupado(true);
    setErro(null);
    try {
      await acao();
      depois();
    } catch (e) {
      setErro(mensagemAmigavel(e));
    } finally {
      setOcupado(false);
      await queryClient.invalidateQueries({ queryKey: ["paf"] });
    }
  }

  const carregando = filas.isPending || prioridades.isPending || setores.isPending;
  const falha = filas.error ?? prioridades.error ?? setores.error;

  return (
    <div className="space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Filas</h1>
        <p className="text-sm text-[var(--color-muted-foreground)]">
          Filas da unidade corrente, com a grade de prioridades, a aparência dos botões e os setores.
        </p>
      </header>

      {falha && <p role="alert" className="text-sm text-[var(--color-destructive)]">{mensagemAmigavel(falha)}</p>}
      {erro && <p role="alert" className="text-sm text-[var(--color-destructive)]">{erro}</p>}
      {carregando && <p role="status">Carregando…</p>}

      {!carregando && !falha && (
        <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
          <section aria-labelledby="titulo-lista" className="space-y-3">
            <div className="flex items-center justify-between">
              <h2 id="titulo-lista" className="text-lg font-medium">Cadastradas</h2>
              <Button onClick={() => setEdicao(novaEdicao())} disabled={ocupado}>Nova fila</Button>
            </div>
            <ListaFilas
              filas={filas.data ?? []}
              setores={setores.data ?? []}
              nomePrioridade={nomePrioridade}
              ocupado={ocupado}
              onEditar={(f) => setEdicao(edicaoDe(f, nomePrioridade))}
              onAtivar={(f) => executar(() => ativarFila(f.id, !f.ativa), () => undefined)}
            />
            <PainelSetores
              setores={setores.data ?? []}
              filas={filas.data ?? []}
              ocupado={ocupado}
              executar={executar}
            />
          </section>

          {edicao && (
            <FormularioFila
              edicao={edicao}
              prioridades={prioridades.data ?? []}
              setores={setores.data ?? []}
              ocupado={ocupado}
              onMudar={setEdicao}
              onCancelar={() => setEdicao(null)}
              onSalvar={() => executar(() => salvar(edicao), () => setEdicao(null))}
            />
          )}
        </div>
      )}
    </div>
  );
}

function ListaFilas(props: {
  filas: FilaDto[];
  setores: SetorDto[];
  nomePrioridade: (id: string) => string;
  ocupado: boolean;
  onEditar: (fila: FilaDto) => void;
  onAtivar: (fila: FilaDto) => void;
}) {
  if (props.filas.length === 0) return <p className="text-sm">Nenhuma fila cadastrada nesta unidade.</p>;
  const nomeSetor = (id: string | null) => props.setores.find((s) => s.id === id)?.nome ?? "Sem setor";
  return (
    <ul className="space-y-2">
      {props.filas.map((f) => {
        const semCobertura = rotulosSemCobertura(f, props.nomePrioridade);
        return (
          <li key={f.id} className="rounded-lg border border-[var(--color-border)] p-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <p className="font-medium">{f.nome} {!f.ativa && <span className="text-xs">(inativa)</span>}</p>
                <p className="text-xs text-[var(--color-muted-foreground)]">
                  {nomeSetor(f.setorId)} · {f.horaInicial ? `${horaCurta(f.horaInicial)} às ${horaCurta(f.horaFinal)}` : "sem restrição de horário"} ·{" "}
                  {f.prioridades.map((p) => `${props.nomePrioridade(p.prioridadeId)} ${p.letra}`).join(", ")}
                </p>
              </div>
              <div className="flex gap-2">
                <Button size="sm" variant="outline" disabled={props.ocupado} onClick={() => props.onEditar(f)}>Editar</Button>
                <Button size="sm" variant="ghost" disabled={props.ocupado} onClick={() => props.onAtivar(f)}>
                  {f.ativa ? "Desativar" : "Ativar"}
                </Button>
              </div>
            </div>
            {semCobertura.length > 0 && (
              <p role="note" className="mt-2 text-xs text-[var(--color-destructive)]">
                Sem guichê que atenda: {semCobertura.join(", ")}. Esses botões ficam fora do totem até um guichê ativo cobrir a combinação.
              </p>
            )}
          </li>
        );
      })}
    </ul>
  );
}

function PainelSetores(props: {
  setores: SetorDto[];
  filas: FilaDto[];
  ocupado: boolean;
  executar: (acao: () => Promise<unknown>, depois: () => void) => Promise<void>;
}) {
  const idNovo = useId();
  const [novo, setNovo] = useState("");
  const [renomeando, setRenomeando] = useState<{ id: string; nome: string } | null>(null);
  const grupos = filasPorSetor(props.setores, props.filas);
  return (
    <section aria-labelledby="titulo-setores" className="space-y-3 rounded-lg border border-[var(--color-border)] p-3">
      <h2 id="titulo-setores" className="text-lg font-medium">Setores</h2>
      <p className="text-xs text-[var(--color-muted-foreground)]">
        O setor só agrupa filas nos relatórios. A fila escolhe o setor no próprio cadastro; sem setor é o caso normal.
      </p>
      <ul className="space-y-1 text-sm">
        {grupos.map((g) => (
          <li key={g.setorId ?? "sem"} className="flex flex-wrap items-center justify-between gap-2">
            {renomeando && renomeando.id === g.setorId ? (
              <form
                className="flex gap-2"
                onSubmit={(e) => {
                  e.preventDefault();
                  void props.executar(() => renomearSetor(renomeando.id, renomeando.nome.trim()), () => setRenomeando(null));
                }}
              >
                <Input aria-label="Novo nome do setor" value={renomeando.nome} maxLength={120}
                  onChange={(e) => setRenomeando({ id: renomeando.id, nome: e.target.value })} />
                <Button size="sm" type="submit" disabled={props.ocupado || renomeando.nome.trim() === ""}>Salvar</Button>
                <Button size="sm" type="button" variant="ghost" onClick={() => setRenomeando(null)}>Cancelar</Button>
              </form>
            ) : (
              <span>
                <strong>{g.nome}</strong>: {g.filas.length > 0 ? g.filas.join(", ") : "nenhuma fila"}
              </span>
            )}
            {g.setorId && !renomeando && (
              <Button size="xs" variant="ghost" disabled={props.ocupado} onClick={() => setRenomeando({ id: g.setorId!, nome: g.nome })}>
                Renomear
              </Button>
            )}
          </li>
        ))}
      </ul>
      <form
        className="flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void props.executar(() => criarSetor(novo.trim()), () => setNovo(""));
        }}
      >
        <div className="grow space-y-1">
          <Label htmlFor={idNovo}>Novo setor</Label>
          <Input id={idNovo} value={novo} maxLength={120} onChange={(e) => setNovo(e.target.value)} />
        </div>
        <Button type="submit" disabled={props.ocupado || novo.trim() === ""}>Criar setor</Button>
      </form>
    </section>
  );
}

function CampoCor(props: { rotulo: string; valor: string; onMudar: (valor: string) => void }) {
  const id = useId();
  const valido = /^#[0-9a-fA-F]{6}$/.test(props.valor);
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{props.rotulo}</Label>
      <div className="flex gap-2">
        <Input id={id} value={props.valor} placeholder="padrão da estação" maxLength={7}
          onChange={(e) => props.onMudar(e.target.value)} />
        <input type="color" aria-label={`${props.rotulo}: seletor`} className="h-9 w-10 rounded border"
          value={valido ? props.valor : "#000000"} onChange={(e) => props.onMudar(e.target.value.toUpperCase())} />
      </div>
    </div>
  );
}

function FormularioFila(props: {
  edicao: Edicao;
  prioridades: PrioridadeDto[];
  setores: SetorDto[];
  ocupado: boolean;
  onMudar: (edicao: Edicao) => void;
  onCancelar: () => void;
  onSalvar: () => void;
}) {
  const { edicao } = props;
  const { form } = edicao;
  const ids = { nome: useId(), exibicao: useId(), inicio: useId(), fim: useId(), fonte: useId(), setor: useId(), adicionar: useId() };
  const problemas = problemasDaFila(form);
  const aparencia = normalizarAparencia(form.aparencia);
  const pares = paresDeContraste(aparencia, form.linhas.map((l) => ({ ...l, corBotao: corOuNula(l.corBotao) })));
  const foraDaGrade = props.prioridades.filter((p) => !form.linhas.some((l) => l.prioridadeId === p.id));

  const mudarForm = (mudanca: Partial<FilaForm>) => props.onMudar({ ...edicao, form: { ...form, ...mudanca } });
  const mudarLinha = (indice: number, mudanca: Partial<LinhaForm>) =>
    mudarForm({ linhas: form.linhas.map((l, i) => (i === indice ? { ...l, ...mudanca } : l)) });
  const mover = (indice: number, passo: number) => {
    const linhas = [...form.linhas];
    const [linha] = linhas.splice(indice, 1);
    linhas.splice(indice + passo, 0, linha);
    mudarForm({ linhas });
  };

  return (
    <section aria-labelledby="titulo-form" className="space-y-4 rounded-lg border border-[var(--color-border)] p-4">
      <h2 id="titulo-form" className="text-lg font-medium">{edicao.filaId ? `Editar ${form.nome}` : "Nova fila"}</h2>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <Label htmlFor={ids.nome}>Nome</Label>
          <Input id={ids.nome} value={form.nome} maxLength={120} onChange={(e) => mudarForm({ nome: e.target.value })} />
        </div>
        <div className="space-y-1">
          <Label htmlFor={ids.exibicao}>Nome de exibição</Label>
          <Input id={ids.exibicao} value={form.nomeExibicao} maxLength={120} onChange={(e) => mudarForm({ nomeExibicao: e.target.value })} />
        </div>
        <div className="space-y-1">
          <Label htmlFor={ids.setor}>Setor</Label>
          <select id={ids.setor} className="h-9 w-full rounded-lg border border-[var(--color-input)] bg-transparent px-2"
            value={edicao.setorId ?? ""} onChange={(e) => props.onMudar({ ...edicao, setorId: e.target.value || null })}>
            <option value="">Sem setor</option>
            {props.setores.map((s) => <option key={s.id} value={s.id}>{s.nome}</option>)}
          </select>
        </div>
      </div>

      <fieldset className="space-y-2">
        <legend className="text-sm font-medium">Horário de emissão</legend>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={form.semRestricao} onChange={(e) => mudarForm({ semRestricao: e.target.checked })} />
          Sem restrição
        </label>
        {!form.semRestricao && (
          <div className="flex gap-3">
            <div className="space-y-1">
              <Label htmlFor={ids.inicio}>Hora inicial</Label>
              <Input id={ids.inicio} type="time" value={form.horaInicial} onChange={(e) => mudarForm({ horaInicial: e.target.value })} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={ids.fim}>Hora final</Label>
              <Input id={ids.fim} type="time" value={form.horaFinal} onChange={(e) => mudarForm({ horaFinal: e.target.value })} />
            </div>
          </div>
        )}
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="text-sm font-medium">Grade de prioridades</legend>
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left">
              <th>Prioridade</th><th>Letra</th><th>Nº inicial</th><th>Cor do botão</th><th><span className="sr-only">Ordem</span></th>
            </tr>
          </thead>
          <tbody>
            {form.linhas.map((l, i) => (
              <tr key={l.prioridadeId}>
                <td>{l.prioridade}</td>
                <td><Input aria-label={`Letra de ${l.prioridade}`} value={l.letra} maxLength={5} className="w-20"
                  onChange={(e) => mudarLinha(i, { letra: e.target.value.toUpperCase() })} /></td>
                <td><Input aria-label={`Número inicial de ${l.prioridade}`} type="number" min={0} value={l.numeroInicial} className="w-24"
                  onChange={(e) => mudarLinha(i, { numeroInicial: Number(e.target.value) })} /></td>
                <td><CampoCor rotulo={`Cor de ${l.prioridade}`} valor={l.corBotao ?? ""} onMudar={(c) => mudarLinha(i, { corBotao: c })} /></td>
                <td className="whitespace-nowrap">
                  <Button size="icon-xs" variant="ghost" aria-label={`Subir ${l.prioridade}`} disabled={i === 0} onClick={() => mover(i, -1)}>↑</Button>
                  <Button size="icon-xs" variant="ghost" aria-label={`Descer ${l.prioridade}`} disabled={i === form.linhas.length - 1} onClick={() => mover(i, 1)}>↓</Button>
                  {!edicao.originais.has(l.prioridadeId) && (
                    <Button size="xs" variant="ghost" onClick={() => mudarForm({ linhas: form.linhas.filter((_, j) => j !== i) })}>Tirar</Button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {foraDaGrade.length > 0 && (
          <div className="flex items-end gap-2">
            <div className="space-y-1">
              <Label htmlFor={ids.adicionar}>Adicionar prioridade</Label>
              <select id={ids.adicionar} className="h-9 rounded-lg border border-[var(--color-input)] bg-transparent px-2" value=""
                onChange={(e) => {
                  const p = props.prioridades.find((x) => x.id === e.target.value);
                  if (p) mudarForm({ linhas: [...form.linhas, { prioridadeId: p.id, prioridade: p.nome, letra: "", numeroInicial: 1, corBotao: "" }] });
                }}>
                <option value="">Escolha…</option>
                {foraDaGrade.map((p) => <option key={p.id} value={p.id}>{p.nome}{p.ativa ? "" : " (inativa)"}</option>)}
              </select>
            </div>
          </div>
        )}
      </fieldset>

      <fieldset className="grid gap-3 sm:grid-cols-3">
        <legend className="mb-2 text-sm font-medium">Aparência</legend>
        <div className="space-y-1">
          <Label htmlFor={ids.fonte}>Fonte</Label>
          <Input id={ids.fonte} value={form.aparencia.fonte ?? ""} placeholder="padrão da estação" maxLength={64}
            onChange={(e) => mudarForm({ aparencia: { ...form.aparencia, fonte: e.target.value } })} />
        </div>
        <CampoCor rotulo="Cor da fonte" valor={form.aparencia.corFonte ?? ""}
          onMudar={(c) => mudarForm({ aparencia: { ...form.aparencia, corFonte: c } })} />
        <CampoCor rotulo="Cor do botão da fila selecionada" valor={form.aparencia.corBotaoSelecionado ?? ""}
          onMudar={(c) => mudarForm({ aparencia: { ...form.aparencia, corBotaoSelecionado: c } })} />
      </fieldset>

      <PreVisualizacao form={form} pares={pares} />

      {problemas.length > 0 && (
        <ul role="alert" className="list-disc pl-5 text-sm text-[var(--color-destructive)]">
          {problemas.map((p) => <li key={p}>{p}</li>)}
        </ul>
      )}

      <div className="flex gap-2">
        <Button onClick={props.onSalvar} disabled={props.ocupado || problemas.length > 0}>Salvar</Button>
        <Button variant="ghost" onClick={props.onCancelar} disabled={props.ocupado}>Cancelar</Button>
      </div>
    </section>
  );
}

function PreVisualizacao(props: { form: FilaForm; pares: ReturnType<typeof paresDeContraste> }) {
  const aparencia = normalizarAparencia(props.form.aparencia);
  const estilo = (fundo: string | null) => ({
    backgroundColor: fundo ?? undefined,
    color: aparencia.corFonte ?? undefined,
    fontFamily: aparencia.fonte ?? undefined,
  });
  const razaoDe = (botao: string) => props.pares.find((p) => p.botao === botao);
  return (
    <div aria-label="Pré-visualização dos botões do totem" className="space-y-2 rounded-lg bg-[var(--color-muted)] p-3">
      <p className="text-xs font-medium">Pré-visualização</p>
      <div className="flex flex-wrap gap-2">
        <BotaoAmostra rotulo={props.form.nomeExibicao || "Fila"} estilo={estilo(aparencia.corBotaoSelecionado)} par={razaoDe("fila selecionada")} />
        {props.form.linhas.map((l) => (
          <BotaoAmostra key={l.prioridadeId} rotulo={`${l.prioridade} ${l.letra}`} estilo={estilo(corOuNula(l.corBotao))} par={razaoDe(l.prioridade)} />
        ))}
      </div>
    </div>
  );
}

function BotaoAmostra(props: { rotulo: string; estilo: CSSProperties; par: { razao: number; suficiente: boolean } | undefined }) {
  return (
    <div className="flex flex-col items-center gap-1">
      <span className="min-h-11 min-w-24 rounded-lg border px-4 py-3 text-center text-lg" style={props.estilo}>{props.rotulo}</span>
      {props.par && (
        <span className={props.par.suficiente ? "text-xs" : "text-xs text-[var(--color-destructive)]"}>
          {formatarRazao(props.par.razao)}{props.par.suficiente ? "" : " (abaixo de 4,5:1)"}
        </span>
      )}
    </div>
  );
}
