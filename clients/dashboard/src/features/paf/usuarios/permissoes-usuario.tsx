import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { apiFetch } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  alterado,
  alternar,
  estadoDaPermissao,
  individuaisParaSalvar,
  type PermissoesUsuarioPafDto,
} from "@/features/paf/usuarios/permissoes-regras";

const rota = (usuarioId: string) => `/api/v1/paf/usuarios/${usuarioId}/permissoes`;

/**
 * RF08 per-user permissions (C15) in the four groups. The user's role is the starting point (checked and locked); the
 * panel adds individual permissions on top, and only the ones the caller holds can change.
 */
export function PermissoesUsuario(props: { usuarioId: string; onFechar: () => void }) {
  const queryClient = useQueryClient();
  const chave = ["paf", "usuarios", props.usuarioId, "permissoes"] as const;
  const consulta = useQuery({ queryKey: chave, queryFn: () => apiFetch<PermissoesUsuarioPafDto>(rota(props.usuarioId)) });
  const [marcadas, setMarcadas] = useState<Set<string> | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);

  if (consulta.isPending) return <p className="text-sm text-[var(--color-muted-foreground)]">Carregando permissões…</p>;
  if (consulta.isError) return <p role="alert">{mensagemAmigavel(consulta.error)}</p>;

  const dto = consulta.data;
  const atuais = marcadas ?? new Set(dto.individuais);

  const salvar = async () => {
    setOcupado(true);
    setErro(null);
    try {
      await apiFetch<string>(rota(props.usuarioId), { method: "PUT", body: JSON.stringify({ permissoes: individuaisParaSalvar(atuais) }) });
      await queryClient.invalidateQueries({ queryKey: chave });
      setMarcadas(null);
      props.onFechar();
    } catch (e) {
      setErro(mensagemAmigavel(e));
    } finally {
      setOcupado(false);
    }
  };

  return (
    <div className="space-y-3 border-t border-[var(--color-border)] pt-3" data-painel="permissoes">
      <p className="text-sm text-[var(--color-muted-foreground)]">
        As permissões do perfil vêm marcadas e não saem por aqui; marque as que este usuário tem a mais.
      </p>
      {dto.grupos.map((grupo) => (
        <fieldset key={grupo.nome} className="space-y-1">
          <legend className="font-medium">{grupo.nome}</legend>
          {grupo.observacao && <p className="text-xs text-[var(--color-muted-foreground)]">{grupo.observacao}</p>}
          {grupo.permissoes.map((p) => {
            const estado = estadoDaPermissao(p.nome, dto, atuais);
            return (
              <label key={p.nome} className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  checked={estado.marcada}
                  disabled={estado.travada || ocupado}
                  onChange={() => setMarcadas(alternar(atuais, p.nome))}
                />
                {p.rotulo}
                {estado.motivo && <span className="text-xs text-[var(--color-muted-foreground)]">({estado.motivo})</span>}
              </label>
            );
          })}
        </fieldset>
      ))}
      {erro && <p role="alert">{erro}</p>}
      <div className="flex gap-2">
        <Button size="sm" disabled={ocupado || !alterado(dto, atuais)} onClick={() => void salvar()}>
          Salvar permissões
        </Button>
        <Button size="sm" variant="outline" disabled={ocupado} onClick={props.onFechar}>
          Fechar
        </Button>
      </div>
    </div>
  );
}
