import { useId, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ApiRequestError } from "@/lib/api-client";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { enviarDireto } from "@/features/paf/midias/envio-direto";
import { enviarEConfirmar, ErroDoEnvio, type EnvioAssinado } from "@/features/paf/midias/envio-midia";
import { lerDuracaoDoArquivo } from "@/features/paf/midias/metadado-midia";
import {
  ativarMidia,
  atualizarMidia,
  criarMidia,
  desativarMidia,
  listarMidias,
  obterUrlDaMidia,
  pedirEnvio,
  pedirEnvioDaTroca,
  trocarArquivo,
  type MidiaDto,
} from "@/features/paf/midias/midias-api";
import {
  ACEITOS_NO_SELETOR,
  conferirArquivoDaTroca,
  conferirArquivoNovo,
  DESCRICAO_MAXIMA,
  LISTA_FORMATOS,
  percentualEnviado,
  ROTULO_TIPO,
  validarCadastro,
  type FormatoMidia,
} from "@/features/paf/midias/midias-regras";

// Same key as the station cadastro's mídia pickers (C17, M10 list editor): a file sent here shows there at once.
const CHAVE_MIDIAS = ["paf", "midias"] as const;
const PERMISSAO_CRIAR = "Permissions.PAF.Midias.Create";
const PERMISSAO_ALTERAR = "Permissions.PAF.Midias.Update";
// ponytail: fixed deadlines; make them settings if a client's link needs more.
const PRAZO_SEM_PROGRESSO_MS = 60_000;
const PRAZO_METADADO_MS = 10_000;

type Progresso = { percentual: number } | null;

/** The server's validation reasons when it sent them, else the friendly line; a browser-side refusal as written. */
function mensagensDoErro(erro: unknown): string[] {
  if (erro instanceof ErroDoEnvio) return [erro.message];
  if (erro instanceof ApiRequestError && erro.problem?.errors !== undefined) {
    const errors = erro.problem.errors;
    const linhas = Array.isArray(errors) ? errors : Object.values(errors).flat();
    if (linhas.length > 0) return linhas;
  }
  return [mensagemAmigavel(erro)];
}

function enviarComProgresso(arquivo: File, aoProgredir: (p: Progresso) => void) {
  return (envio: EnvioAssinado) =>
    enviarDireto(envio, arquivo, (enviados, total) => aoProgredir({ percentual: percentualEnviado(enviados, total) }), PRAZO_SEM_PROGRESSO_MS);
}

export function MidiasPage() {
  const queryClient = useQueryClient();
  const midias = useQuery({ queryKey: CHAVE_MIDIAS, queryFn: listarMidias });
  const { user, permissionsHydrated } = useAuth();
  const podeCriar = permissionsHydrated && (user?.permissions.includes(PERMISSAO_CRIAR) ?? false);
  const podeAlterar = permissionsHydrated && (user?.permissions.includes(PERMISSAO_ALTERAR) ?? false);

  const [erros, setErros] = useState<string[]>([]);
  const [aviso, setAviso] = useState<string | null>(null);
  const [ocupado, setOcupado] = useState(false);
  const [edicao, setEdicao] = useState<{ id: string; descricao: string; duracao: string } | null>(null);
  const [trocando, setTrocando] = useState<string | null>(null);
  const [previa, setPrevia] = useState<string | null>(null);

  const executar = async (acao: () => Promise<unknown>, sucesso: string) => {
    setOcupado(true);
    setErros([]);
    setAviso(null);
    try {
      await acao();
      setAviso(sucesso);
      return true;
    } catch (e) {
      setErros(mensagensDoErro(e));
      return false;
    } finally {
      // A 409 means someone else wrote in between: the list is re-read either way, so a retry starts from it.
      await queryClient.invalidateQueries({ queryKey: CHAVE_MIDIAS });
      setOcupado(false);
    }
  };

  const salvarEdicao = async () => {
    if (!edicao) return;
    const midia = midias.data?.find((m) => m.id === edicao.id);
    if (!midia) return;
    const validado = validarCadastro(edicao.descricao, edicao.duracao);
    if (!validado.ok) {
      setErros([validado.mensagem]);
      return;
    }
    if (await executar(() => atualizarMidia(midia.id, validado.descricao, validado.duracaoSegundos, midia.versao), `${validado.descricao} salva.`)) {
      setEdicao(null);
    }
  };

  return (
    <div className="mx-auto max-w-5xl space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold">Arquivos de mídia</h1>
        <p className="text-sm text-[var(--color-muted-foreground)]">
          Vídeos, imagens e áudios da unidade corrente, exibidos no painel. Formatos aceitos: {LISTA_FORMATOS}.
        </p>
      </header>

      {erros.length > 0 && (
        <ul role="alert" data-erros-midia className="list-disc rounded-md border border-[var(--color-destructive)] py-3 pl-8 pr-4 text-[var(--color-destructive)]">
          {erros.map((e) => (
            <li key={e}>{e}</li>
          ))}
        </ul>
      )}
      {aviso && (
        <p role="status" data-aviso-midia className="rounded-md border border-[var(--color-border)] px-4 py-3 text-sm">
          {aviso}
        </p>
      )}

      {podeCriar && (
        <NovaMidia
          ocupado={ocupado}
          onErro={(linhas) => {
            setAviso(null);
            setErros(linhas);
          }}
          onEnviar={(arquivo, formato, descricao, duracao, aoProgredir) =>
            executar(
              () =>
                enviarEConfirmar(arquivo.size, {
                  pedir: () => pedirEnvio(arquivo.name, formato.mime),
                  enviar: enviarComProgresso(arquivo, aoProgredir),
                  confirmar: (envioId) => criarMidia(envioId, descricao, duracao),
                }),
              `${descricao} cadastrada.`,
            )
          }
        />
      )}

      {midias.isPending && <p className="text-[var(--color-muted-foreground)]">Carregando…</p>}
      {midias.isError && <p role="alert">{mensagemAmigavel(midias.error)}</p>}
      {midias.data?.length === 0 && <p>Nenhum arquivo nesta unidade.</p>}

      <ul className="space-y-3">
        {(midias.data ?? []).map((m) => (
          <li key={m.id} data-midia={m.id} className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4">
            {edicao?.id === m.id ? (
              <div className="flex flex-wrap items-end gap-2">
                <Input
                  aria-label={`Nova descrição de ${m.descricao}`}
                  className="max-w-sm"
                  value={edicao.descricao}
                  maxLength={DESCRICAO_MAXIMA}
                  onChange={(e) => setEdicao({ ...edicao, descricao: e.target.value })}
                />
                <Input
                  aria-label={`Nova duração de ${m.descricao}, em segundos`}
                  className="w-32"
                  inputMode="numeric"
                  value={edicao.duracao}
                  onChange={(e) => setEdicao({ ...edicao, duracao: e.target.value })}
                />
                <Button size="sm" disabled={ocupado} onClick={() => void salvarEdicao()}>
                  Salvar
                </Button>
                <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setEdicao(null)}>
                  Cancelar
                </Button>
              </div>
            ) : (
              <div className="flex flex-wrap items-center gap-3">
                <span className="font-medium" data-descricao>{m.descricao}</span>
                <span className="text-sm text-[var(--color-muted-foreground)]">{ROTULO_TIPO[m.tipo]}</span>
                <span className="text-sm" data-duracao>{m.duracaoSegundos} s</span>
                {!m.ativa && <span className="text-sm text-[var(--color-destructive)]">Inativa</span>}
                <div className="ml-auto flex flex-wrap gap-2">
                  <Button size="sm" variant="outline" aria-expanded={previa === m.id} onClick={() => setPrevia(previa === m.id ? null : m.id)}>
                    {previa === m.id ? "Fechar pré-visualização" : "Pré-visualizar"}
                  </Button>
                  {podeAlterar && (
                    <>
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={ocupado}
                        onClick={() => {
                          setErros([]);
                          setEdicao({ id: m.id, descricao: m.descricao, duracao: String(m.duracaoSegundos) });
                        }}
                      >
                        Editar
                      </Button>
                      <Button size="sm" variant="outline" disabled={ocupado} onClick={() => setTrocando(trocando === m.id ? null : m.id)}>
                        Trocar arquivo
                      </Button>
                      {m.ativa ? (
                        <Button size="sm" variant="destructive" disabled={ocupado} onClick={() => void executar(() => desativarMidia(m.id), `${m.descricao} desativada: sai do painel.`)}>
                          Desativar
                        </Button>
                      ) : (
                        <Button size="sm" variant="secondary" disabled={ocupado} onClick={() => void executar(() => ativarMidia(m.id), `${m.descricao} ativada.`)}>
                          Ativar
                        </Button>
                      )}
                    </>
                  )}
                </div>
              </div>
            )}

            {trocando === m.id && podeAlterar && (
              <TrocaDeArquivo
                midia={m}
                ocupado={ocupado}
                onErro={(linhas) => {
                  setAviso(null);
                  setErros(linhas);
                }}
                onEnviar={async (arquivo, formato, aoProgredir) => {
                  const trocou = await executar(
                    () =>
                      enviarEConfirmar(arquivo.size, {
                        pedir: () => pedirEnvioDaTroca(m.id, arquivo.name, formato.mime),
                        enviar: enviarComProgresso(arquivo, aoProgredir),
                        confirmar: (envioId) => trocarArquivo(m.id, envioId, m.versao),
                      }),
                    `Arquivo de ${m.descricao} trocado.`,
                  );
                  if (trocou) setTrocando(null);
                }}
              />
            )}

            {previa === m.id && <Previa midia={m} />}
          </li>
        ))}
      </ul>
    </div>
  );
}

type AoProgredir = (p: Progresso) => void;

function BarraDeProgresso({ progresso }: { progresso: Progresso }) {
  if (progresso === null) return null;
  return (
    <div className="flex items-center gap-2 text-sm" data-progresso-envio>
      <progress className="h-2 w-64" max={100} value={progresso.percentual} aria-label="Progresso do envio do arquivo" />
      <span aria-live="polite">{progresso.percentual}%</span>
    </div>
  );
}

function NovaMidia(props: {
  ocupado: boolean;
  onErro: (linhas: string[]) => void;
  onEnviar: (arquivo: File, formato: FormatoMidia, descricao: string, duracao: number, aoProgredir: AoProgredir) => Promise<boolean>;
}) {
  const idArquivo = useId();
  const idDescricao = useId();
  const idDuracao = useId();
  const seletor = useRef<HTMLInputElement>(null);
  const [arquivo, setArquivo] = useState<{ arquivo: File; formato: FormatoMidia } | null>(null);
  const [descricao, setDescricao] = useState("");
  const [duracao, setDuracao] = useState("");
  const [lendoDuracao, setLendoDuracao] = useState(false);
  const [progresso, setProgresso] = useState<Progresso>(null);

  const escolher = async (escolhido: File | undefined) => {
    setArquivo(null);
    if (!escolhido) return;
    const conferido = conferirArquivoNovo(escolhido.name);
    if (!conferido.ok) {
      props.onErro([conferido.mensagem]);
      if (seletor.current) seletor.current.value = "";
      return;
    }
    props.onErro([]);
    setArquivo({ arquivo: escolhido, formato: conferido.formato });
    setLendoDuracao(true);
    const lida = await lerDuracaoDoArquivo(escolhido, conferido.formato.tipo, PRAZO_METADADO_MS);
    setLendoDuracao(false);
    if (lida !== null) setDuracao(String(lida));
  };

  const enviar = async () => {
    if (!arquivo) {
      props.onErro(["Escolha o arquivo."]);
      return;
    }
    const validado = validarCadastro(descricao, duracao);
    if (!validado.ok) {
      props.onErro([validado.mensagem]);
      return;
    }
    setProgresso({ percentual: 0 });
    const enviou = await props.onEnviar(arquivo.arquivo, arquivo.formato, validado.descricao, validado.duracaoSegundos, setProgresso);
    setProgresso(null);
    if (enviou) {
      setArquivo(null);
      setDescricao("");
      setDuracao("");
      if (seletor.current) seletor.current.value = "";
    }
  };

  return (
    <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-nova-midia>
      <h2 className="font-medium">Novo arquivo</h2>
      <div className="grid gap-3 sm:grid-cols-[1fr_1fr_10rem]">
        <div className="space-y-1">
          <Label htmlFor={idArquivo}>Arquivo</Label>
          <input
            id={idArquivo}
            ref={seletor}
            type="file"
            accept={ACEITOS_NO_SELETOR}
            className="block w-full text-sm"
            onChange={(e) => void escolher(e.target.files?.[0])}
          />
        </div>
        <div className="space-y-1">
          <Label htmlFor={idDescricao}>Descrição</Label>
          <Input id={idDescricao} value={descricao} maxLength={DESCRICAO_MAXIMA} onChange={(e) => setDescricao(e.target.value)} />
        </div>
        <div className="space-y-1">
          <Label htmlFor={idDuracao}>Duração (segundos)</Label>
          <Input id={idDuracao} inputMode="numeric" value={duracao} onChange={(e) => setDuracao(e.target.value)} />
        </div>
      </div>
      {arquivo && (
        <p className="text-sm text-[var(--color-muted-foreground)]" data-tipo-escolhido>
          {ROTULO_TIPO[arquivo.formato.tipo]}
          {lendoDuracao ? ": lendo a duração do arquivo…" : arquivo.formato.tipo === "Imagem" ? ": informe por quantos segundos a imagem fica na tela." : ""}
        </p>
      )}
      <BarraDeProgresso progresso={progresso} />
      <Button disabled={props.ocupado || lendoDuracao} onClick={() => void enviar()}>
        Enviar e cadastrar
      </Button>
    </section>
  );
}

function TrocaDeArquivo(props: {
  midia: MidiaDto;
  ocupado: boolean;
  onErro: (linhas: string[]) => void;
  onEnviar: (arquivo: File, formato: FormatoMidia, aoProgredir: AoProgredir) => Promise<void>;
}) {
  const id = useId();
  const [progresso, setProgresso] = useState<Progresso>(null);

  const escolher = async (escolhido: File | undefined) => {
    if (!escolhido) return;
    const conferido = conferirArquivoDaTroca(escolhido.name, props.midia.tipo);
    if (!conferido.ok) {
      props.onErro([conferido.mensagem]);
      return;
    }
    setProgresso({ percentual: 0 });
    await props.onEnviar(escolhido, conferido.formato, setProgresso);
    setProgresso(null);
  };

  return (
    <div className="space-y-2 rounded-md border border-[var(--color-border)] p-3" data-troca-arquivo>
      <Label htmlFor={id}>Novo arquivo ({ROTULO_TIPO[props.midia.tipo].toLowerCase()}) para {props.midia.descricao}</Label>
      <input
        id={id}
        type="file"
        accept={ACEITOS_NO_SELETOR}
        disabled={props.ocupado}
        className="block w-full text-sm"
        onChange={(e) => {
          void escolher(e.target.files?.[0]);
          e.target.value = "";
        }}
      />
      <p className="text-sm text-[var(--color-muted-foreground)]">A duração não muda; edite-a se o novo arquivo for mais longo ou mais curto.</p>
      <BarraDeProgresso progresso={progresso} />
    </div>
  );
}

/** M04 URL per version: a swap raises Versao, so the preview asks again and shows the new file. */
function Previa({ midia }: { midia: MidiaDto }) {
  const url = useQuery({
    queryKey: [...CHAVE_MIDIAS, "url", midia.id, midia.versao],
    queryFn: () => obterUrlDaMidia(midia.id),
    enabled: midia.ativa,
    retry: false,
  });
  if (!midia.ativa) return <p className="text-sm" data-previa>Mídia inativa: ative para pré-visualizar.</p>;
  if (url.isPending) return <p className="text-sm text-[var(--color-muted-foreground)]">Carregando a pré-visualização…</p>;
  if (url.isError) return <p role="alert" className="text-sm">{mensagemAmigavel(url.error)}</p>;
  const rotulo = `Pré-visualização de ${midia.descricao}`;
  return (
    <div data-previa data-previa-versao={midia.versao}>
      {midia.tipo === "Imagem" && <img src={url.data.url} alt={rotulo} className="max-h-64 rounded-md" />}
      {midia.tipo === "Video" && (
        <video src={url.data.url} controls aria-label={rotulo} className="max-h-64 rounded-md">
          <track kind="captions" />
        </video>
      )}
      {midia.tipo === "Audio" && (
        <audio src={url.data.url} controls aria-label={rotulo}>
          <track kind="captions" />
        </audio>
      )}
    </div>
  );
}
