import { useLayoutEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { ApiRequestError } from "@/lib/api-client";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { FichaImpressa } from "@/features/paf/totem/ficha-impressa";
import { ajustarFonteSenha, definirPagina, imprimir, removerPagina } from "@/features/paf/totem/impressao";
import { emitirFichaNoGuiche, obterEmissaoGuiche } from "@/features/paf/guiche/emissao-guiche-api";
import {
  botoesDaFila,
  chaveDoClique,
  desfechoDoErro,
  formatarUltimaChamada,
  pendenteApos,
  podeEmitirNoGuiche,
  situacaoDaFila,
  type EmissaoPendente,
} from "@/features/paf/guiche/emissao-guiche-regras";

// The print dialog blocks without --kiosk-printing; past this the screen frees the buttons again.
const LIMITE_IMPRESSAO_MS = 60_000;
const CHAVE_EMISSAO = ["paf", "guiche", "emissao"] as const;

/**
 * RF11 manual emission on the guichê screen: the grid of the unidade's filas, one button per covered prioridade, the
 * guichê's last ficha and its reprint. Printed through the totem's ticket with the browser's normal dialog; no QR.
 */
export function EmissaoGuiche() {
  const { user } = useAuth();
  const permitido = podeEmitirNoGuiche(user?.permissions ?? []);
  // ponytail: no realtime here; the grid re-reads after each emission, on "Atualizar" and on window focus, so the
  // last-call column and the window edge lag until then (the server refuses a stale button anyway).
  const painel = useQuery({ queryKey: CHAVE_EMISSAO, queryFn: obterEmissaoGuiche, enabled: permitido });
  const [ocupado, setOcupado] = useState(false);
  const [erro, setErro] = useState<string | null>(null);
  const [pendente, setPendente] = useState<EmissaoPendente>(null);
  const [paraImprimir, setParaImprimir] = useState<FichaEmitidaDto | null>(null);
  const fichaRef = useRef<HTMLDivElement>(null);

  // Same measuring as the totem: the senha font and the page height depend on the rendered ticket.
  useLayoutEffect(() => {
    if (paraImprimir === null || fichaRef.current === null) return;
    const senha = fichaRef.current.querySelector<HTMLElement>("[data-senha]");
    if (senha) ajustarFonteSenha(senha);
    definirPagina(fichaRef.current);
    let ativo = true;
    const concluir = () => {
      removerPagina();
      if (!ativo) return;
      setParaImprimir(null);
      setOcupado(false);
    };
    void imprimir(LIMITE_IMPRESSAO_MS).then(concluir, (falha: unknown) => {
      console.warn("[paf-guiche] falha ao imprimir a ficha", falha);
      concluir();
    });
    return () => {
      ativo = false;
    };
  }, [paraImprimir]);

  if (!permitido) return null;

  const emitir = async (filaId: string, prioridadeId: string) => {
    const tentativa = chaveDoClique(pendente, filaId, prioridadeId, () => crypto.randomUUID());
    setPendente(tentativa);
    setOcupado(true);
    setErro(null);
    try {
      const ficha = await emitirFichaNoGuiche(filaId, prioridadeId, tentativa.chave);
      setPendente(pendenteApos(tentativa, "sucesso"));
      setParaImprimir(ficha);
      void painel.refetch();
    } catch (e) {
      // Not an ApiRequestError = fetch failure or the client's timeout: the request may have reached the server.
      const codigo = e instanceof ApiRequestError && typeof e.problem?.code === "string" ? e.problem.code : null;
      const desfecho = e instanceof ApiRequestError ? desfechoDoErro(e.status, codigo) : desfechoDoErro(null, null);
      setPendente(pendenteApos(tentativa, desfecho));
      setErro(
        desfecho === "definitiva"
          ? mensagemAmigavel(e)
          : `${mensagemAmigavel(e)} A ficha pode ter sido emitida: toque no mesmo botão para conferir sem duplicar.`,
      );
      setOcupado(false);
      void painel.refetch();
    }
  };

  // Re-reads the last ficha first, so the reprint carries the unidade's current title and footer (C10).
  const reimprimir = async () => {
    setOcupado(true);
    setErro(null);
    const lido = await painel.refetch();
    const ultima = lido.data?.ultimaFicha ?? null;
    if (lido.isError || ultima === null) {
      setErro(lido.isError ? mensagemAmigavel(lido.error) : "Nenhuma ficha emitida hoje neste guichê.");
      setOcupado(false);
      return;
    }
    setParaImprimir(ultima);
  };

  const dados = painel.data;
  const ultima = dados?.ultimaFicha ?? null;

  return (
    <section
      aria-label="Emissão de fichas"
      className="space-y-4 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5"
    >
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="font-semibold">Emissão de fichas</h2>
        <Button variant="outline" size="sm" disabled={painel.isFetching} onClick={() => void painel.refetch()}>
          {painel.isFetching ? "Atualizando…" : "Atualizar"}
        </Button>
      </header>

      {erro && <p role="alert">{erro}</p>}
      {painel.isError && <p role="alert">{mensagemAmigavel(painel.error)}</p>}

      <div className="flex flex-wrap items-center gap-3" data-ultima-ficha>
        <span>
          Última ficha emitida:{" "}
          <strong>{ultima ? `${ultima.senha} (${ultima.fila}, ${ultima.prioridade})` : "nenhuma hoje"}</strong>
        </span>
        <Button variant="secondary" size="sm" disabled={ocupado || ultima === null} onClick={() => void reimprimir()}>
          Reimprimir última
        </Button>
      </div>

      <table className="w-full text-sm">
        <thead>
          <tr className="text-left">
            <th className="py-1 pr-3">Fila</th>
            <th className="py-1 pr-3">Prioridades</th>
            <th className="py-1 pr-3">Última chamada</th>
            <th className="py-1">Ativa</th>
          </tr>
        </thead>
        <tbody>
          {(dados?.filas ?? []).map((fila) => {
            const botoes = botoesDaFila(fila);
            const situacao = situacaoDaFila(fila);
            return (
              <tr key={fila.filaId} data-fila={fila.nomeExibicao} className="border-t border-[var(--color-border)]">
                <td className="py-2 pr-3 font-medium">{fila.nomeExibicao}</td>
                <td className="py-2 pr-3">
                  <div className="flex flex-wrap gap-2">
                    {botoes.map((p) => (
                      <Button
                        key={p.prioridadeId}
                        size="sm"
                        disabled={ocupado}
                        aria-label={`Emitir ficha ${fila.nomeExibicao} ${p.nome}`}
                        onClick={() => void emitir(fila.filaId, p.prioridadeId)}
                      >
                        {p.nome} ({p.letra})
                      </Button>
                    ))}
                    {situacao !== "" && <span className="text-[var(--color-muted-foreground)]">{situacao}</span>}
                  </div>
                </td>
                <td className="py-2 pr-3">{formatarUltimaChamada(fila.ultimaChamadaEm)}</td>
                <td className="py-2">{fila.ativa ? "Sim" : "Não"}</td>
              </tr>
            );
          })}
        </tbody>
      </table>

      {paraImprimir && <FichaImpressa ref={fichaRef} ficha={paraImprimir} />}
    </section>
  );
}
