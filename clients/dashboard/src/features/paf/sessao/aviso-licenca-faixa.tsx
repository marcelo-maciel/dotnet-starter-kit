import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { apiFetch } from "@/lib/api-client";
import {
  avisoAMostrar,
  CHAVE_AVISO_LICENCA_FECHADO,
  textoAvisoLicenca,
  type AvisoLicencaDto,
} from "@/features/paf/sessao/aviso-licenca";

// Under the "paf" key, so the unit switch's invalidation re-reads it with the rest of the PAF screens.
const CHAVE_CONSULTA = ["paf", "sessao", "aviso-licenca"] as const;

async function obterAvisoLicenca(): Promise<AvisoLicencaDto | null> {
  // 204 when the session entered within the limit; the query cache does not take undefined.
  return (await apiFetch<AvisoLicencaDto | undefined>("/api/v1/paf/sessao/aviso-licenca")) ?? null;
}

export function AvisoLicencaFaixa() {
  const [fechado, setFechado] = useState(() => sessionStorage.getItem(CHAVE_AVISO_LICENCA_FECHADO));
  const consulta = useQuery({ queryKey: CHAVE_CONSULTA, queryFn: obterAvisoLicenca });
  const aviso = avisoAMostrar(consulta.data, fechado);
  if (!aviso) return null;

  const fechar = () => {
    sessionStorage.setItem(CHAVE_AVISO_LICENCA_FECHADO, aviso.registroId);
    setFechado(aviso.registroId);
  };

  return (
    <div
      role="status"
      data-paf-aviso-licenca
      className="flex items-center justify-between gap-3 rounded-md border border-[var(--color-border)] bg-[var(--color-card)] px-4 py-2 text-sm"
    >
      <span>{textoAvisoLicenca(aviso)}</span>
      <Button variant="link" size="xs" onClick={fechar}>
        Fechar
      </Button>
    </div>
  );
}
