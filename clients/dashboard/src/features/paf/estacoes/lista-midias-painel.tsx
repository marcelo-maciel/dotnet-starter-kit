import { useId, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { ApiRequestError } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { obterMidiasDaEstacao, substituirMidiasDaEstacao, type MidiaResumoDto } from "@/features/paf/estacoes/estacoes-api";
import { adicionar, bloqueioDoSalvar, duracaoDoLaco, iguais, MAXIMO_ITENS, mover, remover } from "@/features/paf/estacoes/lista-midias-painel-regras";
import { duracaoLegivel } from "@/features/paf/programacoes/programacoes-regras";

const SELECT_CLASSES = "h-9 rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

function mensagensDoErro(erro: unknown): string[] {
  if (erro instanceof ApiRequestError && erro.problem?.errors !== undefined) {
    const errors = erro.problem.errors;
    const linhas = Array.isArray(errors) ? errors : Object.values(errors).flat();
    if (linhas.length > 0) return linhas;
  }
  return [mensagemAmigavel(erro)];
}

/**
 * M10: editor of a painel station's display list (RF04 painel block). Saved on its own (PUT estacoes/{id}/midias, a
 * whole replacement), not with the station form: the list has its own route and transaction (M06), and mixing both
 * would leave the operator guessing which half failed.
 */
export function ListaMidiasPainel({ estacaoId, midias }: { estacaoId: string; midias: MidiaResumoDto[] | null }) {
  const queryClient = useQueryClient();
  const chave = ["paf", "estacoes", estacaoId, "midias"];
  const salva = useQuery({ queryKey: chave, queryFn: () => obterMidiasDaEstacao(estacaoId), retry: false });
  const idEscolha = useId();
  const [rascunho, setRascunho] = useState<string[] | null>(null);
  const [escolhida, setEscolhida] = useState("");
  const [erros, setErros] = useState<string[]>([]);
  const [aviso, setAviso] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);

  if (salva.isPending) return <p className="text-sm text-[var(--color-muted-foreground)]">Carregando a lista de exibição…</p>;
  if (salva.isError) return <p role="alert" className="text-sm">Lista de exibição: {mensagemAmigavel(salva.error)}</p>;

  const salvos = salva.data.itens.map((i) => i.midiaId);
  const itens = rascunho ?? salvos;
  const alterada = !iguais(itens, salvos);
  // Entries already saved keep their description even when the mídia list is unavailable (no Midias.View).
  const descritas = [
    ...salva.data.itens.map((i) => ({ id: i.midiaId, descricao: i.descricao, tipo: i.tipo, duracaoSegundos: i.duracaoSegundos, ativa: i.ativa })),
    ...(midias ?? []),
  ];
  const descrever = (id: string) => (midias ?? []).find((m) => m.id === id) ?? descritas.find((m) => m.id === id);
  const ativas = (midias ?? []).filter((m) => m.ativa);
  const bloqueio = alterada ? bloqueioDoSalvar(itens, [...(midias ?? []), ...descritas]) : null;

  const mudar = (novos: string[]) => {
    setAviso(null);
    setErros([]);
    setRascunho(novos);
  };

  const salvar = async () => {
    setOcupado(true);
    setErros([]);
    setAviso(null);
    try {
      await substituirMidiasDaEstacao(estacaoId, itens);
      setRascunho(null);
      setAviso("Lista de exibição salva: o painel aberto relê a lista sozinho.");
    } catch (e) {
      setErros(mensagensDoErro(e));
    } finally {
      await queryClient.invalidateQueries({ queryKey: chave });
      setOcupado(false);
    }
  };

  return (
    <div className="space-y-2" data-lista-midias-painel>
      <p className="text-sm font-medium">Lista de exibição de mídias</p>
      <p className="text-sm text-[var(--color-muted-foreground)]">
        Toca em laço, na ordem abaixo, sempre que tiver itens; uma programação ativa se sobrepõe a ela na sua janela. Laço:{" "}
        <span data-duracao-laco>{duracaoLegivel(duracaoDoLaco(itens, descritas))}</span>.
      </p>
      {erros.length > 0 && (
        <ul role="alert" className="list-disc rounded-md border border-[var(--color-destructive)] py-2 pl-8 pr-4 text-sm text-[var(--color-destructive)]">
          {erros.map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      )}
      {aviso && (
        <p role="status" className="text-sm" data-aviso-lista>
          {aviso}
        </p>
      )}
      {itens.length === 0 ? (
        <p className="text-sm">Lista vazia: o painel mostra só as senhas, fora das programações.</p>
      ) : (
        <ol className="space-y-1">
          {itens.map((id, i) => {
            const midia = descrever(id);
            const nome = midia?.descricao ?? "Mídia desconhecida";
            return (
              <li key={`${i}-${id}`} data-item-lista={id} className="flex flex-wrap items-center gap-2 rounded-md border border-[var(--color-border)] px-3 py-1 text-sm">
                <span className="w-6 text-right tabular-nums">{i + 1}.</span>
                <span className="font-medium">{nome}</span>
                {midia && <span className="text-[var(--color-muted-foreground)]">{midia.duracaoSegundos} s</span>}
                {midia && !midia.ativa && <span className="text-[var(--color-destructive)]">inativa: pulada no painel</span>}
                <span className="ml-auto flex gap-1">
                  <Button type="button" size="sm" variant="outline" aria-label={`Subir ${nome} (posição ${i + 1})`} disabled={ocupado || i === 0} onClick={() => mudar(mover(itens, i, -1))}>
                    ↑
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="outline"
                    aria-label={`Descer ${nome} (posição ${i + 1})`}
                    disabled={ocupado || i === itens.length - 1}
                    onClick={() => mudar(mover(itens, i, 1))}
                  >
                    ↓
                  </Button>
                  <Button type="button" size="sm" variant="outline" aria-label={`Tirar ${nome} (posição ${i + 1})`} disabled={ocupado} onClick={() => mudar(remover(itens, i))}>
                    Tirar
                  </Button>
                </span>
              </li>
            );
          })}
        </ol>
      )}
      {midias === null ? (
        <p role="note" className="text-sm text-[var(--color-muted-foreground)]">
          Não foi possível listar as mídias da unidade: dá para reordenar e tirar itens, não para acrescentar.
        </p>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <label htmlFor={idEscolha} className="text-sm">
            Acrescentar
          </label>
          <select id={idEscolha} className={SELECT_CLASSES} value={escolhida} onChange={(e) => setEscolhida(e.target.value)}>
            <option value="">Escolha uma mídia ativa…</option>
            {ativas.map((m) => (
              <option key={m.id} value={m.id}>
                {m.descricao} ({m.duracaoSegundos} s)
              </option>
            ))}
          </select>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={ocupado || itens.length >= MAXIMO_ITENS}
            onClick={() => {
              const resultado = adicionar(itens, escolhida);
              if (resultado.ok) mudar(resultado.itens);
              else setErros([resultado.mensagem]);
            }}
          >
            Acrescentar à lista
          </Button>
        </div>
      )}
      {bloqueio && (
        <p role="alert" className="text-sm text-[var(--color-destructive)]" data-bloqueio-lista>
          {bloqueio}
        </p>
      )}
      <div className="flex gap-2">
        <Button type="button" size="sm" disabled={ocupado || !alterada || bloqueio !== null} onClick={() => void salvar()}>
          Salvar lista
        </Button>
        {alterada && (
          <Button type="button" size="sm" variant="outline" disabled={ocupado} onClick={() => mudar(salvos)}>
            Desfazer alterações
          </Button>
        )}
      </div>
    </div>
  );
}
