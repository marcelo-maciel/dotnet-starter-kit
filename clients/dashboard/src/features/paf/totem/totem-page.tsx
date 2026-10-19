import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { lerCredencial } from "@/features/paf/estacao/credencial-estacao";
import {
  lerNumeroDaConfiguracao,
  obterConfiguracaoEmissao,
  type BotaoEmissao,
  type FichaEmitidaDto,
} from "@/features/paf/estacao/estacao-api";
import { CredencialEstacaoInvalidaError } from "@/features/paf/estacao/estacao-http";
import { conectarHubEstacao } from "@/features/paf/estacao/hub-estacao";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { EmissaoTotem, PRAZO_CICLO_PADRAO_S } from "@/features/paf/totem/ciclo-emissao";
import { emitirFichaNoCiclo } from "@/features/paf/totem/emitir-ficha";
import { FichaImpressa } from "@/features/paf/totem/ficha-impressa";
import { ajustarFonteSenha, definirPagina, imprimir, removerPagina } from "@/features/paf/totem/impressao";

const LIMITE_IMPRESSAO_MS = 15_000;
const TEMPO_CONFIRMACAO_MS = 4_000;
// ponytail: a fila window opening or closing pushes no notice, so the buttons follow it by this periodic
// re-read (plus FilaAtualizada); the emission refuses outside the window meanwhile. Upgrade: a scheduled notice.
const RELEITURA_BOTOES_MS = 60_000;
const CHAVE_PRAZO_CICLO = "prazoCicloEmissaoSegundos";
const MENSAGEM_PRAZO_VENCIDO = "Não foi possível emitir a senha agora. Toque de novo ou procure o atendimento.";

type Emissao =
  | { tipo: "ocioso"; aviso: string | null }
  | { tipo: "emitindo" }
  | { tipo: "imprimindo"; ficha: FichaEmitidaDto }
  | { tipo: "concluido"; senha: string };

function chaveDoBotao(b: BotaoEmissao): string {
  return `${b.filaId}:${b.prioridadeId}`;
}

export function TotemPage() {
  const [vinculado, setVinculado] = useState(() => lerCredencial("Emissao") !== null);
  const [botoes, setBotoes] = useState<BotaoEmissao[] | null>(null);
  const [erroConfiguracao, setErroConfiguracao] = useState<string | null>(null);
  const [emissao, setEmissao] = useState<Emissao>({ tipo: "ocioso", aviso: null });
  const cicloRef = useRef<EmissaoTotem<BotaoEmissao, FichaEmitidaDto> | null>(null);
  const prazoCicloMsRef = useRef(PRAZO_CICLO_PADRAO_S * 1_000);
  // Resolves the cycle's delivery once the screen is back to the initial state (RF11: that ends the cycle).
  const fimEntregaRef = useRef<(() => void) | null>(null);
  const fichaRef = useRef<HTMLDivElement>(null);

  const tratarCredencial = useCallback((erro: unknown): boolean => {
    if (!(erro instanceof CredencialEstacaoInvalidaError)) return false;
    setVinculado(false);
    return true;
  }, []);

  // The server drops buttons outside the fila window or without a guichê covering them (RF02), so the
  // list is re-read whenever it may have changed instead of being loaded once.
  const recarregarBotoes = useCallback(() => {
    obterConfiguracaoEmissao().then(
      (cfg) => {
        setErroConfiguracao(null);
        setBotoes([...cfg.botoes].sort((a, b) => a.ordem - b.ordem));
        prazoCicloMsRef.current =
          lerNumeroDaConfiguracao(cfg.configuracao, CHAVE_PRAZO_CICLO, PRAZO_CICLO_PADRAO_S) * 1_000;
      },
      (erro: unknown) => {
        if (!tratarCredencial(erro)) setErroConfiguracao(mensagemAmigavel(erro));
      },
    );
  }, [tratarCredencial]);

  useEffect(() => {
    if (!vinculado) return;
    recarregarBotoes();
    const timer = window.setInterval(recarregarBotoes, RELEITURA_BOTOES_MS);
    const pararHub = conectarHubEstacao({
      papel: "Emissao",
      handlers: { FilaAtualizada: () => recarregarBotoes() },
      aoConectar: recarregarBotoes,
      // The totem shows no connection state: while offline, the periodic re-read keeps the buttons current.
      aoMudarEstado: () => undefined,
      aoPerderCredencial: () => setVinculado(false),
    });
    return () => {
      window.clearInterval(timer);
      pararHub();
    };
  }, [vinculado, recarregarBotoes]);

  // Created in an effect, not in render: StrictMode's mount-unmount-mount must not leave a closed cycle behind.
  useEffect(() => {
    const ciclo = new EmissaoTotem<BotaoEmissao, FichaEmitidaDto>({
      enviar: (botao, chave) => emitirFichaNoCiclo(botao.filaId, botao.prioridadeId, chave),
      entregar: (ficha) =>
        new Promise<void>((resolve) => {
          fimEntregaRef.current = resolve;
          setEmissao({ tipo: "imprimindo", ficha });
        }),
      esperar: (ms) => new Promise<void>((resolve) => window.setTimeout(resolve, ms)),
      agora: () => Date.now(),
      novaChave: () => crypto.randomUUID(),
    });
    cicloRef.current = ciclo;
    return () => ciclo.encerrar();
  }, []);

  const emitir = async (botao: BotaoEmissao) => {
    const ciclo = cicloRef.current;
    if (ciclo === null || ciclo.ocupada()) return;
    // Same visible message for the whole attempt: network resends and the in-flight 409 wait are not errors.
    setEmissao({ tipo: "emitindo" });
    const fim = await ciclo.tocar(botao, prazoCicloMsRef.current);
    if (fim.tipo === "recusada") {
      if (tratarCredencial(fim.erro)) return;
      setEmissao({ tipo: "ocioso", aviso: mensagemAmigavel(fim.erro) });
      // A refusal for the window or for lost coverage means this button is stale.
      recarregarBotoes();
      return;
    }
    if (fim.tipo === "prazoVencido") setEmissao({ tipo: "ocioso", aviso: MENSAGEM_PRAZO_VENCIDO });
  };

  // Measured after layout: the senha font and the page height depend on the rendered ticket.
  useLayoutEffect(() => {
    if (emissao.tipo !== "imprimindo" || fichaRef.current === null) return;
    const senha = fichaRef.current.querySelector<HTMLElement>("[data-senha]");
    if (senha) ajustarFonteSenha(senha);
    definirPagina(fichaRef.current);
    let ativo = true;
    const senhaEmitida = emissao.ficha.senha;
    const concluir = () => {
      removerPagina();
      if (ativo) setEmissao({ tipo: "concluido", senha: senhaEmitida });
    };
    // A print failure never undoes the ficha (RF11): the senha stays on screen and the cycle still has to end,
    // or the emission cycle would stay busy and lock every button until a reload.
    void imprimir(LIMITE_IMPRESSAO_MS).then(concluir, (erro: unknown) => {
      console.warn("[paf-totem] falha ao imprimir a ficha", erro);
      concluir();
    });
    return () => {
      ativo = false;
    };
  }, [emissao]);

  useEffect(() => {
    if (emissao.tipo !== "concluido") return;
    const timer = window.setTimeout(() => {
      setEmissao({ tipo: "ocioso", aviso: null });
      fimEntregaRef.current?.();
      fimEntregaRef.current = null;
    }, TEMPO_CONFIRMACAO_MS);
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

  // The cycle runs until the screen is back to the initial state, confirmation included (RF11).
  const ocupado = emissao.tipo !== "ocioso";

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
        {emissao.tipo === "emitindo" && <p role="status">Emitindo sua senha… aguarde.</p>}
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

      {emissao.tipo === "imprimindo" && <FichaImpressa ref={fichaRef} ficha={emissao.ficha} />}
    </main>
  );
}
