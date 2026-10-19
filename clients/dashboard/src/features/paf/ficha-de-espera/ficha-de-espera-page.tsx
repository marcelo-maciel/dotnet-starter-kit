import { useId, useLayoutEffect, useRef, useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { FichaImpressa } from "@/features/paf/totem/ficha-impressa";
import { ajustarFonteSenha, definirPagina, imprimir, removerPagina } from "@/features/paf/totem/impressao";
import { listarFichasDoDia } from "@/features/paf/fichas-do-dia/fichas-do-dia-api";
import { buscarFichaDeEspera } from "@/features/paf/ficha-de-espera/ficha-de-espera-api";
import { pedidoCompleto, type PedidoReimpressao } from "@/features/paf/ficha-de-espera/ficha-de-espera-regras";
import { hojeEmSaoPaulo, normalizarSenha, SENHA_MAX } from "@/features/paf/consultar-ficha/consultar-ficha-regras";

// The print dialog blocks without --kiosk-printing; past this the screen frees the print buttons again.
const LIMITE_IMPRESSAO_MS = 60_000;

const formatoEmissao = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

/** RF20 "ficha de espera": find the stored ficha by senha, day and fila and print it again, never a new number. */
export function FichaDeEsperaPage() {
  const [senha, setSenha] = useState("");
  const [data, setData] = useState(() => hojeEmSaoPaulo(new Date()));
  const [filaId, setFilaId] = useState("");
  const [pedido, setPedido] = useState<PedidoReimpressao | null>(null);
  const [paraImprimir, setParaImprimir] = useState<FichaEmitidaDto | null>(null);
  const fichaRef = useRef<HTMLDivElement>(null);
  const idSenha = useId();
  const idData = useId();
  const idFila = useId();

  // ponytail: the filas come from the F06 list (same FichasDoDia.View; GET filas needs Filas.View), which also
  // brings today's lines. Swap for a filas-only read if that payload ever weighs on this screen.
  const filas = useQuery({
    queryKey: ["paf", "ficha-de-espera", "filas"],
    queryFn: () => listarFichasDoDia({ data: null, filaId: null, estado: null }),
    select: (dados) => dados.filas,
  });

  const resultado = useQuery({
    queryKey: ["paf", "fichas", "reimpressao", pedido],
    queryFn: () => buscarFichaDeEspera(pedido!),
    enabled: pedido !== null,
    // A 404 is an answer ("no such ficha"), not a transient failure worth repeating.
    retry: false,
  });

  const atual: PedidoReimpressao = { senha, data, filaId };
  const podeBuscar = pedidoCompleto(atual);

  const buscar = (e: FormEvent) => {
    e.preventDefault();
    if (!podeBuscar) return;
    const novo = { ...atual, senha: normalizarSenha(senha) };
    setSenha(novo.senha);
    if (pedido?.senha === novo.senha && pedido.data === novo.data && pedido.filaId === novo.filaId) {
      void resultado.refetch();
      return;
    }
    setPedido(novo);
  };

  // Same measuring as the totem and the fichas do dia: the senha font and the page height depend on the ticket.
  useLayoutEffect(() => {
    if (paraImprimir === null || fichaRef.current === null) return;
    const elementoSenha = fichaRef.current.querySelector<HTMLElement>("[data-senha]");
    if (elementoSenha) ajustarFonteSenha(elementoSenha);
    definirPagina(fichaRef.current);
    let ativo = true;
    const concluir = () => {
      removerPagina();
      if (ativo) setParaImprimir(null);
    };
    void imprimir(LIMITE_IMPRESSAO_MS).then(concluir, (falha: unknown) => {
      console.warn("[paf-ficha-de-espera] falha ao imprimir a ficha", falha);
      concluir();
    });
    return () => {
      ativo = false;
    };
  }, [paraImprimir]);

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Ficha de espera</h1>
        <p className="text-sm text-[var(--color-muted-foreground)]">Reimprime a ficha já emitida, com a mesma senha.</p>
      </header>

      <form
        onSubmit={buscar}
        className="flex flex-wrap items-end gap-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5"
        data-ficha-de-espera
      >
        <div className="space-y-1">
          <Label htmlFor={idSenha}>Senha impressa</Label>
          <Input
            id={idSenha}
            value={senha}
            maxLength={SENHA_MAX + 10}
            autoComplete="off"
            placeholder="A12"
            onChange={(e) => setSenha(e.target.value)}
          />
        </div>
        <div className="space-y-1">
          <Label htmlFor={idData}>Data</Label>
          <Input id={idData} type="date" value={data} onChange={(e) => setData(e.target.value)} />
        </div>
        <div className="space-y-1">
          <Label htmlFor={idFila}>Fila</Label>
          <select
            id={idFila}
            value={filaId}
            onChange={(e) => setFilaId(e.target.value)}
            className="h-9 rounded-md border border-[var(--color-border)] bg-[var(--color-background)] px-2 text-sm"
          >
            <option value="">Selecione a fila</option>
            {(filas.data ?? []).map((f) => (
              <option key={f.id} value={f.id}>
                {f.nomeExibicao}
                {f.ativa ? "" : " (inativa)"}
              </option>
            ))}
          </select>
        </div>
        <Button type="submit" disabled={!podeBuscar || resultado.isFetching}>
          Buscar
        </Button>
      </form>

      {filas.isError && <p role="alert">{mensagemAmigavel(filas.error)}</p>}
      {pedido !== null && resultado.isPending && <p className="text-[var(--color-muted-foreground)]">Buscando…</p>}
      {resultado.isError && <p role="alert">{mensagemAmigavel(resultado.error)}</p>}

      {(resultado.data ?? []).map((ficha) => (
        <section
          key={ficha.fichaId}
          className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5"
          data-ficha={ficha.fichaId}
        >
          <div className="space-y-1">
            <h2 className="text-xl font-semibold">{ficha.senha}</h2>
            <p className="text-sm text-[var(--color-muted-foreground)]">
              Fila {ficha.fila}, prioridade {ficha.prioridade}, emitida em {formatoEmissao.format(new Date(ficha.emitidaEm))}
            </p>
          </div>
          <Button disabled={paraImprimir !== null} onClick={() => setParaImprimir(ficha)}>
            Imprimir
          </Button>
        </section>
      ))}

      {paraImprimir && <FichaImpressa ref={fichaRef} ficha={paraImprimir} />}
    </div>
  );
}
