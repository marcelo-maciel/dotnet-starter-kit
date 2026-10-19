/// <reference types="node" />
// Run: node --test src/features/paf/midias/midias-regras.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  conferirArquivoDaTroca,
  conferirArquivoNovo,
  duracaoDoMetadado,
  formatarBytes,
  formatoDoArquivo,
  LISTA_FORMATOS,
  MENSAGEM_FORMATO,
  MENSAGEM_TIPO_DA_TROCA,
  percentualEnviado,
  validarCadastro,
} from "./midias-regras.ts";
import { enviarEConfirmar, ErroDoEnvio, type EnvioAssinado } from "./envio-midia.ts";

test("a lista de formatos é a mesma da mensagem do servidor (FormatosMidia.ListaAceitos)", () => {
  assert.equal(LISTA_FORMATOS, ".mp4, .webm, .jpg, .jpeg, .png, .webp, .gif, .mp3, .m4a");
  assert.equal(MENSAGEM_FORMATO, "Formato não aceito. Formatos aceitos: .mp4, .webm, .jpg, .jpeg, .png, .webp, .gif, .mp3, .m4a.");
});

test("formato pela última extensão, sem diferença de caixa", () => {
  assert.equal(formatoDoArquivo("Institucional.MP4")?.mime, "video/mp4");
  assert.equal(formatoDoArquivo("x.avi.mp4")?.tipo, "Video");
  assert.equal(formatoDoArquivo("x.mp4.avi"), null);
  assert.equal(formatoDoArquivo("logo.PnG")?.mime, "image/png");
  assert.equal(formatoDoArquivo("jingle.m4a")?.mime, "audio/mp4");
  assert.equal(formatoDoArquivo("sem-extensao"), null);
  assert.equal(formatoDoArquivo("ponto-no-fim."), null);
  assert.equal(formatoDoArquivo(".mp4 "), null, "espaço na extensão não casa");
  assert.equal(formatoDoArquivo("vídeo.mpg"), null);
});

test("arquivo novo: .avi, .mov, .flv e .wmv recusados com a lista de formatos", () => {
  for (const nome of ["filme.avi", "filme.mov", "filme.flv", "filme.wmv", "filme"]) {
    assert.deepEqual(conferirArquivoNovo(nome), { ok: false, mensagem: MENSAGEM_FORMATO }, nome);
  }
  const ok = conferirArquivoNovo("filme.webm");
  assert.ok(ok.ok && ok.formato.tipo === "Video");
});

test("troca: mantém o tipo da mídia, e o formato vem antes do tipo", () => {
  assert.ok(conferirArquivoDaTroca("novo.webm", "Video").ok);
  assert.deepEqual(conferirArquivoDaTroca("foto.png", "Video"), { ok: false, mensagem: MENSAGEM_TIPO_DA_TROCA });
  assert.deepEqual(conferirArquivoDaTroca("filme.avi", "Video"), { ok: false, mensagem: MENSAGEM_FORMATO });
});

test("cadastro: descrição e duração conferidas antes do envio", () => {
  assert.deepEqual(validarCadastro("  Vitrine  ", " 30 "), { ok: true, descricao: "Vitrine", duracaoSegundos: 30 });
  assert.equal(validarCadastro("   ", "30").ok, false);
  assert.equal(validarCadastro("x".repeat(201), "30").ok, false);
  assert.equal(validarCadastro("x".repeat(200), "30").ok, true);
  for (const duracao of ["", "0", "-5", "1.5", "1e3", "0x10", "86401", "abc"]) {
    assert.equal(validarCadastro("Vitrine", duracao).ok, false, duracao);
  }
  assert.equal(validarCadastro("Vitrine", "86400").ok, true);
  assert.equal(validarCadastro("Vitrine", "1").ok, true);
});

test("duração do metadado: arredonda para cima, ignora o que o navegador não soube ler", () => {
  assert.equal(duracaoDoMetadado(30), 30);
  assert.equal(duracaoDoMetadado(30.04), 31);
  assert.equal(duracaoDoMetadado(0.2), 1);
  assert.equal(duracaoDoMetadado(0), null);
  assert.equal(duracaoDoMetadado(Number.NaN), null);
  assert.equal(duracaoDoMetadado(Number.POSITIVE_INFINITY), null, "stream sem duração");
  assert.equal(duracaoDoMetadado(100_000), 86_400);
});

test("percentual do envio e tamanhos legíveis", () => {
  assert.equal(percentualEnviado(0, 0), 0);
  assert.equal(percentualEnviado(50, 200), 25);
  assert.equal(percentualEnviado(199, 200), 99);
  assert.equal(percentualEnviado(300, 200), 100);
  assert.equal(formatarBytes(500 * 1024 * 1024), "500,0 MB");
  assert.equal(formatarBytes(1500), "2 KB");
  assert.equal(formatarBytes(10), "10 B");
});

const ENVIO: EnvioAssinado = { envioId: "e1", url: "http://s3/x", cabecalhosObrigatorios: { "Content-Type": "video/mp4" }, expiraEm: "", tetoBytes: 1000 };

function etapas(falhaNoPut: boolean) {
  const chamadas: string[] = [];
  return {
    chamadas,
    etapas: {
      pedir: async () => {
        chamadas.push("pedir");
        return ENVIO;
      },
      enviar: async () => {
        chamadas.push("enviar");
        if (falhaNoPut) throw new ErroDoEnvio("Falha de rede no envio do arquivo.");
      },
      confirmar: async (envioId: string) => {
        chamadas.push(`confirmar:${envioId}`);
        return { id: "m1" };
      },
    },
  };
}

test("envio: pede, envia e só então confirma", async () => {
  const { chamadas, etapas: e } = etapas(false);
  assert.deepEqual(await enviarEConfirmar(10, e), { id: "m1" });
  assert.deepEqual(chamadas, ["pedir", "enviar", "confirmar:e1"]);
});

test("envio: falha de rede no PUT não confirma, então nenhuma mídia é criada", async () => {
  const { chamadas, etapas: e } = etapas(true);
  await assert.rejects(enviarEConfirmar(10, e), ErroDoEnvio);
  assert.deepEqual(chamadas, ["pedir", "enviar"]);
});

test("envio: arquivo acima do teto do tipo é recusado antes do PUT", async () => {
  const { chamadas, etapas: e } = etapas(false);
  await assert.rejects(enviarEConfirmar(1001, e), (erro: unknown) => erro instanceof ErroDoEnvio && /limite deste tipo/.test(erro.message));
  assert.deepEqual(chamadas, ["pedir"]);
  const exato = etapas(false);
  await enviarEConfirmar(1000, exato.etapas);
  assert.equal(exato.chamadas.length, 3, "no teto exato ainda envia");
});

test("envio: arquivo vazio recusado sem pedir envio", async () => {
  const { chamadas, etapas: e } = etapas(false);
  await assert.rejects(enviarEConfirmar(0, e), ErroDoEnvio);
  assert.deepEqual(chamadas, []);
});
