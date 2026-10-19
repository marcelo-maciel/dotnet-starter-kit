import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { desativarEstacao, preverDesativacao } from "@/features/paf/estacoes/estacoes-api";
import { atendimentoCancelavel, avisoDoResultado, linhasDoImpacto } from "@/features/paf/estacoes/desativacao";

type Props = {
  estacao: { id: string; nome: string };
  onFechar: () => void;
  /** After the deactivation committed; `aviso` is what the screen should still tell, or null. */
  onDesativada: (aviso: string | null) => Promise<void>;
};

/**
 * Deactivation with its impact shown first (RF04, decided 03/10/2026): the senha in progress, offered for cancelling by
 * RF12, and the pairs that leave the totem. The server re-checks under its locks; a stale choice is harmless there.
 */
export function DialogoDesativacao({ estacao, onFechar, onDesativada }: Props) {
  const impacto = useQuery({ queryKey: ["paf", "estacoes", estacao.id, "desativacao"], queryFn: () => preverDesativacao(estacao.id) });
  const [ocupado, setOcupado] = useState(false);
  const [erro, setErro] = useState<string | null>(null);

  const cancelavel = impacto.data ? atendimentoCancelavel(impacto.data) : null;

  const desativar = async (cancelarAtendimentoId: string | null) => {
    setOcupado(true);
    setErro(null);
    try {
      const resultado = await desativarEstacao(estacao.id, cancelarAtendimentoId);
      await onDesativada(avisoDoResultado(resultado));
    } catch (e) {
      setErro(mensagemAmigavel(e));
      setOcupado(false);
    }
  };

  return (
    <Dialog open onOpenChange={(aberto) => !aberto && !ocupado && onFechar()}>
      <DialogContent data-dialogo-desativacao className="space-y-4 p-6">
        <DialogHeader>
          <DialogTitle>Desativar {estacao.nome}</DialogTitle>
          <DialogDescription>
            A estação deixa de chamar e de emitir, e os dispositivos ligados a ela caem. Para reativar um painel ou totem, gere um link novo.
          </DialogDescription>
        </DialogHeader>

        {impacto.isPending && <p className="text-sm text-[var(--color-muted-foreground)]">Verificando o impacto…</p>}
        {impacto.isError && <p role="alert">{mensagemAmigavel(impacto.error)}</p>}
        {impacto.data && (
          <ul className="list-disc space-y-1 pl-5 text-sm">
            {linhasDoImpacto(impacto.data).map((linha) => (
              <li key={linha}>{linha}</li>
            ))}
          </ul>
        )}
        {erro && (
          <p role="alert" className="text-sm text-[var(--color-destructive)]">
            {erro}
          </p>
        )}

        <DialogFooter className="flex flex-wrap gap-2">
          {cancelavel && (
            <Button variant="destructive" disabled={ocupado} onClick={() => void desativar(cancelavel.atendimentoId)}>
              Desativar e cancelar a senha {cancelavel.senha}
            </Button>
          )}
          <Button variant={cancelavel ? "outline" : "destructive"} disabled={ocupado || !impacto.data} onClick={() => void desativar(null)}>
            {cancelavel ? "Desativar sem cancelar" : "Desativar"}
          </Button>
          <Button variant="outline" disabled={ocupado} onClick={onFechar}>
            Voltar
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
