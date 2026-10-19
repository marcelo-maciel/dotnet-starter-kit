import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  encaminharAtendimento,
  listarDestinosEncaminhamento,
  type DestinosEncaminhamentoDto,
} from "@/features/paf/guiche/encaminhamento-api";
import {
  alternarFila,
  bloqueioConfirmacao,
  intersecaoPrioridades,
  prioridadeSelecionada,
} from "@/features/paf/guiche/encaminhamento";

const SELECT_CLASSES =
  "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

type Props = {
  /** Estado of the atendimento on screen; only EmAtendimento can be forwarded (RF17). */
  estado: string | null;
  desabilitado: boolean;
  /** Called with the senha after the server forwarded it; the page re-reads its post. */
  aoEncaminhar: (senha: string) => void;
};

/** RF17 button and screen. The keyboard shortcut is C10's. */
export function EncaminharAtendimento({ estado, desabilitado, aoEncaminhar }: Props) {
  const [aberto, setAberto] = useState(false);
  return (
    <Dialog open={aberto} onOpenChange={setAberto}>
      <Button
        size="lg"
        variant="secondary"
        disabled={desabilitado || estado !== "EmAtendimento"}
        onClick={() => setAberto(true)}
      >
        Encaminhar
      </Button>
      <DialogContent>
        {/* Mounted only while open: every opening reads the destinations again and starts with nothing marked. */}
        {aberto && (
          <TelaEncaminhamento
            aoEncaminhar={(senha) => {
              setAberto(false);
              aoEncaminhar(senha);
            }}
            aoCancelar={() => setAberto(false)}
          />
        )}
      </DialogContent>
    </Dialog>
  );
}

function TelaEncaminhamento({
  aoEncaminhar,
  aoCancelar,
}: {
  aoEncaminhar: (senha: string) => void;
  aoCancelar: () => void;
}) {
  const destinos = useQuery({
    queryKey: ["paf", "guiche", "encaminhamento", "destinos"],
    queryFn: listarDestinosEncaminhamento,
    // Coverage and windows change under the operator: never answer an opening from an older reading.
    gcTime: 0,
    staleTime: 0,
  });

  return (
    <>
      <DialogHeader>
        <DialogTitle>Encaminhar atendimento</DialogTitle>
        <DialogDescription>
          {destinos.data
            ? `Senha ${destinos.data.senha}, fila de origem ${destinos.data.filaOrigem}.`
            : "Escolha as filas de destino e a prioridade."}
        </DialogDescription>
      </DialogHeader>
      {destinos.isPending && <DialogBody>Carregando…</DialogBody>}
      {destinos.isError && (
        <DialogBody>
          <p role="alert" className="text-[var(--color-destructive)]">
            {mensagemAmigavel(destinos.error)}
          </p>
        </DialogBody>
      )}
      {destinos.data && (
        <FormularioEncaminhamento
          dados={destinos.data}
          aoEncaminhar={aoEncaminhar}
          aoCancelar={aoCancelar}
          aoRecusar={() => void destinos.refetch()}
        />
      )}
    </>
  );
}

function FormularioEncaminhamento({
  dados,
  aoEncaminhar,
  aoCancelar,
  aoRecusar,
}: {
  dados: DestinosEncaminhamentoDto;
  aoEncaminhar: (senha: string) => void;
  aoCancelar: () => void;
  aoRecusar: () => void;
}) {
  const [marcadas, setMarcadas] = useState<string[]>([]);
  // null = untouched: the original prioridade is pre-selected while it is in the intersection (RF17 item 3).
  const [escolhida, setEscolhida] = useState<string | null>(null);
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | null>(null);

  // A fila that left the list on a re-read (deactivated meanwhile) is not sent.
  const marcadasValidas = marcadas.filter((id) => dados.destinos.some((d) => d.filaId === id));
  const intersecao = intersecaoPrioridades(dados.destinos, marcadasValidas);
  const prioridadeId = prioridadeSelecionada(intersecao, escolhida, dados.prioridadeOriginalId);
  const bloqueio = bloqueioConfirmacao(marcadasValidas, intersecao, prioridadeId);

  const confirmar = async () => {
    if (bloqueio !== null) return;
    setEnviando(true);
    setErro(null);
    try {
      await encaminharAtendimento(dados.atendimentoId, marcadasValidas, prioridadeId);
      aoEncaminhar(dados.senha);
    } catch (e) {
      // The screen stays open with the server's reason (409 from a lost coverage, a fila deactivated meanwhile)
      // and re-reads the destinations, so the next choice is made on the current coverage.
      setErro(mensagemAmigavel(e));
      aoRecusar();
    } finally {
      setEnviando(false);
    }
  };

  return (
    <>
      <DialogBody className="space-y-4">
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium">Filas de destino</legend>
          {dados.destinos.length === 0 && <p>Nenhuma outra fila ativa nesta unidade.</p>}
          {dados.destinos.map((d) => (
            <label key={d.filaId} className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={marcadas.includes(d.filaId)}
                onChange={() => setMarcadas((atual) => alternarFila(atual, d.filaId))}
              />
              <span>{d.nome}</span>
              {d.foraDoHorario && <Badge variant="warning">fora do horário de atendimento</Badge>}
            </label>
          ))}
        </fieldset>
        <label className="block space-y-1 text-sm">
          <span className="font-medium">Prioridade</span>
          <select
            className={SELECT_CLASSES}
            value={prioridadeId}
            disabled={intersecao.length === 0}
            onChange={(e) => setEscolhida(e.target.value)}
          >
            <option value="">Selecione a prioridade</option>
            {intersecao.map((p) => (
              <option key={p.prioridadeId} value={p.prioridadeId}>
                {p.nome}
              </option>
            ))}
          </select>
        </label>
        {bloqueio && (
          <p role="status" className="text-sm text-[var(--color-muted-foreground)]" data-bloqueio-encaminhar>
            {bloqueio}
          </p>
        )}
        {erro && (
          <p role="alert" className="text-sm text-[var(--color-destructive)]" data-erro-encaminhar>
            {erro}
          </p>
        )}
      </DialogBody>
      <DialogFooter>
        <Button variant="outline" onClick={aoCancelar}>
          Cancelar
        </Button>
        <Button disabled={enviando || bloqueio !== null} onClick={() => void confirmar()}>
          Encaminhar
        </Button>
      </DialogFooter>
    </>
  );
}
