import { useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Building, Pencil, Plus } from "lucide-react";
import { listTenants } from "@/api/tenants";
import { useAuth } from "@/auth/use-auth";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Select } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { ErrorBand, PageHeader, SettingsSection } from "@/components/list";
import { describeError } from "@/lib/api-client";
import {
  listarUnidadesDoTenant,
  PermissoesOperadorPaf,
  type ContratoUnidadeDto,
  type UnidadeOperadorDto,
} from "./api-operador";
import { ContratoDialog } from "./contrato-dialog";
import { hojeEmSaoPaulo, situacaoContrato } from "./vigencia";

type Edicao = { unidade: UnidadeOperadorDto; contrato: ContratoUnidadeDto | null };

// ponytail: one page of 100 tenants feeds the picker; add search when the client base passes that.
const TENANTS_NO_SELETOR = 100;

/**
 * Codefi operator side of RF23: one client tenant at a time, chosen here and kept in `?tenant=`, so a link opens
 * the same client. Every call of the page sends that tenant explicitly.
 */
export function UnidadesOperadorPage() {
  const [parametros, setParametros] = useSearchParams();
  const tenantId = parametros.get("tenant") ?? "";
  const [edicao, setEdicao] = useState<Edicao | null>(null);
  const { user } = useAuth();
  const gravaContrato = (user?.permissions ?? []).includes(PermissoesOperadorPaf.ContratosManage);
  const hoje = hojeEmSaoPaulo(new Date());

  const tenants = useQuery({
    queryKey: ["tenants", "list", { pageNumber: 1, pageSize: TENANTS_NO_SELETOR }],
    queryFn: () => listTenants({ pageNumber: 1, pageSize: TENANTS_NO_SELETOR }),
  });
  const opcoes = useMemo(
    () => (tenants.data?.items ?? []).map((t) => ({ value: t.id, label: `${t.name} (${t.id})` })),
    [tenants.data],
  );

  const unidades = useQuery({
    queryKey: ["paf", "operador", "unidades", tenantId],
    queryFn: () => listarUnidadesDoTenant(tenantId),
    enabled: tenantId !== "",
  });

  const escolherTenant = (id: string) => {
    setEdicao(null);
    setParametros(id === "" ? {} : { tenant: id });
  };

  return (
    <div className="space-y-6">
      <PageHeader
        crumbs={[{ label: "PAF" }, { label: "Unidades e contratos", muted: true }]}
        title="Unidades e contratos"
        description="Plano e limite de licenças de cada unidade do cliente. O cliente vê o limite, mas não o altera."
      />

      <Select
        label="Cliente"
        placeholder={tenants.isLoading ? "Carregando…" : "Escolha o cliente"}
        value={tenantId}
        onChange={escolherTenant}
        options={opcoes}
        minWidth="20rem"
      />
      {tenants.isError && <ErrorBand message={describeError(tenants.error)} />}

      {tenantId !== "" && unidades.isError && <ErrorBand message={describeError(unidades.error)} />}
      {tenantId !== "" && unidades.isLoading && <Skeleton className="h-32 w-full" />}
      {tenantId !== "" && unidades.data?.length === 0 && (
        <p className="text-sm text-[var(--color-muted-foreground)]">Este cliente ainda não tem unidade.</p>
      )}

      {unidades.data?.map((unidade) => (
        <SettingsSection
          key={unidade.id}
          icon={Building}
          title={unidade.nome}
          description={
            <span className="flex flex-wrap items-center gap-2">
              {unidade.inicial && <Badge variant="outline">Inicial</Badge>}
              {unidade.ativa ? <Badge variant="success">Ativa</Badge> : <Badge variant="muted">Inativa</Badge>}
              {unidade.documentoFiscal && <span>{unidade.documentoFiscal}</span>}
            </span>
          }
          footer={
            gravaContrato ? (
              <Button onClick={() => setEdicao({ unidade, contrato: null })} aria-label={`Novo contrato de ${unidade.nome}`}>
                <Plus className="mr-1 h-4 w-4" /> Novo contrato
              </Button>
            ) : undefined
          }
        >
          {unidade.contratos.length === 0 ? (
            <p className="text-sm text-[var(--color-muted-foreground)]">
              Sem contrato: ninguém desta unidade entra até a Codefi gravar um.
            </p>
          ) : (
            <table className="w-full text-sm">
              <caption className="sr-only">Contratos de {unidade.nome}</caption>
              <thead>
                <tr className="text-left text-[var(--color-muted-foreground)]">
                  <th scope="col" className="py-2 font-medium">Vigência</th>
                  <th scope="col" className="py-2 font-medium">Plano</th>
                  <th scope="col" className="py-2 font-medium">Limite</th>
                  <th scope="col" className="py-2 font-medium">Acima do limite</th>
                  <th scope="col" className="py-2 font-medium">Situação</th>
                  {gravaContrato && <th scope="col" className="py-2"><span className="sr-only">Ações</span></th>}
                </tr>
              </thead>
              <tbody>
                {unidade.contratos.map((c) => {
                  const situacao = situacaoContrato(c, hoje);
                  return (
                    <tr key={c.id} className="border-t border-[var(--color-border)]">
                      <td className="py-2 tabular-nums">{c.inicioVigencia} a {c.fimVigencia ?? "sem fim"}</td>
                      <td className="py-2">{c.plano}</td>
                      <td className="py-2 tabular-nums">{c.limiteSessoes}</td>
                      <td className="py-2">{c.politica === "Recusar" ? "Recusar" : "Ultrapassar com aviso"}</td>
                      <td className="py-2">
                        <Badge variant={situacao === "Vigente" ? "success" : "muted"}>{situacao}</Badge>
                      </td>
                      {gravaContrato && (
                        <td className="py-2 text-right">
                          <Button
                            variant="ghost"
                            size="icon"
                            aria-label={`Alterar contrato de ${c.inicioVigencia}`}
                            onClick={() => setEdicao({ unidade, contrato: c })}
                          >
                            <Pencil className="h-4 w-4" />
                          </Button>
                        </td>
                      )}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </SettingsSection>
      ))}

      {edicao && (
        <ContratoDialog
          tenantId={tenantId}
          unidade={edicao.unidade}
          contrato={edicao.contrato}
          onClose={() => setEdicao(null)}
        />
      )}
    </div>
  );
}
