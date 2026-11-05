/// <reference types="node" />
// Run: node --test src/features/paf/navegacao/textos-do-kit.test.ts (Node 22 strips the types, D21).
// The PAF ships in pt-BR only and the dashboard has no i18n layer: the kit screens a client tenant reaches carry their
// text in the source, so a word of the kit's English left there is a word the client reads. This gate scans those
// sources with the TypeScript parser and fails on any English UI word of the denylist below, in visible positions only.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const DASHBOARD = fileURLToPath(new URL("../../../../", import.meta.url));
const SRC = join(DASHBOARD, "src");

// Not reached by a client tenant: the PAF's own screens (already pt-BR, and home of this test), the identity pages
// (root only), the kit modules the PAF does not ship (their routes redirect home) and the components only they use.
const FORA_DO_ESCOPO = [
  /^features\/paf\//,
  /^pages\/identity\//,
  /^pages\/files\//,
  /^pages\/(subscription|wallet|invoices|invoice-detail)\.tsx$/,
  // Minha conta keeps Perfil, Segurança and Aparência only (02/11/2026); these routes redirect home.
  /^pages\/settings\/(api-keys|notifications|branding)\.tsx$/,
  /^api\/tenants\.ts$/,
  /^components\/file\/(file-preview-dialog|file-dropzone)\.tsx$/,
  /^api\/(billing|wallet|files)\.ts$/,
  /^components\/list\/combobox\.tsx$/,
  // The demo-account picker only renders with the runtime demoMode flag, which the PAF deploy never turns on.
  /^components\/auth\/demo-accounts-dialog\.tsx$/,
  /^pages\/login\.demo-accounts\.ts$/,
];

// English the kit screens showed before the translation (inventory of 02/11/2026). Case-sensitive, whole words.
const PALAVRAS_EM_INGLES = [
  "Settings", "Sign out", "Sign in", "Search", "Cancel", "Profile", "Security", "Appearance", "Branding",
  "Notifications", "API keys", "Page not found", "Reset your password", "Send reset link", "Remembered it",
  "Save", "Saving", "Saved", "Delete", "Remove", "Loading", "No results", "No matches", "Refresh", "Reload",
  "Close", "Unknown", "Failed", "Could not", "Couldn't", "Back to", "Light", "Dark", "Theme", "Accent", "Font",
  "Density", "Password", "Passwords", "Email", "Tenant", "Session", "Sessions", "Revoke", "Revoked", "Active",
  "Inactive", "Live", "Recent", "Something went wrong", "Still there", "Connected", "Connecting",
  "Reconnecting", "Offline", "Healthy", "Degraded", "Unhealthy", "Dependencies", "Event", "Events", "Severity",
  "Activity", "Overview", "Health", "Audit", "Copy", "Copied", "Clear", "Previous page", "Next page", "Upload",
  "Enable", "Disable", "Two-factor", "Confirm", "Continue", "Try again", "Users", "Phone", "First name",
  "Last name", "Dismiss", "Open", "View", "See all", "Show password", "Hide password", "Welcome", "Good morning",
  "Good afternoon", "Good evening", "Expand sidebar", "Collapse sidebar", "Skip to main content",
];
const PALAVRAS_EM_INGLES_MINUSCULAS = ["just now", "ago", "seconds", "required", "unsaved", "revoked", "enabled", "disabled"];

const DENYLIST = [
  ...PALAVRAS_EM_INGLES.map((p) => new RegExp(`(^|[^\\p{L}\\p{N}_-])${escapar(p)}(?![\\p{L}\\p{N}_])`, "u")),
  ...PALAVRAS_EM_INGLES_MINUSCULAS.map((p) => new RegExp(`(^|[^\\p{L}\\p{N}_-])${escapar(p)}(?![\\p{L}\\p{N}_])`, "u")),
];

function escapar(texto: string): string {
  return texto.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

// JSX attributes whose value the user sees or hears.
const ATRIBUTOS_VISIVEIS = new Set([
  "label", "placeholder", "aria-label", "title", "alt", "description", "hint", "caption", "body", "heading",
  "subtitle", "eyebrow", "lead", "accent", "trail", "sublabel", "unit", "units",
]);
// Object properties whose value lands on screen (menus, option lists, label maps).
const PROPRIEDADES_VISIVEIS = new Set([
  "label", "hint", "title", "description", "heading", "caption", "body", "headline", "subline", "text", "message",
  "detail", "name", "placeholder",
]);

export type Achado = { arquivo: string; linha: number; texto: string; palavra: string };

function emPosicaoVisivel(no: ts.Node): boolean {
  let atual: ts.Node = no;
  for (let pai = no.parent; pai; atual = pai, pai = pai.parent) {
    if (ts.isJsxAttribute(pai)) return ATRIBUTOS_VISIVEIS.has(pai.name.getText());
    if (ts.isJsxExpression(pai)) return true;
    if (ts.isPropertyAssignment(pai)) {
      if (pai.initializer !== atual) return false;
      const chave = pai.name.getText().replace(/^["']|["']$/g, "");
      // A label map keyed by an enum value (HEALTH_STATUS_LABEL, AUDIT_EVENT_TYPE_LABELS) is visible too.
      const mapa = pai.parent.parent;
      const nomeDoMapa = mapa && ts.isVariableDeclaration(mapa) ? mapa.name.getText() : "";
      return PROPRIEDADES_VISIVEIS.has(chave) || /LABEL|COPY/.test(nomeDoMapa);
    }
    if (ts.isCallExpression(pai) || ts.isNewExpression(pai)) {
      const chamada = pai.expression.getText();
      return /^(toast(\.\w+)?|set(Local)?Error|setNotice|ApiRequestError|Error|DOMException|apiErrorMessage)$/.test(chamada);
    }
    if (ts.isReturnStatement(pai)) return true;
    if (ts.isArrayLiteralExpression(pai) || ts.isConditionalExpression(pai) || ts.isBinaryExpression(pai)) {
      if (ts.isBinaryExpression(pai) && /^(===|!==|==|!=)$/.test(pai.operatorToken.getText())) return false;
      continue;
    }
    if (ts.isParenthesizedExpression(pai) || ts.isTemplateSpan(pai) || ts.isTemplateExpression(pai)) continue;
    return false;
  }
  return false;
}

export function textosEmIngles(arquivo: string, fonte: string): Achado[] {
  const tipo = arquivo.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS;
  const sf = ts.createSourceFile(arquivo, fonte, ts.ScriptTarget.Latest, true, tipo);
  const achados: Achado[] = [];
  const conferir = (no: ts.Node, texto: string) => {
    const palavra = DENYLIST.find((r) => r.test(texto));
    if (!palavra) return;
    const linha = sf.getLineAndCharacterOfPosition(no.getStart(sf)).line + 1;
    achados.push({ arquivo, linha, texto: texto.trim().replace(/\s+/g, " "), palavra: palavra.source });
  };
  const visitar = (no: ts.Node) => {
    if (ts.isImportDeclaration(no) || ts.isExportDeclaration(no) || ts.isLiteralTypeNode(no)) return;
    if (ts.isJsxText(no)) conferir(no, no.getText(sf).replace(/&apos;/g, "'"));
    else if ((ts.isStringLiteral(no) || ts.isNoSubstitutionTemplateLiteral(no)) && emPosicaoVisivel(no)) conferir(no, no.text);
    else if (ts.isTemplateExpression(no) && emPosicaoVisivel(no)) {
      conferir(no, [no.head.text, ...no.templateSpans.map((s) => s.literal.text)].join(" "));
    }
    ts.forEachChild(no, visitar);
  };
  visitar(sf);
  return achados;
}

function arquivosDoCliente(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const caminho = join(dir, e.name);
    if (e.isDirectory()) return arquivosDoCliente(caminho);
    return /\.tsx?$/.test(e.name) && !/\.test\.ts$/.test(e.name) ? [caminho] : [];
  });
}

test("as telas do kit que o cliente vê não têm texto em inglês", () => {
  const arquivos = arquivosDoCliente(SRC)
    .map((caminho) => relative(SRC, caminho).replaceAll("\\", "/"))
    .filter((rel) => !FORA_DO_ESCOPO.some((r) => r.test(rel)));
  assert.ok(arquivos.length > 50, `poucos arquivos no escopo (${arquivos.length}): o caminho de src mudou?`);
  const achados = arquivos.flatMap((rel) => textosEmIngles(rel, readFileSync(join(SRC, rel), "utf8")));
  assert.deepEqual(
    achados.map((a) => `${a.arquivo}:${a.linha} "${a.texto}"`),
    [],
    "texto em inglês visível ao cliente",
  );
});

test("o html declara o idioma pt-BR", () => {
  assert.match(readFileSync(join(DASHBOARD, "index.html"), "utf8"), /<html lang="pt-BR"/);
});

test("o scanner acha inglês em texto, atributo, aviso, ternário, mapa de rótulos e template", () => {
  const fonte = [
    'import { Settings } from "lucide-react";',
    "const LABEL: Record<string, string> = { Healthy: \"Healthy\" };",
    "export function X({ ok }: { ok: boolean }) {",
    '  toast.success("Profile saved");',
    "  toast.info(`Revoked ${1} sessions`);",
    "  return (",
    '    <div className="flex" aria-label="Close" title={ok ? "Live" : "Pausado"}>',
    "      Sign out",
    '      {ok ? "Loading…" : "Carregando…"}',
    "    </div>",
    "  );",
    "}",
  ].join("\n");
  const textos = textosEmIngles("x.tsx", fonte).map((a) => a.texto);
  assert.deepEqual(textos.sort(), ["Close", "Healthy", "Live", "Loading…", "Profile saved", "Revoked sessions", "Sign out"].sort());
});

test("o scanner ignora identificador, classe, comparação, permissão e texto em português", () => {
  const fonte = [
    'import { SettingsLayout } from "@/pages/settings/settings-layout";',
    'type Estado = "Healthy" | "Degraded";',
    'const PERM = "Permissions.Sessions.ViewAll";',
    "export function Y({ s }: { s: string }) {",
    '  if (s === "Healthy") return null;',
    "  return (",
    '    <a href="/settings/security" className="text-sm" data-x="Close">',
    "      Sair · Minha conta · Sessões · Evento · E-mail · agora",
    "    </a>",
    "  );",
    "}",
  ].join("\n");
  assert.deepEqual(textosEmIngles("y.tsx", fonte), []);
});
