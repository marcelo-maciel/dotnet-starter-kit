import { useEffect, useId, useState } from "react";
import { Outlet } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { definirUnidadeCorrente, listarUnidades, obterPosto } from "@/features/paf/guiche/guiche-api";
import {
  apagarAvisoDeRecarga,
  lerAvisoDeRecarga,
  usePafTempoReal,
} from "@/features/paf/tempo-real/paf-tempo-real-contexto";

// Parent route of the PAF screens inside AppShell: the current-unit selector on top (RF08) and the notice of a
// switch made in another tab. Same query keys as the guichê page, so both read one cache entry.
const CHAVE_POSTO = ["paf", "guiche", "posto"] as const;
const CHAVE_UNIDADES = ["paf", "guiche", "unidades"] as const;

const SELECT_CLASSES =
  "h-9 rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

export function PafLayout() {
  const [aviso, setAviso] = useState(lerAvisoDeRecarga);
  // Read in the initializer, removed here: StrictMode runs initializers twice, and a reload shows it once.
  useEffect(() => apagarAvisoDeRecarga(), []);

  return (
    <div className="space-y-4">
      {aviso && (
        <div
          role="status"
          data-paf-aviso-troca
          className="flex items-center justify-between gap-3 rounded-md border border-[var(--color-border)] bg-[var(--color-card)] px-4 py-2 text-sm"
        >
          <span>{aviso}</span>
          <Button variant="link" size="xs" onClick={() => setAviso(null)}>
            Fechar
          </Button>
        </div>
      )}
      <SeletorUnidade />
      <Outlet />
    </div>
  );
}

function SeletorUnidade() {
  const queryClient = useQueryClient();
  const { unidadeTrocada } = usePafTempoReal();
  const id = useId();
  const [ocupado, setOcupado] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const posto = useQuery({ queryKey: CHAVE_POSTO, queryFn: obterPosto });
  const unidades = useQuery({ queryKey: CHAVE_UNIDADES, queryFn: listarUnidades });

  // Whoever has a single unidade, most users, never sees the selector (RF08). The list holds only active ones.
  const lista = unidades.data ?? [];
  if (!posto.data || lista.length <= 1) return null;

  const atual = posto.data.unidadeId ?? "";
  const guiche = posto.data.guiche;
  // A current unit deactivated after it was chosen is not in the list; it stays shown until the user leaves it.
  const atualForaDaLista = atual !== "" && !lista.some((u) => u.id === atual);

  const trocar = async (unidadeId: string) => {
    const destino = lista.find((u) => u.id === unidadeId);
    if (!destino || unidadeId === atual) return;
    // The switch releases the guichê (RF04): the user agrees to it first.
    if (guiche && !window.confirm(`Trocar de unidade solta o ${guiche}. Continuar?`)) return;
    setOcupado(true);
    setErro(null);
    try {
      await definirUnidadeCorrente(destino.id);
      unidadeTrocada(destino.id, destino.nome);
    } catch (e) {
      setErro(mensagemAmigavel(e));
    } finally {
      // Refused or not, every PAF read follows the server's current unit.
      await queryClient.invalidateQueries({ queryKey: ["paf"] });
      setOcupado(false);
    }
  };

  return (
    <div className="flex flex-wrap items-center gap-3" data-paf-seletor-unidade>
      <Label htmlFor={id}>Unidade</Label>
      <select
        id={id}
        className={SELECT_CLASSES}
        value={atual}
        disabled={ocupado}
        onChange={(e) => void trocar(e.target.value)}
      >
        {atual === "" && <option value="">Selecione a unidade</option>}
        {atualForaDaLista && <option value={atual}>{posto.data.unidade ?? atual}</option>}
        {lista.map((u) => (
          <option key={u.id} value={u.id}>
            {u.nome}
          </option>
        ))}
      </select>
      {erro && (
        <p role="alert" className="text-sm text-[var(--color-destructive)]">
          {erro}
        </p>
      )}
    </div>
  );
}
