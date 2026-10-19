import { useState, type FormEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field } from "@/components/list";
import { Select } from "@/components/ui/select";
import { describeError } from "@/lib/api-client";
import {
  alterarContrato,
  criarContrato,
  type ContratoUnidadeDto,
  type PoliticaLimite,
  type UnidadeOperadorDto,
} from "./api-operador";
import { contratoSobreposto, fimAntesDoInicio } from "./vigencia";

type ContratoDialogProps = {
  tenantId: string;
  unidade: UnidadeOperadorDto;
  /** Null = new contract for the unidade. */
  contrato: ContratoUnidadeDto | null;
  onClose: () => void;
};

const POLITICAS: { value: PoliticaLimite; label: string }[] = [
  { value: "Recusar", label: "Recusar acima do limite" },
  { value: "UltrapassarComAviso", label: "Ultrapassar com aviso" },
];

export function ContratoDialog({ tenantId, unidade, contrato, onClose }: ContratoDialogProps) {
  const queryClient = useQueryClient();
  const [inicio, setInicio] = useState(contrato?.inicioVigencia ?? "");
  const [fim, setFim] = useState(contrato?.fimVigencia ?? "");
  const [plano, setPlano] = useState(contrato?.plano ?? "");
  const [limite, setLimite] = useState(contrato ? String(contrato.limiteSessoes) : "");
  const [politica, setPolitica] = useState<PoliticaLimite>(contrato?.politica ?? "Recusar");
  const [enviado, setEnviado] = useState(false);

  const fimOuNulo = fim === "" ? null : fim;
  const limiteNumero = Number(limite);
  const erroInicio = inicio === "" ? "Informe o início da vigência." : undefined;
  const erroFim = inicio !== "" && fimAntesDoInicio(inicio, fimOuNulo)
    ? "O fim da vigência não pode ser anterior ao início."
    : undefined;
  const sobreposto = inicio === "" || erroFim
    ? undefined
    : contratoSobreposto(unidade.contratos, { id: contrato?.id ?? "", inicioVigencia: inicio, fimVigencia: fimOuNulo });
  const erroSobreposicao = sobreposto
    ? `A vigência se sobrepõe ao contrato de ${sobreposto.inicioVigencia} a ${sobreposto.fimVigencia ?? "sem fim"}.`
    : undefined;
  const erroPlano = plano.trim() === "" ? "Informe o plano." : plano.trim().length > 120 ? "Até 120 caracteres." : undefined;
  const erroLimite = limite === "" || !Number.isInteger(limiteNumero) || limiteNumero < 0
    ? "Limite de sessões: número inteiro, zero ou mais."
    : undefined;
  const invalido = Boolean(erroInicio || erroFim || erroSobreposicao || erroPlano || erroLimite);

  const gravar = useMutation({
    mutationFn: () => {
      const dados = { inicioVigencia: inicio, fimVigencia: fimOuNulo, plano: plano.trim(), limiteSessoes: limiteNumero, politica };
      return contrato
        ? alterarContrato(tenantId, contrato.id, dados)
        : criarContrato(tenantId, unidade.id, dados);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["paf", "operador", "unidades", tenantId] });
      toast.success(contrato ? "Contrato alterado." : "Contrato gravado.");
      onClose();
    },
  });

  const enviar = (e: FormEvent) => {
    e.preventDefault();
    setEnviado(true);
    if (invalido || gravar.isPending) return;
    gravar.mutate();
  };

  return (
    <Dialog open onOpenChange={(aberto) => !aberto && onClose()}>
      <DialogContent>
        <form onSubmit={enviar} noValidate>
          <DialogHeader>
            <DialogTitle>{contrato ? "Alterar contrato" : "Novo contrato"}</DialogTitle>
            <DialogDescription>
              {unidade.nome}: plano e limite de licenças que só a Codefi grava.
            </DialogDescription>
          </DialogHeader>
          <DialogBody className="space-y-4">
            <Field id="contrato-inicio" label="Início da vigência" required error={enviado ? erroInicio : undefined}>
              <Input id="contrato-inicio" type="date" value={inicio} onChange={(e) => setInicio(e.target.value)} />
            </Field>
            <Field
              id="contrato-fim"
              label="Fim da vigência"
              hint="Vazio = sem fim."
              error={erroFim ?? erroSobreposicao}
            >
              <Input id="contrato-fim" type="date" value={fim} onChange={(e) => setFim(e.target.value)} />
            </Field>
            <Field id="contrato-plano" label="Plano" required error={enviado ? erroPlano : undefined}>
              <Input id="contrato-plano" value={plano} maxLength={120} onChange={(e) => setPlano(e.target.value)} />
            </Field>
            <Field id="contrato-limite" label="Limite de sessões simultâneas" required error={enviado ? erroLimite : undefined}>
              <Input
                id="contrato-limite"
                type="number"
                min={0}
                step={1}
                inputMode="numeric"
                value={limite}
                onChange={(e) => setLimite(e.target.value)}
              />
            </Field>
            <Select<PoliticaLimite>
              label="Acima do limite"
              value={politica}
              onChange={(v) => v !== "" && setPolitica(v)}
              options={POLITICAS}
            />
            {gravar.isError && (
              <p role="alert" className="text-sm text-[var(--color-destructive)]">
                {describeError(gravar.error)}
              </p>
            )}
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancelar
            </Button>
            <Button type="submit" disabled={gravar.isPending}>
              {gravar.isPending ? "Gravando…" : "Gravar contrato"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
