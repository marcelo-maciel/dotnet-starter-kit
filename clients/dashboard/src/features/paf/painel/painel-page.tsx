import { useCallback, useEffect, useReducer, useRef, useState } from "react";
import { lerCredencial } from "@/features/paf/estacao/credencial-estacao";
import { conectarHubEstacao, type EstadoConexao } from "@/features/paf/estacao/hub-estacao";
import {
  lerNumeroDaConfiguracao,
  obterConfiguracaoPainel,
  obterEstadoPainel,
} from "@/features/paf/estacao/estacao-api";
import { CredencialEstacaoInvalidaError } from "@/features/paf/estacao/estacao-http";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import {
  ESTADO_INICIAL_PAINEL,
  reduzirPainel,
  type AtendimentoAtualizadoEvento,
  type SenhaChamadaEvento,
} from "@/features/paf/painel/painel-estado";
import { carregarVozes, escolherVoz, falar, textoFalado } from "@/features/paf/painel/voz";

// ponytail: the RF04 "tempo de exibição da chamada" has no cadastro yet; read from the station JSON
// block under this key, 8 s when absent. Upgrade: the panel appearance cadastro writes it.
const CHAVE_TEMPO_EXIBICAO = "tempoExibicaoChamadaSegundos";
const TEMPO_EXIBICAO_PADRAO_S = 8;
const LIMITE_FALA_MS = 15_000;
const ESPERA_VOZES_MS = 3_000;

const ROTULO_CONEXAO: Record<EstadoConexao, string> = {
  conectando: "Conectando…",
  conectado: "Conectado",
  reconectando: "Reconectando…",
  desconectado: "Desconectado",
};

const ROTULO_ESTADO: Record<string, string> = {
  Chamado: "Chamada",
  EmAtendimento: "Em atendimento",
  Encerrado: "Encerrado",
};

function esperar(ms: number): Promise<void> {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
}

export function PainelPage() {
  const [vinculado, setVinculado] = useState(() => lerCredencial("Painel") !== null);
  const [estado, dispatch] = useReducer(reduzirPainel, ESTADO_INICIAL_PAINEL);
  const [conexao, setConexao] = useState<EstadoConexao>("conectando");
  const [nomeEstacao, setNomeEstacao] = useState("");
  // A ref, not state: the config may arrive mid-highlight and must not restart the current announcement.
  const tempoExibicaoMsRef = useRef(TEMPO_EXIBICAO_PADRAO_S * 1_000);
  const [erro, setErro] = useState<string | null>(null);
  const [avisoVoz, setAvisoVoz] = useState<string | null>(null);
  const vozRef = useRef<SpeechSynthesisVoice | null>(null);

  const perderCredencial = useCallback(() => setVinculado(false), []);

  const tratarErro = useCallback(
    (e: unknown) => {
      if (e instanceof CredencialEstacaoInvalidaError) {
        perderCredencial();
        return;
      }
      setErro(mensagemAmigavel(e));
    },
    [perderCredencial],
  );

  const recarregarEstado = useCallback(() => {
    obterEstadoPainel().then(
      (dto) => {
        setErro(null);
        dispatch({ tipo: "estadoCarregado", estado: dto });
      },
      tratarErro,
    );
  }, [tratarErro]);

  useEffect(() => {
    if (!vinculado) return;
    obterConfiguracaoPainel().then((cfg) => {
      setNomeEstacao(cfg.nome);
      tempoExibicaoMsRef.current =
        lerNumeroDaConfiguracao(cfg.configuracao, CHAVE_TEMPO_EXIBICAO, TEMPO_EXIBICAO_PADRAO_S) * 1_000;
    }, tratarErro);

    return conectarHubEstacao({
      papel: "Painel",
      handlers: {
        SenhaChamada: (p) => dispatch({ tipo: "senhaChamada", evento: p as SenhaChamadaEvento }),
        SenhaRechamada: (p) => dispatch({ tipo: "senhaRechamada", evento: p as SenhaChamadaEvento }),
        AtendimentoAtualizado: (p) =>
          dispatch({ tipo: "atendimentoAtualizado", evento: p as AtendimentoAtualizadoEvento }),
      },
      aoConectar: recarregarEstado,
      aoMudarEstado: setConexao,
      aoPerderCredencial: perderCredencial,
    });
  }, [vinculado, recarregarEstado, tratarErro, perderCredencial]);

  // One announcement at a time: the next one leaves the queue only when the highlight ends.
  useEffect(() => {
    if (estado.anunciando && estado.destaque === null && estado.fila.length > 0) {
      dispatch({ tipo: "destaqueIniciado" });
    }
  }, [estado.anunciando, estado.destaque, estado.fila.length]);

  const destaque = estado.destaque;
  useEffect(() => {
    if (destaque === null) return;
    let ativo = true;
    // The highlight lasts the configured time even when the voice is shorter, and waits for the
    // voice when it is longer, so a senha is never cut mid-sentence.
    // A synthesis that throws costs only the voice: the highlight still runs its time and the queue moves.
    const fala = falar(textoFalado(destaque.senha), vozRef.current, LIMITE_FALA_MS).catch((erro: unknown) =>
      console.warn("[paf-painel] fala recusada", erro),
    );
    void Promise.all([fala, esperar(tempoExibicaoMsRef.current)]).then(() => {
      if (ativo) dispatch({ tipo: "destaqueEncerrado" });
    });
    return () => {
      ativo = false;
    };
  }, [destaque]);

  const iniciar = async () => {
    const voz = escolherVoz(await carregarVozes(ESPERA_VOZES_MS));
    vozRef.current = voz;
    setAvisoVoz(
      voz === null
        ? "Nenhuma voz em português instalada: as senhas aparecem na tela, mas não serão faladas. Instale o pacote de voz pt-BR do Windows."
        : null,
    );
    dispatch({ tipo: "anunciosIniciados" });
  };

  if (!vinculado) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-black p-8 text-center text-white">
        <p role="alert" className="max-w-xl text-2xl">
          Painel não vinculado ou vínculo revogado. Abra nesta janela o link de vínculo gerado no cadastro da estação.
        </p>
      </main>
    );
  }

  const principal = destaque ?? estado.ultimoDestaque ?? estado.chamadas[0] ?? null;

  return (
    <main className="flex min-h-screen flex-col bg-black text-white">
      <header className="flex items-center justify-between px-8 py-4 text-lg">
        <span>{nomeEstacao}</span>
        <span role="status" aria-label="Conexão" data-conexao={conexao}>
          {ROTULO_CONEXAO[conexao]}
        </span>
      </header>

      {erro && (
        <p role="alert" className="mx-8 rounded-md bg-red-900 px-4 py-2">
          {erro}
        </p>
      )}
      {avisoVoz && (
        <p role="alert" className="mx-8 rounded-md bg-yellow-800 px-4 py-2">
          {avisoVoz}
        </p>
      )}

      <section aria-label="Chamada atual" className="flex flex-1 flex-col items-center justify-center gap-4 px-8">
        {principal ? (
          <div
            data-destaque={destaque !== null ? "sim" : "nao"}
            className={
              destaque !== null
                ? "rounded-3xl bg-yellow-400 px-16 py-10 text-center text-black"
                : "rounded-3xl px-16 py-10 text-center"
            }
          >
            <div className="text-4xl">Senha</div>
            <div className="text-[10rem] leading-none font-bold" data-senha-atual>
              {principal.senha}
            </div>
            <div className="mt-4 text-5xl">{principal.nomeEstacao}</div>
          </div>
        ) : (
          <p className="text-3xl text-white/60">Aguardando chamadas</p>
        )}
      </section>

      <section aria-label="Últimas chamadas" className="border-t border-white/20 px-8 py-4">
        <h2 className="mb-2 text-xl text-white/70">Últimas chamadas</h2>
        <ol className="grid grid-cols-2 gap-2 text-2xl md:grid-cols-5">
          {estado.chamadas.map((c) => (
            <li key={c.atendimentoId} data-atendimento={c.atendimentoId} className="rounded-lg bg-white/10 px-3 py-2">
              <span className="font-bold">{c.senha}</span> <span className="text-white/80">{c.nomeEstacao}</span>
              <span className="block text-base text-white/60">{ROTULO_ESTADO[c.estado] ?? c.estado}</span>
            </li>
          ))}
        </ol>
      </section>

      {!estado.anunciando && (
        <div className="fixed inset-0 flex items-center justify-center bg-black/80">
          <button
            type="button"
            onClick={() => void iniciar()}
            className="rounded-2xl bg-yellow-400 px-12 py-8 text-4xl font-bold text-black"
          >
            Iniciar painel
          </button>
        </div>
      )}
    </main>
  );
}
