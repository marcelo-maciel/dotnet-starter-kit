// Checks an ASP.NET Core Identity V3 password hash (read from stdin, base64) against SENHA_ESPERADA and SENHA_RECUSADA
// (environment). Used by seed-homolog.test.sh to prove the seed stored the caller's own demo password and not the kit
// default: prints "esperada=<bool> recusada=<bool>" and never prints the hash or a password.
import { pbkdf2Sync, timingSafeEqual } from "node:crypto";

const prfs = { 0: "sha1", 1: "sha256", 2: "sha512" };

function confere(hash, senha) {
  if (hash[0] !== 0x01) throw new Error("hash fora do formato V3 do Identity");
  const prf = prfs[hash.readUInt32BE(1)];
  if (!prf) throw new Error("PRF desconhecida no hash");
  const iteracoes = hash.readUInt32BE(5);
  const tamanhoSal = hash.readUInt32BE(9);
  const sal = hash.subarray(13, 13 + tamanhoSal);
  const subchave = hash.subarray(13 + tamanhoSal);
  const calculada = pbkdf2Sync(senha, sal, iteracoes, subchave.length, prf);
  return timingSafeEqual(calculada, subchave);
}

let entrada = "";
process.stdin.on("data", (d) => (entrada += d)).on("end", () => {
  const texto = entrada.trim();
  const esperada = process.env.SENHA_ESPERADA;
  const recusada = process.env.SENHA_RECUSADA;
  if (!texto || !esperada || !recusada) {
    console.error("uso: echo <hash> | SENHA_ESPERADA=... SENHA_RECUSADA=... node seed-homolog-senha.mjs");
    process.exit(2);
  }
  const hash = Buffer.from(texto, "base64");
  console.log(`esperada=${confere(hash, esperada)} recusada=${confere(hash, recusada)}`);
});
