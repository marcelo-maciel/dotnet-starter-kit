// C14 walk against a REAL API (no response mocks): the prioridade screen (RF05) refuses an empty name and a
// non-integer value on the screen and in the API, a higher value is called before Normal, and deactivating takes the
// button off the totem while the ficha already emitted stays callable.
//
// Run (API and Vite dev server already up, fresh `seed-demo` database):
//   PAF_API=http://localhost:5391 PAF_WEB=http://localhost:5184 node tests/e2e/paf-prioridades.walk.mjs
// Exit code 0 only when every check passed; each check prints PASS/FAIL.
import { chromium } from "@playwright/test";

const API = process.env.PAF_API;
const WEB = process.env.PAF_WEB;
if (!API || !WEB) {
  console.error("PAF_API and PAF_WEB are required");
  process.exit(2);
}
const TENANT = "acme";
const EMAIL = "admin@acme.com";
const SENHA_DEMO = "Password123!";
const TIMEOUT = 15_000;
const NOME = `Preferencial ${Date.now()}`;
const NOME_EDITADO = `Prioritário ${Date.now()}`;
const LETRA = "WP";

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

function visivel(locator) {
  return locator.waitFor({ timeout: TIMEOUT }).then(
    () => true,
    () => false,
  );
}

async function chamarApi(method, path, token, body) {
  const headers = { tenant: TENANT, "X-FSH-App": "dashboard" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  return fetch(API + path, {
    method,
    headers,
    body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body),
    signal: AbortSignal.timeout(TIMEOUT),
  });
}

async function api(method, path, token, body) {
  const r = await chamarApi(method, path, token, body);
  if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${await r.text()}`);
  const texto = await r.text();
  return texto === "" ? null : JSON.parse(texto);
}

// The totem prints through window.print; afterprint ends the emission cycle like --kiosk-printing would.
const STUB_IMPRESSAO = () => {
  window.print = () => setTimeout(() => window.dispatchEvent(new Event("afterprint")), 20);
};

const browser = await chromium.launch({ channel: "chrome", headless: true });
try {
  // ── Person: the admin in the browser. Every API call below reuses the browser's own token: a second login
  // would revoke the browser session (sessão única).
  const ctxAdmin = await browser.newContext();
  const pAdmin = await ctxAdmin.newPage();
  await pAdmin.goto(`${WEB}/login`);
  await pAdmin.locator("#tenant").fill(TENANT);
  await pAdmin.locator("#email").fill(EMAIL);
  await pAdmin.locator("#password").fill(SENHA_DEMO);
  await pAdmin.locator('button[type="submit"]').click();
  await pAdmin.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: TIMEOUT });
  const token = () => pAdmin.evaluate(() => localStorage.getItem("fsh.dashboard.accessToken"));

  const linkMenu = pAdmin.locator('aside a[href="/paf/prioridades"]');
  check("menu: entrada Prioridades para quem tem Prioridades.View", await visivel(linkMenu));
  await linkMenu.click();
  await pAdmin.waitForURL((u) => u.pathname === "/paf/prioridades", { timeout: TIMEOUT });
  check("tela: explica que valor maior é chamado antes", await visivel(pAdmin.getByText("valor maior é chamado antes")));

  // ── Refusals on the screen: nothing reaches the server.
  const posts = [];
  pAdmin.on("request", (r) => {
    if (r.method() === "POST" && r.url().endsWith("/api/v1/paf/prioridades")) posts.push(r.postData());
  });
  const novo = pAdmin.locator("[data-nova-prioridade]");
  const cadastrar = novo.getByRole("button", { name: "Cadastrar prioridade" });
  await novo.getByLabel("Nome").fill("   ");
  await novo.getByLabel("Valor").fill("5");
  await cadastrar.click();
  check("tela: nome vazio recusado", await visivel(pAdmin.getByRole("alert").filter({ hasText: "Informe o nome" })));
  await novo.getByLabel("Nome").fill(NOME);
  await novo.getByLabel("Valor").fill("1.5");
  await cadastrar.click();
  check("tela: valor 1.5 recusado", await visivel(pAdmin.getByRole("alert").filter({ hasText: "número inteiro" })));
  check("tela: nenhuma recusa chegou ao servidor", posts.length === 0, JSON.stringify(posts));

  // ── Refusals in the API, with the screen's token.
  for (const [rotulo, corpo] of [
    ["nome vazio", '{"nome":"","valor":1}'],
    ["valor 1.5", '{"nome":"X","valor":1.5}'],
    ["valor texto", '{"nome":"X","valor":"abc"}'],
  ]) {
    const r = await chamarApi("POST", "/api/v1/paf/prioridades", await token(), corpo);
    check(`api: POST com ${rotulo} dá 400`, r.status === 400, r.status);
  }
  const antes = await api("GET", "/api/v1/paf/prioridades", await token());
  const normal = antes.find((p) => p.nome === "Normal");
  {
    const r = await chamarApi("PUT", `/api/v1/paf/prioridades/${normal.id}`, await token(), `{"id":"${normal.id}","nome":"Normal","valor":2.5}`);
    check("api: PUT com valor 2.5 dá 400", r.status === 400, r.status);
  }

  // ── The server's 400 reaches the screen: the request leaves valid and is rewritten on the wire to an empty name.
  await pAdmin.route("**/api/v1/paf/prioridades", async (route) => {
    if (route.request().method() !== "POST") return route.continue();
    return route.continue({ postData: JSON.stringify({ nome: "", valor: 1 }) });
  });
  await novo.getByLabel("Valor").fill("10");
  await cadastrar.click();
  check("tela: 400 do servidor aparece como erro", await visivel(pAdmin.getByRole("alert").filter({ hasText: "Pedido inválido" })));
  await pAdmin.unroute("**/api/v1/paf/prioridades");
  const depoisDaRecusa = await api("GET", "/api/v1/paf/prioridades", await token());
  check("api: nada gravado pelas recusas", depoisDaRecusa.length === antes.length, `${antes.length} -> ${depoisDaRecusa.length}`);

  // ── Create: listed above Normal (value 10 > 0), warned that it is in no fila.
  await cadastrar.click();
  const linhaNova = pAdmin.locator("li[data-prioridade]").filter({ hasText: NOME });
  check("criar: aparece na lista", await visivel(linhaNova));
  const nomesNaTela = await pAdmin.locator("li[data-prioridade] [data-nome]").allTextContents();
  check("lista: valor maior primeiro", nomesNaTela.indexOf(NOME) < nomesNaTela.indexOf("Normal"), JSON.stringify(nomesNaTela));
  check("aviso: fora de qualquer fila", await visivel(linhaNova.locator("[data-aviso-fora-das-filas]")));
  const preferencial = (await api("GET", "/api/v1/paf/prioridades", await token())).find((p) => p.nome === NOME);

  // ── Wiring (screens of C09 and C04, here by API): the fila grid gets it first, the guichê after.
  const estacoes = await api("GET", "/api/v1/paf/estacoes", await token());
  const totem = estacoes.find((e) => e.tipo === "EmissaoPainel");
  const guiche = estacoes.find((e) => e.tipo === "Guiche");
  const filaId = totem.filas.find((f) => f.papel === "Emissao").filaId;
  const fila = (await api("GET", "/api/v1/paf/filas", await token())).find((f) => f.id === filaId);
  await api("POST", `/api/v1/paf/filas/${filaId}/prioridades`, await token(), {
    filaId,
    prioridadeId: preferencial.id,
    letra: LETRA,
    numeroInicial: 1,
    ordem: fila.prioridades.length,
    corBotao: null,
  });
  await pAdmin.reload();
  check(
    "aviso: na fila mas sem guichê",
    await visivel(linhaNova.locator("[data-aviso-sem-guiche]").filter({ hasText: `${fila.nome} (${LETRA})` })),
  );
  await api("PUT", `/api/v1/paf/estacoes/${guiche.id}`, await token(), {
    id: guiche.id,
    nome: guiche.nome,
    filas: guiche.filas,
    prioridades: [...guiche.prioridades, { prioridadeId: preferencial.id, ordem: guiche.prioridades.length }],
    ramal: guiche.ramal,
    configuracao: guiche.configuracao,
  });
  await pAdmin.reload();
  await linhaNova.waitFor({ timeout: TIMEOUT });
  check("aviso: coberta, sem aviso", (await linhaNova.locator("[data-aviso-sem-guiche], [data-aviso-fora-das-filas]").count()) === 0);

  // ── Edit name and value on the screen.
  await linhaNova.getByRole("button", { name: "Editar" }).click();
  await pAdmin.getByLabel(`Novo nome de ${NOME}`).fill(NOME_EDITADO);
  await pAdmin.getByLabel(`Novo valor de ${NOME}`).fill("20.5");
  await pAdmin.locator("li[data-prioridade]").getByRole("button", { name: "Salvar" }).click();
  check("editar: valor 20.5 recusado na tela", await visivel(pAdmin.getByRole("alert").filter({ hasText: "número inteiro" })));
  await pAdmin.getByLabel(`Novo valor de ${NOME}`).fill("20");
  await pAdmin.locator("li[data-prioridade]").getByRole("button", { name: "Salvar" }).click();
  const linha = pAdmin.locator("li[data-prioridade]").filter({ hasText: NOME_EDITADO });
  check("editar: nome novo na lista", await visivel(linha));
  check("editar: valor 20 na lista", (await linha.locator("[data-valor]").textContent())?.trim() === "Valor 20");
  const gravada = (await api("GET", "/api/v1/paf/prioridades", await token())).find((p) => p.id === preferencial.id);
  check("editar: gravado no servidor", gravada?.nome === NOME_EDITADO && gravada?.valor === 20, JSON.stringify(gravada));

  // ── Totem: a station link redeemed in its own context; Normal first, then the higher value.
  const link = await api("POST", `/api/v1/paf/estacoes/${totem.id}/vinculo`, await token(), {});
  const ctxEstacao = await browser.newContext();
  await ctxEstacao.addInitScript(STUB_IMPRESSAO);
  const pVinculo = await ctxEstacao.newPage();
  await pVinculo.goto(`${WEB}/paf/estacao/vinculo#tenant=${link.tenant}&token=${link.token}`);
  await pVinculo.getByRole("link", { name: "Abrir o totem" }).waitFor({ timeout: TIMEOUT });
  const pTotem = await ctxEstacao.newPage();
  await pTotem.goto(`${WEB}/paf/totem`);
  const botaoNormal = pTotem.getByRole("button", { name: new RegExp(`${fila.nome}\\s*Normal`) });
  const botaoNovo = pTotem.getByRole("button", { name: new RegExp(NOME_EDITADO) });
  check("totem: botão da prioridade nova", await visivel(botaoNovo));

  async function emitir(botao) {
    await botao.click();
    const senha = pTotem.locator("[data-senha-emitida]");
    await senha.waitFor({ timeout: TIMEOUT });
    const texto = (await senha.textContent())?.trim();
    await senha.waitFor({ state: "detached", timeout: TIMEOUT });
    return texto;
  }
  const senhaNormal = await emitir(botaoNormal);
  const senhaNova = await emitir(botaoNovo);
  const emitidasEm = Date.now();
  check("totem: senha da prioridade nova com a letra dela", senhaNova?.startsWith(LETRA) === true, `${senhaNormal} ${senhaNova}`);

  // ── Deactivate on the screen: the confirmation names the totem button that goes away.
  await linha.getByRole("button", { name: "Desativar" }).click();
  const confirmacao = linha.locator("[data-confirmar-desativar]");
  check(
    "desativar: aviso das combinações que somem do totem",
    await visivel(confirmacao.getByText(`Somem do totem os botões: ${fila.nome} (${LETRA}).`)),
  );
  await confirmacao.getByRole("button", { name: "Voltar" }).click();
  const aindaAtiva = (await api("GET", "/api/v1/paf/prioridades", await token())).find((p) => p.id === preferencial.id);
  check("desativar: voltar não grava", aindaAtiva?.ativa === true);
  await linha.getByRole("button", { name: "Desativar" }).click();
  await linha.getByRole("button", { name: "Confirmar desativação" }).click();
  check("desativar: linha marcada Inativa", await visivel(linha.getByText("Inativa")));

  await pTotem.reload();
  await botaoNormal.waitFor({ timeout: TIMEOUT });
  check("totem: botão da prioridade desativada sumiu", (await botaoNovo.count()) === 0);

  // ── Guichê: the five-second guard (seção 4) passes, then the disabled, higher-value ficha is called first.
  const espera = 6_000 - (Date.now() - emitidasEm);
  if (espera > 0) await pAdmin.waitForTimeout(espera);
  await pAdmin.goto(`${WEB}/paf/guiche`);
  const seletorUnidade = pAdmin.locator("main").getByLabel("Unidade");
  const seletorGuiche = pAdmin.getByLabel("Guichê", { exact: true });
  await Promise.race([seletorUnidade.waitFor({ timeout: TIMEOUT }), seletorGuiche.waitFor({ timeout: TIMEOUT })]);
  if (await pAdmin.getByRole("button", { name: "Usar esta unidade" }).isVisible()) {
    await seletorUnidade.selectOption({ label: "Loja Demo" });
    await pAdmin.getByRole("button", { name: "Usar esta unidade" }).click();
  }
  await seletorGuiche.waitFor({ timeout: TIMEOUT });
  const opcao = await seletorGuiche.locator("option").evaluateAll(
    (os, nome) => os.find((o) => o.textContent.includes(nome))?.getAttribute("value"),
    guiche.nome,
  );
  await seletorGuiche.selectOption(opcao);
  await pAdmin.getByRole("button", { name: "Usar este guichê" }).click();
  await pAdmin.getByRole("button", { name: "Chamar próxima" }).click();
  const chamada = pAdmin.locator("[data-senha-guiche]");
  await chamada.waitFor({ timeout: TIMEOUT });
  check(
    "guichê: valor maior chamado antes da Normal, mesmo desativada depois da emissão",
    (await chamada.textContent())?.trim() === senhaNova,
    `${await chamada.textContent()} esperado ${senhaNova} (Normal ${senhaNormal})`,
  );

  // ── Reactivate: the button comes back.
  await pAdmin.goto(`${WEB}/paf/prioridades`);
  await linha.getByRole("button", { name: "Reativar" }).click();
  await linha.getByRole("button", { name: "Desativar" }).waitFor({ timeout: TIMEOUT });
  await pTotem.reload();
  check("reativar: botão volta ao totem", await visivel(botaoNovo));
} catch (e) {
  check("walk terminou sem exceção", false, e instanceof Error ? e.stack : String(e));
} finally {
  await browser.close();
}
console.log(falhas === 0 ? "WALK OK" : `WALK FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
