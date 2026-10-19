import { forwardRef } from "react";
import { createPortal } from "react-dom";
import type { FichaEmitidaDto } from "@/features/paf/estacao/estacao-api";
import { LARGURA_BOBINA_MM, TAMANHO_SENHA_PX } from "@/features/paf/totem/impressao";
import { ALTURA_LOGOTIPO_FICHA_MM, lerLogotipo } from "@/features/paf/logotipo/logotipo";
import { LogotipoImagem } from "@/features/paf/logotipo/logotipo-imagem";

const formatoData = new Intl.DateTimeFormat("pt-BR", {
  timeZone: "America/Sao_Paulo",
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

// The ticket is portalled next to #root so print CSS can drop the whole app (display: none) and leave
// only the ticket: hiding by visibility would keep the screen layout and print blank extra pages.
const ESTILO_IMPRESSAO = `
.paf-ficha-impressao { position: fixed; left: -10000px; top: 0; }
@media print {
  #root { display: none !important; }
  .paf-ficha-impressao { position: static; left: auto; }
  html, body { margin: 0 !important; padding: 0 !important; background: #fff !important; }
}`;

type Props = { ficha: FichaEmitidaDto };

export const FichaImpressa = forwardRef<HTMLDivElement, Props>(function FichaImpressa({ ficha }, ref) {
  return createPortal(
    <div className="paf-ficha-impressao" aria-hidden="true">
      <style>{ESTILO_IMPRESSAO}</style>
      <div
        ref={ref}
        data-ficha-impressa
        style={{
          width: `${LARGURA_BOBINA_MM}mm`,
          boxSizing: "border-box",
          padding: "3mm 4mm 6mm",
          background: "#fff",
          color: "#000",
          fontFamily: "Arial, Helvetica, sans-serif",
          textAlign: "center",
          // F20: a long unidade, fila or footer word breaks inside the 80 mm instead of running past the paper edge.
          overflowWrap: "anywhere",
        }}
      >
        <div data-logotipo-ficha style={{ display: "flex", justifyContent: "center", marginBottom: "2mm" }}>
          <LogotipoImagem url={lerLogotipo(ficha.logotipo)} altura={`${ALTURA_LOGOTIPO_FICHA_MM}mm`} larguraMaxima="60mm" />
        </div>
        {ficha.titulo !== "" && (
          <div data-titulo-ficha style={{ fontSize: 17, fontWeight: "bold", marginBottom: "2mm" }}>
            {ficha.titulo}
          </div>
        )}
        <div style={{ fontSize: 15 }}>{ficha.unidade}</div>
        <div style={{ fontSize: 18, marginTop: "3mm" }}>Ficha</div>
        <div
          data-senha
          style={{ fontSize: TAMANHO_SENHA_PX, fontWeight: "bold", lineHeight: 1.05, whiteSpace: "nowrap" }}
        >
          {ficha.senha}
        </div>
        {/* ponytail: no QR until the public ficha page exists (requisitos RF11 and 7.1: the QR stays off by
            parameter while the page does not exist). Then print the QR of identificadorPublico behind the
            RF06 "impressão do QR da ficha" parameter. */}
        <div style={{ fontSize: 18, fontWeight: "bold" }}>{ficha.fila}</div>
        <div style={{ fontSize: 15 }}>{ficha.prioridade}</div>
        <div style={{ fontSize: 13, marginTop: "1mm" }}>{formatoData.format(new Date(ficha.emitidaEm))}</div>
        {ficha.rodape !== "" && (
          <div data-rodape-ficha style={{ fontSize: 13, marginTop: "3mm", whiteSpace: "pre-wrap" }}>
            {ficha.rodape}
          </div>
        )}
      </div>
    </div>,
    document.body,
  );
});
