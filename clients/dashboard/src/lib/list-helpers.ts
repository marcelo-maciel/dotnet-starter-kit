import i18n from "@/i18n";
import { ApiRequestError } from "@/lib/api-client";

/** Every instant reads in Brasília, whatever the browser's zone: the product and its tenants are in
 *  Brazil, and the API sends UTC. */
export const SAO_PAULO = "America/Sao_Paulo";

// "30/04/2026" — the Brazilian calendar day of the instant.
const dateLong = new Intl.DateTimeFormat("pt-BR", {
  timeZone: SAO_PAULO,
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
});

export function formatDate(iso: string | null | undefined) {
  if (!iso) return "—";
  return dateLong.format(new Date(iso));
}

// Tabular form for ledger/registry rows; the numeric pt-BR day already lines up.
export function formatDateMono(iso: string | null | undefined) {
  return formatDate(iso);
}

// "22:30" — Brasília wall-clock time, never the browser's zone. "h23" and not `hour12: false`, which
// Chrome renders as "24:05" just after midnight.
const timeShort = new Intl.DateTimeFormat("pt-BR", {
  timeZone: SAO_PAULO,
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});

// "30/04/2026 22:30" — date + Brasília time for lists such as the session list.
export function formatDateTime(iso: string | null | undefined) {
  if (!iso) return "—";
  return `${formatDate(iso)} ${timeShort.format(new Date(iso))}`;
}

// "30/04/2026 · 22:30" — date + Brasília time for audit/detail panels.
export function formatDateTimeMono(iso: string | null | undefined) {
  if (!iso) return "—";
  return `${formatDateMono(iso)} · ${timeShort.format(new Date(iso))}`;
}

// Locale-grouped integer/decimal under the active UI language (pt-BR "1.234"), as #1384 formats counts.
export function formatNumber(value: number) {
  return new Intl.NumberFormat(i18n.resolvedLanguage).format(value);
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

// "há 3 dias", "há 2 meses" — terse relative time for the secondary line.
export function formatRelative(iso: string | null | undefined) {
  if (!iso) return "";
  const diffMs = Date.now() - new Date(iso).getTime();
  if (Number.isNaN(diffMs) || diffMs < 0) return "";
  const sec = Math.floor(diffMs / 1000);
  if (sec < 60) return "agora";
  const min = Math.floor(sec / 60);
  if (min < 60) return `há ${min} min`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `há ${hr} h`;
  const day = Math.floor(hr / 24);
  if (day < 30) return `há ${plural(day, "dia", "dias")}`;
  const mo = Math.floor(day / 30);
  if (mo < 12) return `há ${plural(mo, "mês", "meses")}`;
  const yr = Math.floor(day / 365);
  return `há ${plural(yr, "ano", "anos")}`;
}

export function pad2(n: number) {
  return n.toString().padStart(2, "0");
}

// Mirror the server's slug derivation so editors can show a live preview.
export function slugify(value: string) {
  const lower = value.trim().toLowerCase();
  const chars = [...lower].map((c) => (/[a-z0-9]/.test(c) ? c : "-"));
  let s = chars.join("").replace(/^-+|-+$/g, "");
  while (s.includes("--")) s = s.replace(/--/g, "-");
  return s;
}

export function formatMoney(amount: number, currency: string) {
  try {
    return new Intl.NumberFormat(undefined, {
      style: "currency",
      currency,
    }).format(amount);
  } catch {
    return `${amount.toFixed(2)} ${currency}`;
  }
}

// Flatten the ProblemDetails `errors` extension into a list of human messages.
// FluentValidation sends a field-keyed map ({ Email: ["..."] }); CustomException
// (e.g. Identity registration failures) sends a flat string[]. Both end up here.
function problemErrorMessages(errors: unknown): string[] {
  if (Array.isArray(errors)) {
    return errors.filter((e): e is string => typeof e === "string");
  }
  if (errors && typeof errors === "object") {
    return Object.values(errors as Record<string, unknown>)
      .flatMap((v) => (Array.isArray(v) ? v : [v]))
      .filter((e): e is string => typeof e === "string");
  }
  return [];
}

// Surface API/network/runtime errors with the same formatting everywhere.
// When the server includes specific reasons in the ProblemDetails `errors`
// extension (validation failures, Identity errors like "Email is already
// taken"), show those — they tell the user what to fix. Otherwise fall back to
// the Dev-only `reason` extension / detail / title so JwtBearer rejection
// causes (expired token, signing key drift, etc) stay visible in development.
export function describe(err: unknown): string {
  if (err instanceof ApiRequestError) {
    const details = problemErrorMessages(err.problem?.errors);
    if (details.length > 0) {
      return details.join(" ");
    }
    const reason =
      err.problem?.reason ??
      err.problem?.detail ??
      err.problem?.title ??
      err.message;
    return `${err.status} ${reason}`;
  }
  if (err instanceof Error) return err.message;
  return String(err);
}
