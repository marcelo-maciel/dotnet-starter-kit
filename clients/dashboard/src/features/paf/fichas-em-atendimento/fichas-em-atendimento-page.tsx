import { useId, useState } from "react";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  cancelarAtendimento,
  listarFichasEmAtendimento,
  type FichaEmAtendimentoDto,
} from "@/features/paf/fichas-em-atendimento/fichas-em-atendimento-api";
import {
  formatarEntrada,
  formatarPermanencia,
  PERMISSAO_CANCELAR,
  rotuloEstado,
  textoTotal,
} from "@/features/paf/fichas-em-atendimento/fichas-em-atendimento-regras";

// ponytail: polling every 30 s plus the Atualizar button, no realtime. Subscribe to AtendimentoAtualizado on the
// PafHub when a supervisor needs the row to vanish the instant a guichê closes it.
const INTERVALO_ATUALIZACAO_MS = 30_000;

/** RF13: what is in progress in the current unit, longest stay first, so an abandoned atendimento stands out. */
export function FichasEmAtendimentoPage() {
  const queryClient = useQueryClient();
  const { user, permissionsHydrated } = useAuth();
  const podeCancelar = permissionsHydrated && (user?.permissions.includes(PERMISSAO_CANCELAR) ?? false);
  const [filaId, setFilaId] = useState<string | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState<string | null>(null);
  const idFila = useId();

  const chave = ["paf", "fichas-em-atendimento", filaId] as const;
  const lista = useQuery({
    queryKey: chave,
    queryFn: () => listarFichasEmAtendimento(filaId),
    refetchInterval: INTERVALO_ATUALIZACAO_MS,
    // The fila options come with every answer: keep the last ones on screen while another fila loads.
    placeholderData: keepPreviousData,
  });
  const filas = lista.data?.filas ?? [];

  const cancelar = async (linha: FichaEmAtendimentoDto) => {
    if (!window.confirm(`Cancelar o atendimento da senha ${linha.senha}? Os atendimentos dela em outras filas voltam a ser chamáveis.`)) return;
    setOcupado(linha.atendimentoId);
    setErro(null);
    try {
      await cancelarAtendimento(linha.atendimentoId);
    } catch (e) {
      setErro(mensagemAmigavel(e));
    } finally {
      setOcupado(null);
      await queryClient.invalidateQueries({ queryKey: ["paf", "fichas-em-atendimento"] });
    }
  };

  return (
    <div className="mx-auto max-w-5xl space-y-6">
      <header className="flex flex-wrap items-end justify-between gap-3">
        <h1 className="text-2xl font-semibold">Fichas em atendimento</h1>
        <div className="flex flex-wrap items-end gap-3">
          <div className="space-y-1">
            <Label htmlFor={idFila}>Fila</Label>
            <select
              id={idFila}
              className="h-9 rounded-lg border border-[var(--color-input)] bg-transparent px-2"
              value={filaId ?? ""}
              onChange={(e) => setFilaId(e.target.value === "" ? null : e.target.value)}
            >
              <option value="">Todas as filas</option>
              {filas.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.nome}
                </option>
              ))}
            </select>
          </div>
          <Button variant="outline" disabled={lista.isFetching} onClick={() => void lista.refetch()}>
            Atualizar
          </Button>
        </div>
      </header>

      {erro && (
        <p role="alert" className="rounded-md border border-[var(--color-destructive)] px-4 py-3 text-[var(--color-destructive)]">
          {erro}
        </p>
      )}

      {lista.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {lista.isError && <p role="alert">{mensagemAmigavel(lista.error)}</p>}
      {lista.data && (
        <p data-total className="font-medium">
          {textoTotal(lista.data.total)}
        </p>
      )}

      {lista.data && lista.data.atendimentos.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--color-border)] bg-[var(--color-card)]">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-[var(--color-border)] text-left">
                <th className="px-3 py-2">Guichê</th>
                <th className="px-3 py-2">Senha</th>
                <th className="px-3 py-2">Fila</th>
                <th className="px-3 py-2">Tipo</th>
                <th className="px-3 py-2">Estado</th>
                <th className="px-3 py-2">Entrada</th>
                <th className="px-3 py-2">Permanência</th>
                {podeCancelar && (
                  <th className="px-3 py-2">
                    <span className="sr-only">Ações</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {lista.data.atendimentos.map((a) => (
                <tr key={a.atendimentoId} data-atendimento={a.atendimentoId} className="border-b border-[var(--color-border)] last:border-0">
                  <td className="px-3 py-2">{a.guiche ?? "—"}</td>
                  <td className="px-3 py-2 font-medium">{a.senha}</td>
                  <td className="px-3 py-2">{a.fila}</td>
                  <td className="px-3 py-2">{a.prioridade}</td>
                  <td className="px-3 py-2">{rotuloEstado(a.estado)}</td>
                  <td className="px-3 py-2 whitespace-nowrap">{formatarEntrada(a.chamadoEm)}</td>
                  <td className="px-3 py-2 whitespace-nowrap" data-permanencia={a.permanenciaSegundos}>
                    {formatarPermanencia(a.permanenciaSegundos)}
                  </td>
                  {podeCancelar && (
                    <td className="px-3 py-2 text-right">
                      <Button
                        size="sm"
                        variant="destructive"
                        disabled={ocupado !== null}
                        onClick={() => void cancelar(a)}
                      >
                        Cancelar
                      </Button>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
