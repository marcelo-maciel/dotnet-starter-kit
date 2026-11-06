// D04/O15 smoke: the dashboard container serves, at /config.json, the inactivity timeout of the PAF template.
// env.ts (`positiveOr`) silently falls back to 20 minutes on anything that is not a positive finite number, so a
// quoted value or an empty substitution would still parse as JSON and log the idle attendant out: the checks
// compare type and value, not presence.
//
// Run by smoke-inatividade.sh:
//   PAF_DASHBOARD=http://127.0.0.1:<porta> PAF_ESPERADO=paf|kit PAF_API_URL=<valor> node deploy/paf/smoke-inatividade.mjs
// PAF_ESPERADO=paf: the PAF template is mounted (8 h). PAF_ESPERADO=kit: control without the mount, the kit's
// template has no timeout, which proves the mount is what changes it. Exit 0 only when every check passed.
const BASE = process.env.PAF_DASHBOARD;
const ESPERADO = process.env.PAF_ESPERADO;
const API_URL = process.env.PAF_API_URL;
if (!BASE || !API_URL || (ESPERADO !== "paf" && ESPERADO !== "kit")) {
  console.error("PAF_DASHBOARD, PAF_API_URL and PAF_ESPERADO=paf|kit are required");
  process.exit(2);
}
const TIMEOUT_MS = 10_000;
// One shift for every dashboard screen (D04, decided 17/10/2026).
const PRAZO_TURNO_MS = 8 * 60 * 60_000;
const AVISO_MS = 60_000;

let falhas = 0;
function check(nome, ok, detalhe) {
  console.log(`${ok ? "PASS" : "FAIL"} ${nome}${detalhe === undefined ? "" : ` :: ${detalhe}`}`);
  if (!ok) falhas += 1;
}

const r = await fetch(`${BASE}/config.json`, { cache: "no-store", signal: AbortSignal.timeout(TIMEOUT_MS) });
check("GET /config.json 200", r.status === 200, `status ${r.status}`);
check("Cache-Control no-store", r.headers.get("cache-control") === "no-store", r.headers.get("cache-control") ?? "ausente");
const texto = await r.text();
let cfg = null;
try {
  cfg = JSON.parse(texto);
} catch (erro) {
  check("JSON válido", false, `${erro.message} :: ${texto}`);
}

if (cfg !== null) {
  check("apiBase substituído pelo entrypoint", cfg.apiBase === API_URL, JSON.stringify(cfg.apiBase));
  check("defaultTenant substituído pelo entrypoint", typeof cfg.defaultTenant === "string" && cfg.defaultTenant.length > 0, JSON.stringify(cfg.defaultTenant));
  if (ESPERADO === "paf") {
    check(
      "inactivityIdleMs é número igual a 8 h",
      typeof cfg.inactivityIdleMs === "number" && cfg.inactivityIdleMs === PRAZO_TURNO_MS,
      `${typeof cfg.inactivityIdleMs} ${JSON.stringify(cfg.inactivityIdleMs)}`,
    );
    check(
      "inactivityWarningMs é número igual a 60 s",
      typeof cfg.inactivityWarningMs === "number" && cfg.inactivityWarningMs === AVISO_MS,
      `${typeof cfg.inactivityWarningMs} ${JSON.stringify(cfg.inactivityWarningMs)}`,
    );
  } else {
    check("sem a montagem, o template do kit não traz inactivityIdleMs", !("inactivityIdleMs" in cfg), JSON.stringify(cfg));
  }
}

console.log(falhas === 0 ? "SMOKE-INATIVIDADE OK" : `SMOKE-INATIVIDADE FALHOU (${falhas})`);
process.exit(falhas === 0 ? 0 : 1);
