// Fails when an inline <script> of the built index.html has no matching 'sha256-...' in docker/csp.conf.template,
// or when the template carries a hash no inline script produces. The browser hashes the script text with newlines
// normalized to LF, so CRLF checkouts hash the same as Linux ones. Run after `vite build` (Dockerfile).
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";

const html = readFileSync(new URL("../dist/index.html", import.meta.url), "utf8").replace(/\r\n/g, "\n");
const csp = readFileSync(new URL("../docker/csp.conf.template", import.meta.url), "utf8");

const inline = [...html.matchAll(/<script(?![^>]*\ssrc=)[^>]*>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
const built = inline.map((body) => `sha256-${createHash("sha256").update(body).digest("base64")}`);
const declared = [...csp.matchAll(/'(sha256-[A-Za-z0-9+/=]+)'/g)].map((m) => m[1]);

const missing = built.filter((h) => !declared.includes(h));
const stale = declared.filter((h) => !built.includes(h));
if (missing.length > 0 || stale.length > 0) {
  console.error(`CSP hash mismatch. Built inline scripts: ${built.join(", ") || "none"}.`);
  console.error(
    `Missing from docker/csp.conf.template: ${missing.join(", ") || "none"}. Stale in it: ${stale.join(", ") || "none"}.`,
  );
  process.exit(1);
}
console.log(`CSP hashes match (${built.length} inline script(s)).`);
