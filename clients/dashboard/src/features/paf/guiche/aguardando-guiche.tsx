import { useQuery } from "@tanstack/react-query";
import { apiFetch } from "@/lib/api-client";
import { textoAguardando, type AguardandoGuicheDto } from "@/features/paf/guiche/aguardando";

// Under the posto's key on purpose: every guichê action invalidates ["paf", "guiche", "posto"] and TanStack matches by
// prefix, so this guichê's own call re-reads the count with no extra wiring in the page.
const CHAVE_AGUARDANDO = ["paf", "guiche", "posto", "aguardando"] as const;
// ponytail: other guichês' calls and totem emissions reach the count by this periodic re-read. A FilaAtualizada
// handler alone would not do: a ficha only becomes callable five seconds after its notice (seção 4). Upgrade: a
// notice when the guard expires.
const RELEITURA_MS = 10_000;

/** RF06 Geral, D30: the total waiting, or the count per fila of the station when the parameter is on. */
export function AguardandoGuiche() {
  const aguardando = useQuery({
    queryKey: CHAVE_AGUARDANDO,
    queryFn: () => apiFetch<AguardandoGuicheDto>("/api/v1/paf/guiche/aguardando"),
    refetchInterval: RELEITURA_MS,
  });

  if (!aguardando.data) return null;
  const { total, porFila } = aguardando.data;
  return (
    <div className="text-center text-sm" data-aguardando-guiche>
      <p className="font-medium" data-aguardando-total={total}>
        {textoAguardando(total)}
      </p>
      {porFila && (
        <ul className="mt-1 flex flex-wrap justify-center gap-x-4 text-[var(--color-muted-foreground)]">
          {porFila.map((f) => (
            <li key={f.filaId} data-aguardando-fila={f.fila}>
              {f.fila}: {f.quantidade}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
