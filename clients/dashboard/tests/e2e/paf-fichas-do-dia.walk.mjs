// F06 walk against a REAL API: the RF12 screen lists the current unit's atendimentos, cancels (F04) and
// reabilita (F05) by row, and reprints the stored ficha without a new number. The only stub is window.print,
// which records the printed ticket instead of opening the dialog.
//
// Run against the production build (`npm run build`, dist/config.json apiBase = the API, `vite preview`), fresh
// `seed-demo` database in a docker container:
//   PAF_API=http://localhost:5416 PAF_WEB=http://localhost:5516 PAF_PG=f06walk-pg node tests/e2e/paf-fichas-do-dia.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { execFileSync } from "node:child_process";
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
const PG = process.env.PAF_PG;
if (!API || !WEB || !PG) {
  console.error("PAF_API, PAF_WEB and PAF_PG are required");
  process.exit(2);
}
const TENANT = "acme";
const SENHA_DEMO = "Password123!";
const TIMEOUT = 15_000;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

// Setup and read-only checks go straight to PostgreSQL: a second API login would revoke the browser's session.
function sql(consulta) {
  return execFileSync("docker", ["exec", PG, "psql", "-U", "postgres", "-d", "fsh", "-At", "-c", consulta], {
    encoding: "utf8",
    timeout: TIMEOUT,
  }).trim();
}

const estadoNoBanco = (id) => sql(`SELECT "Estado" FROM paf."Atendimentos" WHERE "Id" = '${id}'`);

// Records what would be printed and ends the print at once, as --kiosk-printing does.
const IMPRESSAO = () => {
  window.__impressoes = [];
  window.print = () => {
    const ficha = document.querySelector("[data-ficha-impressa]");
    window.__impressoes.push(ficha ? ficha.textContent : null);
    setTimeout(() => window.dispatchEvent(new Event("afterprint")), 0);
  };
};

async function estadoNaTela(pagina, id, estado) {
  return pagina
    .locator(`tr[data-atendimento="${id}"] td[data-estado="${estado}"]`)
    .waitFor({ timeout: TIMEOUT })
    .then(
      () => true,
      () => false,
    );
}

const [unidade, fila, prioridade, letra, estacao] = sql(`
  SELECT u."Id", fp."FilaId", fp."PrioridadeId", fp."Letra",
         (SELECT e."Id" FROM paf."Estacoes" e WHERE e."TenantId" = u."TenantId" AND e."UnidadeId" = u."Id" LIMIT 1)
    FROM paf."Unidades" u
    JOIN paf."FilaPrioridades" fp ON fp."TenantId" = u."TenantId" AND fp."UnidadeId" = u."Id"
   WHERE u."TenantId" = '${TENANT}' AND u."Inicial"
   ORDER BY fp."Letra" LIMIT 1`).split("|");
if (!unidade || !estacao) {
  console.error("seed-demo sem unidade inicial com fila e estação no tenant acme");
  process.exit(2);
}

// Two fichas of today with numbers no totem reaches in a walk, each with its waiting atendimento.
function inserirFicha(numero) {
  return sql(`
    WITH f AS (
      INSERT INTO paf."Fichas" ("Id", "UnidadeId", "FilaId", "PrioridadeId", "Letra", "Numero", "IdentificadorPublico", "Canal", "EstacaoId", "ChaveIdempotencia", "TenantId")
      VALUES (gen_random_uuid(), '${unidade}', '${fila}', '${prioridade}', '${letra}', ${numero}, gen_random_uuid(), 'Totem', '${estacao}', gen_random_uuid()::text, '${TENANT}')
      RETURNING "Id")
    INSERT INTO paf."Atendimentos" ("Id", "UnidadeId", "FichaId", "FilaId", "PrioridadeId", "Estado", "TenantId")
    SELECT gen_random_uuid(), '${unidade}', f."Id", '${fila}', '${prioridade}', 'Aguardando', '${TENANT}' FROM f
    RETURNING "Id" || '|' || "FichaId"`).split("\n")[0].split("|");
}
const sufixo = Math.floor(Math.random() * 900) + 9000;
const [atendimento1] = inserirFicha(sufixo);
const [atendimento2, ficha2] = inserirFicha(sufixo + 1000);
const senha2 = `${letra}${sufixo + 1000}`;

const browser = await chromium.launch({ channel: "chrome" });
try {
  const contexto = await browser.newContext();
  await contexto.addInitScript(IMPRESSAO);
  const pagina = await contexto.newPage();
  await pagina.goto(`${WEB}/login`);
  await pagina.locator("#tenant").fill(TENANT);
  await pagina.locator("#email").fill("admin@acme.com");
  await pagina.locator("#password").fill(SENHA_DEMO);
  await pagina.locator('button[type="submit"]').click();
  await pagina.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: TIMEOUT });

  check("menu: entrada Fichas do dia para quem tem FichasDoDia.View",
    await pagina.locator('aside a[href="/paf/fichas-do-dia"]').first().waitFor({ timeout: TIMEOUT }).then(() => true, () => false));

  await pagina.goto(`${WEB}/paf/fichas-do-dia`);
  check("lista: as duas fichas inseridas aparecem Aguardando",
    (await estadoNaTela(pagina, atendimento1, "Aguardando")) && (await estadoNaTela(pagina, atendimento2, "Aguardando")));
  const totalBanco = sql(`SELECT count(*) FROM paf."Atendimentos" WHERE "UnidadeId" = '${unidade}' AND "Estado" = 'Aguardando'
    AND "DataOperacional" = (now() AT TIME ZONE 'America/Sao_Paulo')::date`);
  const totalTela = (await pagina.locator("[data-total-aguardando]").textContent())?.replace(/\D/g, "");
  check("total de aguardando confere com a contagem do banco", totalTela === totalBanco, `tela=${totalTela} banco=${totalBanco}`);

  // NEGATIVE: the operator says no to the confirmation; nothing is sent.
  pagina.once("dialog", (d) => void d.dismiss());
  await pagina.locator(`tr[data-atendimento="${atendimento1}"]`).getByRole("button", { name: "Cancelar" }).click();
  await pagina.waitForTimeout(500);
  check("cancelar recusado na confirmação não grava", estadoNoBanco(atendimento1) === "Aguardando");

  pagina.once("dialog", (d) => void d.accept());
  await pagina.locator(`tr[data-atendimento="${atendimento1}"]`).getByRole("button", { name: "Cancelar" }).click();
  check("cancelar: a linha vira Cancelado na tela", await estadoNaTela(pagina, atendimento1, "Cancelado"));
  check("cancelar: Cancelado no banco", estadoNoBanco(atendimento1) === "Cancelado");
  check("cancelar: o botão some e o reabilitar aparece",
    (await pagina.locator(`tr[data-atendimento="${atendimento1}"]`).getByRole("button", { name: "Cancelar" }).count()) === 0 &&
      (await pagina.locator(`tr[data-atendimento="${atendimento1}"]`).getByRole("button", { name: "Reabilitar" }).count()) === 1);

  await pagina.locator(`tr[data-atendimento="${atendimento1}"]`).getByRole("button", { name: "Reabilitar" }).click();
  check("reabilitar: a linha volta a Aguardando na tela", await estadoNaTela(pagina, atendimento1, "Aguardando"));
  check("reabilitar: Aguardando no banco", estadoNoBanco(atendimento1) === "Aguardando");

  const fichasAntes = sql(`SELECT count(*) FROM paf."Fichas" WHERE "TenantId" = '${TENANT}'`);
  await pagina.locator(`tr[data-atendimento="${atendimento2}"]`).getByRole("button", { name: "Reimprimir" }).click();
  await pagina.waitForFunction(() => (window.__impressoes ?? []).length > 0, null, { timeout: TIMEOUT }).catch(() => undefined);
  const impressoes = await pagina.evaluate(() => window.__impressoes);
  check("reimprimir: uma impressão com a senha da ficha", impressoes.length === 1 && (impressoes[0] ?? "").includes(senha2),
    JSON.stringify(impressoes));
  check("reimprimir: nenhuma ficha nova", sql(`SELECT count(*) FROM paf."Fichas" WHERE "TenantId" = '${TENANT}'`) === fichasAntes);
  check("reimprimir: a ficha impressa sai da página depois da impressão",
    await pagina.locator("[data-ficha-impressa]").waitFor({ state: "detached", timeout: TIMEOUT }).then(() => true, () => false));
  check("reimprimir: identificador público intacto",
    sql(`SELECT count(*) FROM paf."Fichas" WHERE "Id" = '${ficha2}' AND "Numero" = ${sufixo + 1000}`) === "1");

  await pagina.getByLabel("Status").selectOption("Cancelado");
  await pagina.waitForTimeout(800);
  check("filtro de status: só cancelados (nenhuma das duas, ambas aguardando)",
    (await pagina.locator(`tr[data-atendimento="${atendimento1}"], tr[data-atendimento="${atendimento2}"]`).count()) === 0);
  await pagina.getByLabel("Status").selectOption("");
  check("sem filtro as duas voltam", await estadoNaTela(pagina, atendimento2, "Aguardando"));
} finally {
  await browser.close();
}

console.log(falhas === 0 ? "OK" : `${falhas} FALHA(S)`);
process.exit(falhas === 0 ? 0 : 1);
