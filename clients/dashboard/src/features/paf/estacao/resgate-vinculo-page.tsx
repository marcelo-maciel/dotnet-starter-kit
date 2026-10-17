import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { ApiRequestError } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { resgatarVinculoEstacao } from "@/features/paf/estacao/resgate-vinculo";
import type { PapelEstacao } from "@/features/paf/estacao/credencial-estacao";

type Estado =
  | { tipo: "resgatando" }
  | { tipo: "pronto"; papeis: PapelEstacao[] }
  | { tipo: "erro"; mensagem: string };

const ROTA_POR_PAPEL: Record<PapelEstacao, { caminho: string; rotulo: string }> = {
  Emissao: { caminho: "/paf/totem", rotulo: "Abrir o totem" },
  Painel: { caminho: "/paf/painel", rotulo: "Abrir o painel" },
};

// Read on the first render, before the effect rewrites the URL: the token lives in the fragment so it
// never reaches a server or proxy access log, and it is wiped from the address bar right after.
function lerLink(): { tenant: string; token: string } | null {
  const params = new URLSearchParams(window.location.hash.replace(/^#/, ""));
  const tenant = params.get("tenant")?.trim() ?? "";
  const token = params.get("token")?.trim() ?? "";
  return tenant && token ? { tenant, token } : null;
}

function mensagemDoResgate(erro: unknown): string {
  if (erro instanceof ApiRequestError && (erro.status === 400 || erro.status === 401 || erro.status === 404)) {
    return "Link inválido, vencido ou já usado. Gere um novo link no cadastro da estação.";
  }
  return mensagemAmigavel(erro);
}

export function ResgateVinculoPage() {
  const [link] = useState(lerLink);
  const [estado, setEstado] = useState<Estado>(() =>
    link === null
      ? { tipo: "erro", mensagem: "Link incompleto: faltam o cliente ou o código da estação." }
      : { tipo: "resgatando" },
  );

  useEffect(() => {
    if (link === null) return;
    window.history.replaceState(null, "", window.location.pathname);
    let ativo = true;
    resgatarVinculoEstacao(link.tenant, link.token).then(
      (papeis) => {
        if (!ativo) return;
        setEstado(
          papeis.length === 0
            ? { tipo: "erro", mensagem: "Esta estação não tem papel de totem nem de painel." }
            : { tipo: "pronto", papeis },
        );
      },
      (erro: unknown) => {
        if (ativo) setEstado({ tipo: "erro", mensagem: mensagemDoResgate(erro) });
      },
    );
    return () => {
      ativo = false;
    };
  }, [link]);

  return (
    <main className="flex min-h-screen items-center justify-center bg-[var(--color-background)] p-6">
      <section className="w-full max-w-md space-y-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-6 text-center shadow-xs">
        <h1 className="text-xl font-semibold">Vincular estação</h1>
        {estado.tipo === "resgatando" && (
          <p role="status" aria-live="polite">
            Vinculando esta estação…
          </p>
        )}
        {estado.tipo === "erro" && (
          <p role="alert" className="text-[var(--color-destructive)]">
            {estado.mensagem}
          </p>
        )}
        {estado.tipo === "pronto" && (
          <>
            <p role="status">Estação vinculada. Escolha a tela desta janela.</p>
            <ul className="space-y-2">
              {estado.papeis.map((papel) => (
                <li key={papel}>
                  <Link
                    className="inline-block w-full rounded-md bg-[var(--color-primary)] px-4 py-3 font-medium text-[var(--color-primary-foreground)]"
                    to={ROTA_POR_PAPEL[papel].caminho}
                    replace
                  >
                    {ROTA_POR_PAPEL[papel].rotulo}
                  </Link>
                </li>
              ))}
            </ul>
          </>
        )}
      </section>
    </main>
  );
}
