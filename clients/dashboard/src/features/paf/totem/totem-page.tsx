import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import QRCode from "qrcode";
import { ApiRequestError } from "@/lib/api-client";
import { lerCredencial } from "@/features/paf/estacao/credencial-estacao";
import {
  emitirFicha,
  obterConfiguracaoEmissao,
  type BotaoEmissao,
  type FichaEmitidaDto,
} from "@/features/paf/estacao/estacao-api";
import { CredencialEstacaoInvalidaError } from "@/features/paf/estacao/estacao-http";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { FichaImpressa } from "@/features/paf/totem/ficha-impressa";
import { ajustarFonteSenha, definirPagina, imprimir, removerPagina } from "@/features/paf/totem/impressao";

const LIMITE_IMPRESSAO_MS = 15_000;
const TEMPO_CONFIRMACAO_MS = 4_000;

type Pendente = { chave: string; botao: BotaoEmissao };

type Emissao =
  | { tipo: "ocioso"; aviso: string | null }
  | { tipo: "emitindo" }
  | { tipo: "imprimindo"; ficha: FichaEmitidaDto; qrSvg: string }
  | { tipo: "concluido"; senha: string };

function chaveDoBotao(b: BotaoEmissao): string {
  return `${b.filaId}:${b.prioridadeId}`;
}

// A failure the server answered for good (validation, closed window, fila not on this station) ends the
// cycle; a network error, timeout, 429 or 5xx may have reached the server, so the same key is reused
// on the next tap and the idempotency filter returns the stored ficha instead of a new number.
function falhaDefinitiva(erro: unknown): boolean {
  return erro instanceof ApiRequestError && erro.status >= 400 && erro.status < 500 && erro.status !== 408 && erro.status !== 429;
}

// ponytail: the QR points to the public ficha page, which does not exist yet (fila digital, requisitos 7.1).
function urlPublicaDaFicha(identificadorPublico: string): string {
  return `${window.location.origin}/paf/f/${identificadorPublico}`;
}

export function TotemPage() {
  const [vinculado, setVinculado] = useState(() => lerCredencial("Emissao") !== null);
  const [botoes, setBotoes] = useState<BotaoEmissao[] | null>(null);
  const [erroConfiguracao, setErroConfiguracao] = useState<string | null>(null);
  const [emissao, setEmissao] = useState<Emissao>({ tipo: "ocioso", aviso: null });
  const pendenteRef = useRef<Pendente | null>(null);
  const fichaRef = useRef<HTMLDivElement>(null);

  const tratarCredencial = useCallback((erro: unknown): boolean => {
    if (!(erro instanceof CredencialEstacaoInvalidaError)) return false;
    setVinculado(false);
    return true;
  }, []);

  useEffect(() => {
    if (!vinculado) return;
    obterConfiguracaoEmissao().then(
      (cfg) => setBotoes([...(cfg.botoes ?? [])].sort((a, b) => a.ordem - b.ordem)),
      (erro: unknown) => {
        if (!tratarCredencial(erro)) setErroConfiguracao(mensagemAmigavel(erro));
      },
    );
  }, [vinculado, tratarCredencial]);

  const emitir = async (botao: BotaoEmissao) => {
    const pendente = pendenteRef.current;
    const chave =
      pendente !== null && chaveDoBotao(pendente.botao) === chaveDoBotao(botao) ? pendente.chave : crypto.randomUUID();
    pendenteRef.current = { chave, botao };
    setEmissao({ tipo: "emitindo" });
    try {
      const ficha = await emitirFicha(botao.filaId, botao.prioridadeId, chave);
      pendenteRef.current = null;
      const qrSvg = await QRCode.toString(urlPublicaDaFicha(ficha.identificadorPublico), {
        type: "svg",
        errorCorrectionLevel: "M",
        margin: 4,
      });
      setEmissao({ tipo: "imprimindo", ficha, qrSvg });
    } catch (erro) {
      if (tratarCredencial(erro)) return;
      if (falhaDefinitiva(erro)) {
        pendenteRef.current = null;
        setEmissao({ tipo: "ocioso", aviso: mensagemAmigavel(erro) });
        return;
      }
      setEmissao({
        tipo: "ocioso",
        aviso: `${mensagemAmigavel(erro)} Toque de novo em "${botao.fila} - ${botao.prioridade}".`,
      });
    }
  };

  // Measured after layout: the senha font and the page height depend on the rendered ticket.
  useLayoutEffect(() => {
    if (emissao.tipo !== "imprimindo" || fichaRef.current === null) return;
    const senha = fichaRef.current.querySelector<HTMLElement>("[data-senha]");
    if (senha) ajustarFonteSenha(senha);
    definirPagina(fichaRef.current);
    let ativo = true;
    const senhaEmitida = emissao.ficha.senha;
    void imprimir(LIMITE_IMPRESSAO_MS).then(() => {
      removerPagina();
      if (ativo) setEmissao({ tipo: "concluido", senha: senhaEmitida });
    });
    return () => {
      ativo = false;
    };
  }, [emissao]);

  useEffect(() => {
    if (emissao.tipo !== "concluido") return;
    const timer = window.setTimeout(() => setEmissao({ tipo: "ocioso", aviso: null }), TEMPO_CONFIRMACAO_MS);
    return () => window.clearTimeout(timer);
  }, [emissao]);

  if (!vinculado) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-white p-8 text-center">
        <p role="alert" className="max-w-xl text-2xl">
          Totem não vinculado ou vínculo revogado. Abra nesta janela o link de vínculo gerado no cadastro da estação.
        </p>
      </main>
    );
  }

  const ocupado = emissao.tipo === "emitindo" || emissao.tipo === "imprimindo";

  return (
    <main className="flex min-h-screen flex-col items-center gap-8 bg-white p-8 text-black">
      <h1 className="text-4xl font-bold">Retire sua senha</h1>

      {erroConfiguracao && (
        <p role="alert" className="text-2xl text-red-700">
          {erroConfiguracao}
        </p>
      )}
      {botoes !== null && botoes.length === 0 && (
        <p role="alert" className="max-w-xl text-center text-2xl">
          Esta estação não tem filas de emissão configuradas.
        </p>
      )}

      <div className="grid w-full max-w-4xl grid-cols-1 gap-6 sm:grid-cols-2">
        {(botoes ?? []).map((b) => (
          <button
            key={chaveDoBotao(b)}
            type="button"
            disabled={ocupado}
            onClick={() => void emitir(b)}
            className="min-h-32 rounded-2xl bg-blue-700 px-6 py-8 text-3xl font-bold text-white disabled:opacity-50"
          >
            {b.fila}
            <span className="block text-2xl font-normal">{b.prioridade}</span>
          </button>
        ))}
      </div>

      <div aria-live="polite" className="min-h-16 text-center text-3xl">
        {emissao.tipo === "emitindo" && <p role="status">Emitindo sua senha…</p>}
        {emissao.tipo === "imprimindo" && <p role="status">Imprimindo a senha {emissao.ficha.senha}…</p>}
        {emissao.tipo === "concluido" && (
          <p role="status">
            Sua senha é <strong data-senha-emitida>{emissao.senha}</strong>. Retire a ficha.
          </p>
        )}
        {emissao.tipo === "ocioso" && emissao.aviso && (
          <p role="alert" className="text-red-700">
            {emissao.aviso}
          </p>
        )}
      </div>

      {emissao.tipo === "imprimindo" && <FichaImpressa ref={fichaRef} ficha={emissao.ficha} qrSvg={emissao.qrSvg} />}
    </main>
  );
}
