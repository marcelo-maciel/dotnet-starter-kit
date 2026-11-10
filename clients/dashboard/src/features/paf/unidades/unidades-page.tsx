import { useId, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  atualizarUnidade,
  criarUnidade,
  desativarUnidade,
  listarUnidadesDoCadastro,
  obterAdministracaoUnidades,
  reativarUnidade,
  type UnidadeCadastroDto,
} from "@/features/paf/unidades/unidades-api";
import { acoesDaUnidade, AVISO_CONTRATO, resumoContrato } from "@/features/paf/unidades/unidades-regras";

const CHAVE_UNIDADES = ["paf", "unidades", "cadastro"] as const;
const CHAVE_ADMINISTRACAO = ["paf", "unidades", "administracao"] as const;

function textoOuNulo(valor: string): string | null {
  const limpo = valor.trim();
  return limpo === "" ? null : limpo;
}

export function UnidadesPage() {
  const queryClient = useQueryClient();
  const unidades = useQuery({ queryKey: CHAVE_UNIDADES, queryFn: listarUnidadesDoCadastro });
  const administracao = useQuery({ queryKey: CHAVE_ADMINISTRACAO, queryFn: obterAdministracaoUnidades });
  // Until the server answers, no escopo empresa button shows: the server is the one that decides (RF23).
  const administra = administracao.data?.administraUnidades ?? false;

  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [nome, setNome] = useState("");
  const [documento, setDocumento] = useState("");
  const [emEdicao, setEmEdicao] = useState<string | null>(null);
  const [nomeEditado, setNomeEditado] = useState("");
  const idNome = useId();
  const idDocumento = useId();

  const executar = async (acao: () => Promise<unknown>) => {
    setOcupado(true);
    setErro(null);
    try {
      await acao();
      await queryClient.invalidateQueries({ queryKey: CHAVE_UNIDADES });
      return true;
    } catch (e) {
      setErro(mensagemAmigavel(e));
      return false;
    } finally {
      setOcupado(false);
    }
  };

  const criar = async () => {
    if (await executar(() => criarUnidade(nome.trim(), textoOuNulo(documento)))) {
      setNome("");
      setDocumento("");
    }
  };

  const salvarNome = async (u: UnidadeCadastroDto) => {
    if (await executar(() => atualizarUnidade(u.id, nomeEditado.trim(), u.documentoFiscal))) {
      setEmEdicao(null);
    }
  };

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Unidades</h1>
      </header>

      <p role="note" data-aviso-contrato className="rounded-md border border-[var(--color-border)] px-4 py-3 text-sm">
        {AVISO_CONTRATO}
      </p>

      {erro && (
        <p role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erro}
        </p>
      )}

      {administra && (
        <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-nova-unidade>
          <h2 className="font-medium">Nova unidade</h2>
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <Label htmlFor={idNome}>Nome</Label>
              <Input id={idNome} value={nome} maxLength={160} onChange={(e) => setNome(e.target.value)} />
            </div>
            <div className="space-y-1">
              <Label htmlFor={idDocumento}>CNPJ (opcional)</Label>
              <Input id={idDocumento} value={documento} maxLength={32} onChange={(e) => setDocumento(e.target.value)} />
            </div>
          </div>
          <Button disabled={ocupado || nome.trim() === ""} onClick={() => void criar()}>
            Cadastrar unidade
          </Button>
        </section>
      )}

      {unidades.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {unidades.isError && <p role="alert">{mensagemAmigavel(unidades.error)}</p>}
      {unidades.data?.length === 0 && <p>Nenhuma unidade no seu escopo.</p>}

      <ul className="space-y-3">
        {(unidades.data ?? []).map((u) => {
          const acoes = acoesDaUnidade(administra, u.ativa);
          return (
            <li
              key={u.id}
              data-unidade={u.id}
              className="space-y-2 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4"
            >
              {emEdicao === u.id ? (
                <div className="flex flex-wrap items-center gap-2">
                  <Input
                    aria-label={`Novo nome de ${u.nome}`}
                    className="max-w-sm"
                    value={nomeEditado}
                    maxLength={160}
                    onChange={(e) => setNomeEditado(e.target.value)}
                  />
                  <Button size="sm" disabled={ocupado || nomeEditado.trim() === ""} onClick={() => void salvarNome(u)}>
                    Salvar
                  </Button>
                  <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setEmEdicao(null)}>
                    Cancelar
                  </Button>
                </div>
              ) : (
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{u.nome}</span>
                  {u.documentoFiscal && <span className="text-sm text-[var(--color-muted-foreground)]">{u.documentoFiscal}</span>}
                  {!u.ativa && <span className="text-sm text-[var(--color-destructive)]">Inativa</span>}
                </div>
              )}
              <p className="text-sm text-[var(--color-muted-foreground)]" data-contrato>
                {resumoContrato(u.contrato)}
              </p>
              <div className="flex flex-wrap gap-2">
                {acoes.renomear && emEdicao !== u.id && (
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={ocupado}
                    onClick={() => {
                      setEmEdicao(u.id);
                      setNomeEditado(u.nome);
                    }}
                  >
                    Renomear
                  </Button>
                )}
                {acoes.desativar && (
                  <Button size="sm" variant="destructive" disabled={ocupado} onClick={() => void executar(() => desativarUnidade(u.id))}>
                    Desativar
                  </Button>
                )}
                {acoes.reativar && (
                  <Button size="sm" variant="secondary" disabled={ocupado} onClick={() => void executar(() => reativarUnidade(u.id))}>
                    Reativar
                  </Button>
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
