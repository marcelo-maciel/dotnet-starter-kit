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
  APARENCIA_PAINEL_PADRAO,
  INTERVALO_ALTERNANCIA_MS,
  corDaChamada,
  formatarDataHora,
  lerAparenciaPainel,
  type AparenciaPainel,
} from "@/features/paf/painel/aparencia-painel";
import {
  ESTADO_INICIAL_PAINEL,
  reduzirPainel,
  type AtendimentoAtualizadoEvento,
  type SenhaChamadaEvento,
} from "@/features/paf/painel/painel-estado";
import { carregarVozes, escolherVoz, falar } from "@/features/paf/painel/voz";
import { estiloImagemDeFundo, useImagemDeFundo } from "@/features/paf/estacao/imagem-fundo";
import { lerLogotipo } from "@/features/paf/logotipo/logotipo";
import { LogotipoImagem } from "@/features/paf/logotipo/logotipo-imagem";
import { SenhaContida } from "@/features/paf/painel/senha-contida";
import {
  ANUNCIO_PADRAO,
  lerAnuncioDaChamada,
  textoDoAnuncio,
  type AnuncioDaChamada,
} from "@/features/paf/painel/anuncio-chamada";

// The RF04 "tempo de exibição da chamada", written by the station cadastro at the root of the JSON block; 8 s when absent.
const CHAVE_TEMPO_EXIBICAO = "tempoExibicaoChamadaSegundos";
const TEMPO_EXIBICAO_PADRAO_S = 8;
// ponytail: a station cadastro change pushes no notice, so the appearance follows it by this periodic re-read
// (plus FilaAtualizada and reconnection), like the totem's buttons. Upgrade: a station-updated notice.
const RELEITURA_CONFIGURACAO_MS = 60_000;
const RELOGIO_MS = 1_000;
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
  const [logotipo, setLogotipo] = useState<string | null>(null);
  const [aparencia, setAparencia] = useState<AparenciaPainel>(APARENCIA_PAINEL_PADRAO);
  const [urlFundo, setUrlFundo] = useState<string | null>(null);
  const [agora, setAgora] = useState(() => new Date());
  const [faseAlternancia, setFaseAlternancia] = useState(0);
  // A ref, not state: the config may arrive mid-highlight and must not restart the current announcement.
  const tempoExibicaoMsRef = useRef(TEMPO_EXIBICAO_PADRAO_S * 1_000);
  // C20: state for the text under the senha, ref for the speech, which is composed once when a highlight starts.
  const [anuncio, setAnuncio] = useState<AnuncioDaChamada>(ANUNCIO_PADRAO);
  const anuncioRef = useRef<AnuncioDaChamada>(ANUNCIO_PADRAO);
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

  const recarregarConfiguracao = useCallback(() => {
    obterConfiguracaoPainel().then((cfg) => {
      setNomeEstacao(cfg.nome);
      tempoExibicaoMsRef.current =
        lerNumeroDaConfiguracao(cfg.configuracao, CHAVE_TEMPO_EXIBICAO, TEMPO_EXIBICAO_PADRAO_S) * 1_000;
      setAparencia(lerAparenciaPainel(cfg.configuracao));
      setUrlFundo(cfg.imagemFundoPainel?.url ?? null);
      anuncioRef.current = lerAnuncioDaChamada(cfg.anuncioDaChamada);
      setAnuncio(anuncioRef.current);
      // C19: re-read with the configuration (every call, reconnection, 60 s), well inside the URL's hour.
      setLogotipo(lerLogotipo(cfg.logotipo));
    }, tratarErro);
  }, [tratarErro]);

  const fundo = useImagemDeFundo(urlFundo, recarregarConfiguracao);

  useEffect(() => {
    if (!vinculado) return;
    recarregarConfiguracao();
    const timer = window.setInterval(recarregarConfiguracao, RELEITURA_CONFIGURACAO_MS);

    const pararHub = conectarHubEstacao({
      papel: "Painel",
      handlers: {
        FilaAtualizada: () => recarregarConfiguracao(),
        SenhaChamada: (p) => dispatch({ tipo: "senhaChamada", evento: p as SenhaChamadaEvento }),
        SenhaRechamada: (p) => dispatch({ tipo: "senhaRechamada", evento: p as SenhaChamadaEvento }),
        AtendimentoAtualizado: (p) =>
          dispatch({ tipo: "atendimentoAtualizado", evento: p as AtendimentoAtualizadoEvento }),
      },
      aoConectar: () => {
        recarregarEstado();
        recarregarConfiguracao();
      },
      aoMudarEstado: setConexao,
      aoPerderCredencial: perderCredencial,
    });
    return () => {
      window.clearInterval(timer);
      pararHub();
    };
  }, [vinculado, recarregarEstado, recarregarConfiguracao, perderCredencial]);

  const mostrarDataHora = aparencia.mostrarDataHora;
  useEffect(() => {
    if (!mostrarDataHora) return;
    setAgora(new Date());
    const timer = window.setInterval(() => setAgora(new Date()), RELOGIO_MS);
    return () => window.clearInterval(timer);
  }, [mostrarDataHora]);

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
    const fala = falar(textoDoAnuncio(destaque.senha, anuncioRef.current), vozRef.current, LIMITE_FALA_MS).catch((erro: unknown) =>
      console.warn("[paf-painel] fala recusada", erro),
    );
    void Promise.all([fala, esperar(tempoExibicaoMsRef.current)]).then(() => {
      if (ativo) dispatch({ tipo: "destaqueEncerrado" });
    });
    return () => {
      ativo = false;
    };
  }, [destaque]);

  // The legacy attention cue: the highlighted call alternates its text color, only when the cadastro set the colors.
  const alternar = destaque !== null && aparencia.cores !== null;
  useEffect(() => {
    setFaseAlternancia(0);
    if (!alternar) return;
    const timer = window.setInterval(() => setFaseAlternancia((f) => f + 1), INTERVALO_ALTERNANCIA_MS);
    return () => window.clearInterval(timer);
  }, [alternar, destaque]);

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
  // Every text below is sized in em, so the cadastro's font size scales the whole screen; absent, the base is the
  // page's 16 px and the screen looks as before. With the cadastro colors, only those colors are used: the
  // translucent whites and the yellow highlight box were never checked against the chosen background (RNF19).
  const cores = aparencia.cores;

  return (
    <main
      className="flex min-h-screen flex-col bg-black text-white"
      style={{
        backgroundColor: cores?.fundo,
        color: cores?.filas,
        fontSize: aparencia.tamanhoFontePx ?? undefined,
        ...estiloImagemDeFundo(fundo),
      }}
      data-imagem-fundo={fundo ?? undefined}
    >
      <header className="flex items-center justify-between px-8 py-4 text-[1.125em]">
        <span className="flex items-center gap-4">
          <LogotipoImagem url={logotipo} altura="3em" larguraMaxima="12em" />
          <span>{nomeEstacao}</span>
        </span>
        {aparencia.mostrarDataHora && <span data-data-hora>{formatarDataHora(agora)}</span>}
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
            data-chamada
            className={
              cores !== null
                ? "max-w-full rounded-3xl border-[0.1em] px-16 py-10 text-center"
                : destaque !== null
                  ? "max-w-full rounded-3xl bg-yellow-400 px-16 py-10 text-center text-black"
                  : "max-w-full rounded-3xl px-16 py-10 text-center"
            }
            style={
              cores === null
                ? undefined
                : {
                    color: corDaChamada(cores, destaque !== null, faseAlternancia),
                    borderColor: destaque !== null ? "currentcolor" : "transparent",
                  }
            }
          >
            <div className="text-[2.25em]">Senha</div>
            <SenhaContida senha={principal.senha} className="text-[10em] leading-none font-bold" />
            <div className="mt-4 text-[3em]">{principal.nomeEstacao}</div>
            {destaque !== null && anuncio.mensagemAoChamar !== "" && (
              <div className="mt-4 text-[2em]" data-mensagem-chamada>
                {anuncio.mensagemAoChamar}
              </div>
            )}
          </div>
        ) : (
          <p className={cores !== null ? "text-[1.875em]" : "text-[1.875em] text-white/60"}>Aguardando chamadas</p>
        )}
      </section>

      {aparencia.mostrarUltimasChamadas && (
        <section aria-label="Últimas chamadas" className="border-t border-white/20 px-8 py-4">
          <h2 className={cores !== null ? "mb-2 text-[1.25em]" : "mb-2 text-[1.25em] text-white/70"}>Últimas chamadas</h2>
          {/* F20: still 2 and 5 columns, but never narrower than 5em, so at a large cadastro font a glyph of the senha
              fits its cell and the senha wraps inside it instead of running out. */}
          <ol className="grid grid-cols-[repeat(auto-fill,minmax(max(calc(50%_-_5px),min(100%,5em)),1fr))] gap-2 text-[1.5em] md:grid-cols-[repeat(auto-fill,minmax(max(calc(20%_-_7px),min(100%,5em)),1fr))]">
            {estado.chamadas.map((c) => (
              <li
                key={c.atendimentoId}
                data-atendimento={c.atendimentoId}
                className={cores !== null ? "min-w-0 rounded-lg border px-3 py-2 [overflow-wrap:anywhere]" : "min-w-0 rounded-lg bg-white/10 px-3 py-2 [overflow-wrap:anywhere]"}
              >
                <span className="font-bold">{c.senha}</span>{" "}
                <span className={cores !== null ? undefined : "text-white/80"}>{c.nomeEstacao}</span>
                {/* 16 px at the default base: 1 / 1.5 of the list's size. */}
                <span className={cores !== null ? "block text-[0.6667em]" : "block text-[0.6667em] text-white/60"}>
                  {ROTULO_ESTADO[c.estado] ?? c.estado}
                </span>
              </li>
            ))}
          </ol>
        </section>
      )}

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
