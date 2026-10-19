import { useId, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { listarUnidadesDoCadastro, type UnidadeCadastroDto } from "@/features/paf/unidades/unidades-api";
import {
  atualizarUsuario,
  copiarPerfil,
  criarUsuario,
  definirEscopo,
  desativarUsuario,
  listarAtendimentosEmCurso,
  listarUsuarios,
  PAPEIS_PAF,
  type UsuarioPafDto,
} from "@/features/paf/usuarios/usuarios-api";
import { PermissoesUsuario } from "@/features/paf/usuarios/permissoes-usuario";
import { dominioDoEmail, moverUnidade, ROTULO_PRINCIPAL, sugestaoDeEmail, unidadesRetiradas } from "@/features/paf/usuarios/usuarios-regras";

const CHAVE_USUARIOS = ["paf", "usuarios"] as const;
const CHAVE_UNIDADES = ["paf", "unidades", "cadastro"] as const;

type Modo = "editar" | "escopo" | "copiar" | "desativar";

function textoOuNulo(valor: string): string | null {
  const limpo = valor.trim();
  return limpo === "" ? null : limpo;
}

/** Ordered scope editor (RF08): the first row is the principal, said in words; up and down buttons reorder. */
function EditorEscopo(props: {
  unidades: string[];
  opcoes: UnidadeCadastroDto[];
  onChange: (unidades: string[]) => void;
  escopoEmpresa: boolean;
}) {
  const { unidades, opcoes, onChange, escopoEmpresa } = props;
  const nome = (id: string) => opcoes.find((o) => o.id === id)?.nome ?? "Unidade fora da sua lista";
  const disponiveis = opcoes.filter((o) => o.ativa && !unidades.includes(o.id));
  // Escopo empresa sees every unidade; it keeps only the principal (RF08).
  const podeAcrescentar = !escopoEmpresa || unidades.length === 0;
  return (
    <div className="space-y-2" data-editor-escopo>
      <ol className="space-y-1">
        {unidades.map((id, indice) => (
          <li key={id} data-unidade-escopo={id} className="flex flex-wrap items-center gap-2 text-sm">
            <span className="font-medium">{nome(id)}</span>
            {indice === 0 && <span data-principal className="rounded bg-[var(--color-muted)] px-2 py-0.5 text-xs">{ROTULO_PRINCIPAL}</span>}
            <Button size="sm" variant="outline" aria-label={`Subir ${nome(id)}`} disabled={indice === 0} onClick={() => onChange(moverUnidade(unidades, indice, -1))}>
              ↑
            </Button>
            <Button
              size="sm"
              variant="outline"
              aria-label={`Descer ${nome(id)}`}
              disabled={indice === unidades.length - 1}
              onClick={() => onChange(moverUnidade(unidades, indice, 1))}
            >
              ↓
            </Button>
            <Button size="sm" variant="ghost" aria-label={`Retirar ${nome(id)}`} onClick={() => onChange(unidades.filter((u) => u !== id))}>
              Retirar
            </Button>
          </li>
        ))}
      </ol>
      {podeAcrescentar && disponiveis.length > 0 && (
        <select
          aria-label="Acrescentar unidade"
          className="rounded-md border border-[var(--color-border)] bg-transparent px-2 py-1 text-sm"
          value=""
          onChange={(e) => e.target.value && onChange([...unidades, e.target.value])}
        >
          <option value="">Acrescentar unidade…</option>
          {disponiveis.map((o) => (
            <option key={o.id} value={o.id}>
              {o.nome}
            </option>
          ))}
        </select>
      )}
    </div>
  );
}

/** RF08/RF22: before taking unidades out or deactivating, the screen shows the user's atendimentos in progress there. */
function AtendimentosEmCurso(props: { usuarioId: string; unidades: string[] | "todas"; opcoes: UnidadeCadastroDto[]; cancelar: boolean; onCancelar: (v: boolean) => void }) {
  const { usuarioId, unidades, opcoes, cancelar, onCancelar } = props;
  const idCancelar = useId();
  const consulta = useQuery({ queryKey: ["paf", "usuarios", usuarioId, "em-curso"], queryFn: () => listarAtendimentosEmCurso(usuarioId) });
  const afetados = (consulta.data ?? []).filter((a) => unidades === "todas" || unidades.includes(a.unidadeId));
  if (consulta.isPending) return <p className="text-sm text-[var(--color-muted-foreground)]">Conferindo atendimentos em curso…</p>;
  if (consulta.isError) return <p role="alert">{mensagemAmigavel(consulta.error)}</p>;
  if (afetados.length === 0) return <p className="text-sm text-[var(--color-muted-foreground)]">Nenhum atendimento em curso afetado.</p>;
  return (
    <div className="space-y-2 rounded-md border border-[var(--color-border)] p-3" data-em-curso>
      <p className="text-sm">Atendimentos em curso deste usuário:</p>
      <ul className="text-sm">
        {afetados.map((a) => (
          <li key={a.atendimentoId}>
            Senha {a.senha} · {a.estado === "EmAtendimento" ? "em atendimento" : "chamada"} · {opcoes.find((o) => o.id === a.unidadeId)?.nome ?? "outra unidade"}
          </li>
        ))}
      </ul>
      <div className="flex items-center gap-2">
        <input id={idCancelar} type="checkbox" checked={cancelar} onChange={(e) => onCancelar(e.target.checked)} />
        <Label htmlFor={idCancelar}>Liberar esses atendimentos (cancelar; o cliente volta pela reabilitação)</Label>
      </div>
    </div>
  );
}

export function UsuariosPage() {
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const usuarios = useQuery({ queryKey: CHAVE_USUARIOS, queryFn: listarUsuarios });
  const unidades = useQuery({ queryKey: CHAVE_UNIDADES, queryFn: listarUnidadesDoCadastro });
  const opcoes = unidades.data ?? [];

  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [aberto, setAberto] = useState<{ id: string; modo: Modo } | null>(null);
  const [permissoesDe, setPermissoesDe] = useState<string | null>(null);

  // Form state of the open panel and of the creation form.
  const [nome, setNome] = useState("");
  const [sobrenome, setSobrenome] = useState("");
  const [email, setEmail] = useState("");
  const [senha, setSenha] = useState("");
  const [confirmacao, setConfirmacao] = useState("");
  const [funcao, setFuncao] = useState("");
  const [ramal, setRamal] = useState("");
  const [papel, setPapel] = useState<string>("Atendente");
  const [escopo, setEscopo] = useState<string[]>([]);
  const [empresa, setEmpresa] = useState(false);
  const [origem, setOrigem] = useState("");
  const [cancelar, setCancelar] = useState(false);
  const ids = { nome: useId(), sobrenome: useId(), email: useId(), senha: useId(), confirmacao: useId(), funcao: useId(), ramal: useId(), papel: useId(), empresa: useId() };

  const executar = async (acao: () => Promise<unknown>) => {
    setOcupado(true);
    setErro(null);
    try {
      await acao();
      await queryClient.invalidateQueries({ queryKey: CHAVE_USUARIOS });
      return true;
    } catch (e) {
      setErro(mensagemAmigavel(e));
      return false;
    } finally {
      setOcupado(false);
    }
  };

  const limpar = () => {
    setNome("");
    setSobrenome("");
    setEmail("");
    setSenha("");
    setConfirmacao("");
    setFuncao("");
    setRamal("");
    setPapel("Atendente");
    setEscopo([]);
    setEmpresa(false);
    setOrigem("");
    setCancelar(false);
  };

  const abrir = (u: UsuarioPafDto, modo: Modo) => {
    limpar();
    setNome(u.nome);
    setSobrenome(u.sobrenome);
    setFuncao(u.funcao ?? "");
    setRamal(u.ramal ?? "");
    setEscopo(u.unidades);
    setEmpresa(u.escopoEmpresa);
    setAberto({ id: u.id, modo });
  };

  const criar = async () => {
    const novo = {
      nome: nome.trim(),
      sobrenome: sobrenome.trim(),
      email: email.trim(),
      senha,
      confirmacaoSenha: confirmacao,
      funcao: textoOuNulo(funcao),
      ramal: textoOuNulo(ramal),
      papel,
      unidades: escopo,
      escopoEmpresa: empresa,
    };
    if (await executar(() => criarUsuario(novo))) limpar();
  };

  const confirmar = async (u: UsuarioPafDto) => {
    if (aberto === null) return;
    const acoes: Record<Modo, () => Promise<unknown>> = {
      editar: () => atualizarUsuario(u.id, nome.trim(), sobrenome.trim(), textoOuNulo(funcao), textoOuNulo(ramal)),
      escopo: () => definirEscopo(u.id, escopo, empresa, cancelar),
      copiar: () => copiarPerfil(u.id, origem, cancelar),
      desativar: () => desativarUsuario(u.id, cancelar),
    };
    if (await executar(acoes[aberto.modo])) {
      setAberto(null);
      limpar();
    }
  };

  const sugestao = sugestaoDeEmail(nome, sobrenome, dominioDoEmail(user?.email));
  const lista = usuarios.data ?? [];
  const nomeDe = (id: string) => opcoes.find((o) => o.id === id)?.nome ?? "Unidade fora da sua lista";

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Usuários</h1>
        <p className="text-sm text-[var(--color-muted-foreground)]">
          Só aparecem os usuários cujas unidades estão todas no seu acesso. O login é o e-mail.
        </p>
      </header>

      {erro && (
        <p role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erro}
        </p>
      )}

      {aberto === null && (
        <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-novo-usuario>
          <h2 className="font-medium">Novo usuário</h2>
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <Label htmlFor={ids.nome}>Nome</Label>
              <Input id={ids.nome} value={nome} maxLength={100} onChange={(e) => setNome(e.target.value)} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={ids.sobrenome}>Sobrenome</Label>
              <Input id={ids.sobrenome} value={sobrenome} maxLength={100} onChange={(e) => setSobrenome(e.target.value)} />
            </div>
            <div className="space-y-1 sm:col-span-2">
              <Label htmlFor={ids.email}>E-mail (login)</Label>
              <div className="flex gap-2">
                <Input id={ids.email} type="email" value={email} maxLength={256} onChange={(e) => setEmail(e.target.value)} />
                {sugestao && email.trim() === "" && (
                  <Button size="sm" variant="outline" onClick={() => setEmail(sugestao)}>
                    Usar {sugestao}
                  </Button>
                )}
              </div>
            </div>
            <div className="space-y-1">
              <Label htmlFor={ids.senha}>Senha</Label>
              <Input id={ids.senha} type="password" autoComplete="new-password" value={senha} onChange={(e) => setSenha(e.target.value)} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={ids.confirmacao}>Confirmação da senha</Label>
              <Input id={ids.confirmacao} type="password" autoComplete="new-password" value={confirmacao} onChange={(e) => setConfirmacao(e.target.value)} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={ids.funcao}>Função (opcional)</Label>
              <Input id={ids.funcao} value={funcao} maxLength={120} onChange={(e) => setFuncao(e.target.value)} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={ids.ramal}>Ramal (opcional)</Label>
              <Input id={ids.ramal} value={ramal} maxLength={20} onChange={(e) => setRamal(e.target.value)} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={ids.papel}>Perfil</Label>
              <select
                id={ids.papel}
                className="w-full rounded-md border border-[var(--color-border)] bg-transparent px-2 py-2 text-sm"
                value={papel}
                onChange={(e) => setPapel(e.target.value)}
              >
                {PAPEIS_PAF.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </select>
            </div>
            <div className="flex items-center gap-2">
              <input id={ids.empresa} type="checkbox" checked={empresa} onChange={(e) => setEmpresa(e.target.checked)} />
              <Label htmlFor={ids.empresa}>Escopo empresa (todas as unidades, inclusive as futuras)</Label>
            </div>
          </div>
          <EditorEscopo unidades={escopo} opcoes={opcoes} onChange={setEscopo} escopoEmpresa={empresa} />
          <Button
            disabled={ocupado || nome.trim() === "" || sobrenome.trim() === "" || email.trim() === "" || senha === "" || escopo.length === 0}
            onClick={() => void criar()}
          >
            Cadastrar usuário
          </Button>
        </section>
      )}

      {usuarios.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {usuarios.isError && <p role="alert">{mensagemAmigavel(usuarios.error)}</p>}
      {usuarios.data?.length === 0 && <p>Nenhum usuário no seu escopo.</p>}

      <ul className="space-y-3">
        {lista.map((u) => (
          <li key={u.id} data-usuario={u.id} className="space-y-2 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium">
                {u.nome} {u.sobrenome}
              </span>
              <span className="text-sm text-[var(--color-muted-foreground)]">{u.email}</span>
              {!u.ativo && <span className="text-sm text-[var(--color-destructive)]">Inativo</span>}
            </div>
            <p className="text-sm text-[var(--color-muted-foreground)]">
              {[u.papeis.join(", ") || "Sem perfil do PAF", u.funcao, u.ramal && `ramal ${u.ramal}`, u.escopoEmpresa ? "escopo empresa" : null]
                .filter(Boolean)
                .join(" · ")}
            </p>
            <ol className="text-sm">
              {u.unidades.map((id, indice) => (
                <li key={id}>
                  {nomeDe(id)}
                  {indice === 0 && <span className="ml-2 text-xs">({ROTULO_PRINCIPAL})</span>}
                </li>
              ))}
            </ol>

            {aberto?.id === u.id ? (
              <div className="space-y-3 border-t border-[var(--color-border)] pt-3" data-painel={aberto.modo}>
                {aberto.modo === "editar" && (
                  <div className="grid gap-2 sm:grid-cols-2">
                    <Input aria-label="Nome" value={nome} maxLength={100} onChange={(e) => setNome(e.target.value)} />
                    <Input aria-label="Sobrenome" value={sobrenome} maxLength={100} onChange={(e) => setSobrenome(e.target.value)} />
                    <Input aria-label="Função" value={funcao} maxLength={120} onChange={(e) => setFuncao(e.target.value)} />
                    <Input aria-label="Ramal" value={ramal} maxLength={20} onChange={(e) => setRamal(e.target.value)} />
                  </div>
                )}
                {aberto.modo === "escopo" && (
                  <>
                    <EditorEscopo unidades={escopo} opcoes={opcoes} onChange={setEscopo} escopoEmpresa={empresa} />
                    <p className="text-sm text-[var(--color-muted-foreground)]">A nova ordem vale a partir do próximo login.</p>
                    {unidadesRetiradas(u.unidades, escopo).length > 0 && (
                      <AtendimentosEmCurso
                        usuarioId={u.id}
                        unidades={unidadesRetiradas(u.unidades, escopo)}
                        opcoes={opcoes}
                        cancelar={cancelar}
                        onCancelar={setCancelar}
                      />
                    )}
                  </>
                )}
                {aberto.modo === "copiar" && (
                  <>
                    <select
                      aria-label="Copiar perfil de"
                      className="rounded-md border border-[var(--color-border)] bg-transparent px-2 py-1 text-sm"
                      value={origem}
                      onChange={(e) => setOrigem(e.target.value)}
                    >
                      <option value="">Copiar perfil e unidades de…</option>
                      {lista
                        .filter((o) => o.id !== u.id)
                        .map((o) => (
                          <option key={o.id} value={o.id}>
                            {o.nome} {o.sobrenome}
                          </option>
                        ))}
                    </select>
                    {origem !== "" && (
                      <AtendimentosEmCurso
                        usuarioId={u.id}
                        unidades={unidadesRetiradas(u.unidades, lista.find((o) => o.id === origem)?.unidades ?? [])}
                        opcoes={opcoes}
                        cancelar={cancelar}
                        onCancelar={setCancelar}
                      />
                    )}
                  </>
                )}
                {aberto.modo === "desativar" && (
                  <>
                    <p className="text-sm">O usuário perde o acesso e todas as sessões dele caem agora.</p>
                    <AtendimentosEmCurso usuarioId={u.id} unidades="todas" opcoes={opcoes} cancelar={cancelar} onCancelar={setCancelar} />
                  </>
                )}
                <div className="flex gap-2">
                  <Button
                    size="sm"
                    variant={aberto.modo === "desativar" ? "destructive" : "default"}
                    disabled={ocupado || (aberto.modo === "copiar" && origem === "") || (aberto.modo === "escopo" && escopo.length === 0)}
                    onClick={() => void confirmar(u)}
                  >
                    Confirmar
                  </Button>
                  <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setAberto(null)}>
                    Cancelar
                  </Button>
                </div>
              </div>
            ) : (
              <div className="flex flex-wrap gap-2">
                <Button size="sm" variant="outline" disabled={ocupado} onClick={() => abrir(u, "editar")}>
                  Editar
                </Button>
                <Button size="sm" variant="outline" disabled={ocupado} onClick={() => abrir(u, "escopo")}>
                  Unidades
                </Button>
                <Button size="sm" variant="outline" disabled={ocupado} onClick={() => abrir(u, "copiar")}>
                  Copiar perfil
                </Button>
                <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setPermissoesDe(permissoesDe === u.id ? null : u.id)}>
                  Permissões
                </Button>
                {u.ativo && (
                  <Button size="sm" variant="destructive" disabled={ocupado} onClick={() => abrir(u, "desativar")}>
                    Desativar
                  </Button>
                )}
              </div>
            )}
            {permissoesDe === u.id && <PermissoesUsuario usuarioId={u.id} onFechar={() => setPermissoesDe(null)} />}
          </li>
        ))}
      </ul>
    </div>
  );
}
