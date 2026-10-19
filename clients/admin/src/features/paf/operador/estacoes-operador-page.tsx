import { useMemo } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { MonitorSmartphone, RefreshCw } from "lucide-react";
import { listTenants } from "@/api/tenants";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { ErrorBand, PageHeader, SettingsSection } from "@/components/list";
import { describeError } from "@/lib/api-client";
import { listarEstacoesDoTenant } from "./api-operador";
import { agruparPorUnidade, instanteEmSaoPaulo, rotuloPapel, situacaoPapel, type SituacaoPapel } from "./monitoramento";

// ponytail: one page of 100 tenants feeds the picker, as on the units page; add search when the client base passes that.
const TENANTS_NO_SELETOR = 100;
// ponytail: polling while the page is open; a pushed notice to the operator needs a root-side hub group.
const ATUALIZACAO_MS = 30_000;

const VARIANTE: Record<SituacaoPapel, "success" | "danger" | "muted" | "warning"> = {
  Conectada: "success",
  "Sem conexão": "danger",
  "Nunca conectou": "warning",
  Desativada: "muted",
};

/**
 * Support screen of RNF20: every totem and painel role of one client tenant, chosen here and kept in `?tenant=`.
 * A combined station shows one line per role, so a dropped painel is not hidden by a connected totem.
 */
export function EstacoesOperadorPage() {
  const [parametros, setParametros] = useSearchParams();
  const tenantId = parametros.get("tenant") ?? "";

  const tenants = useQuery({
    queryKey: ["tenants", "list", { pageNumber: 1, pageSize: TENANTS_NO_SELETOR }],
    queryFn: () => listTenants({ pageNumber: 1, pageSize: TENANTS_NO_SELETOR }),
  });
  const opcoes = useMemo(
    () => (tenants.data?.items ?? []).map((t) => ({ value: t.id, label: `${t.name} (${t.id})` })),
    [tenants.data],
  );

  const monitoramento = useQuery({
    queryKey: ["paf", "operador", "estacoes", tenantId],
    queryFn: () => listarEstacoesDoTenant(tenantId),
    enabled: tenantId !== "",
    refetchInterval: ATUALIZACAO_MS,
  });
  const grupos = useMemo(() => agruparPorUnidade(monitoramento.data?.papeis ?? []), [monitoramento.data]);
  const avisos = grupos.reduce((total, g) => total + g.avisos, 0);

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: "PAF" }, { label: "Estações", muted: true }]}
        title="Estações dos clientes"
        description="Última conexão e última queda de cada totem e painel. Aviso: estação ativa sem conexão no horário de alguma fila ativa da unidade."
      />

      <div className="flex flex-wrap items-end gap-3">
        <Select
          label="Cliente"
          placeholder={tenants.isLoading ? "Carregando…" : "Escolha o cliente"}
          value={tenantId}
          onChange={(id) => setParametros(id === "" ? {} : { tenant: id })}
          options={opcoes}
          minWidth="20rem"
        />
        {tenantId !== "" && (
          <Button variant="outline" onClick={() => void monitoramento.refetch()} disabled={monitoramento.isFetching}>
            <RefreshCw className="mr-1 h-4 w-4" aria-hidden="true" /> Atualizar
          </Button>
        )}
      </div>
      {tenants.isError && <ErrorBand message={describeError(tenants.error)} />}

      {tenantId !== "" && monitoramento.isError && <ErrorBand message={describeError(monitoramento.error)} />}
      {tenantId !== "" && monitoramento.isLoading && <Skeleton className="h-32 w-full" />}
      {monitoramento.data && (
        <p className="text-sm text-[var(--color-muted-foreground)]" role="status">
          {avisos === 0 ? "Nenhum aviso." : `${avisos} ${avisos === 1 ? "aviso" : "avisos"}.`} Consultado às{" "}
          {monitoramento.data.agoraEmSaoPaulo.slice(11, 19)} (horário de Brasília).
          {!monitoramento.data.estadoPeloRegistro &&
            " Registro de conexões indisponível: o estado vem só do banco e não mostra uma instância que caiu sem desconectar."}
        </p>
      )}
      {monitoramento.data?.papeis.length === 0 && (
        <p className="text-sm text-[var(--color-muted-foreground)]">Este cliente não tem totem nem painel cadastrado.</p>
      )}

      {grupos.map((grupo) => (
        <SettingsSection
          key={grupo.unidadeId}
          icon={MonitorSmartphone}
          title={grupo.unidadeNome}
          description={grupo.avisos > 0 ? <Badge variant="danger">{grupo.avisos} com aviso</Badge> : undefined}
        >
          <table className="w-full text-sm">
            <caption className="sr-only">Estações de {grupo.unidadeNome}</caption>
            <thead>
              <tr className="text-left text-[var(--color-muted-foreground)]">
                <th scope="col" className="py-2 font-medium">Estação</th>
                <th scope="col" className="py-2 font-medium">Papel</th>
                <th scope="col" className="py-2 font-medium">Situação</th>
                <th scope="col" className="py-2 font-medium">Última conexão</th>
                <th scope="col" className="py-2 font-medium">Última queda</th>
              </tr>
            </thead>
            <tbody>
              {grupo.papeis.map((p) => {
                const situacao = situacaoPapel(p);
                return (
                  <tr key={`${p.estacaoId}-${p.papel}`} className="border-t border-[var(--color-border)]">
                    <td className="py-2">{p.estacaoNome}</td>
                    <td className="py-2">{rotuloPapel(p.papel)}</td>
                    <td className="py-2">
                      <span className="flex flex-wrap items-center gap-2">
                        <Badge variant={VARIANTE[situacao]}>{situacao}</Badge>
                        {p.aviso && <Badge variant="danger">Aviso: sem conexão em horário de operação</Badge>}
                      </span>
                    </td>
                    <td className="py-2 tabular-nums">{instanteEmSaoPaulo(p.ultimaConexaoEm)}</td>
                    <td className="py-2 tabular-nums">{instanteEmSaoPaulo(p.ultimaQuedaEm)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </SettingsSection>
      ))}
    </div>
  );
}
