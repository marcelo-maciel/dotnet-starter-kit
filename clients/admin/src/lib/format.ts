import i18n from "@/i18n";

/** Every instant reads in Brasília, whatever the browser's zone: the product and its operators are in
 *  Brazil, and the API sends UTC. Without it an operator abroad, or a UTC test box, sees other hours. */
export const SAO_PAULO = "America/Sao_Paulo";

/**
 * Locale-aware formatting helpers built on the platform `Intl` APIs. The active
 * UI locale (`i18n.resolvedLanguage`, the language the text is in) is the default so formatted numbers, currency and
 * dates track the language switcher without every call site threading a locale.
 * Pass an explicit `locale` to override (e.g. rendering a fixed-locale export).
 */

// Active UI locale, guarded for the window before i18n has initialized.
const resolveLocale = (locale?: string): string => locale ?? i18n.resolvedLanguage ?? "en-US";

export function formatNumber(value: number, locale?: string): string {
  return new Intl.NumberFormat(resolveLocale(locale)).format(value);
}

export function formatCurrency(amount: number, currency: string, locale?: string): string {
  try {
    return new Intl.NumberFormat(resolveLocale(locale), {
      style: "currency",
      currency,
    }).format(amount);
  } catch {
    // Intl throws RangeError on an unknown/malformed ISO 4217 code — degrade to a
    // readable amount + raw code rather than crashing the render.
    return `${amount.toFixed(2)} ${currency}`;
  }
}

/** Date and time under the active UI locale. `toLocaleString()` with no argument uses the
 *  browser's locale instead, which puts 5/23/2026 next to Portuguese labels. */
export function formatDateTime(iso?: string | null, locale?: string): string {
  if (!iso) return "-";
  const date = new Date(iso);
  return Number.isNaN(date.getTime())
    ? iso
    : new Intl.DateTimeFormat(resolveLocale(locale), {
        timeZone: SAO_PAULO,
        dateStyle: "short",
        timeStyle: "medium",
      }).format(date);
}

/** Time of day under the active UI locale. */
export function formatTime(iso?: string | null, locale?: string): string {
  if (!iso) return "-";
  const date = new Date(iso);
  return Number.isNaN(date.getTime())
    ? iso
    : new Intl.DateTimeFormat(resolveLocale(locale), { timeZone: SAO_PAULO, timeStyle: "medium" }).format(date);
}

export function formatDate(iso?: string | null, locale?: string): string {
  if (!iso) return "-";
  const date = new Date(iso);
  return Number.isNaN(date.getTime())
    ? iso
    : new Intl.DateTimeFormat(resolveLocale(locale), {
        timeZone: SAO_PAULO,
        month: "short",
        day: "2-digit",
        year: "numeric",
      }).format(date);
}

// en-CA with h23 gives zero-padded numeric parts; "h23" and not `hour12: false`, which Chrome renders
// as "24:05" just after midnight.
const saoPauloParts = new Intl.DateTimeFormat("en-CA", {
  timeZone: SAO_PAULO,
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hourCycle: "h23",
});
const saoPauloOffset = new Intl.DateTimeFormat("en-US", { timeZone: SAO_PAULO, timeZoneName: "longOffset" });

function wallClock(date: Date): Record<"year" | "month" | "day" | "hour" | "minute" | "second", string> {
  const parts = Object.fromEntries(saoPauloParts.formatToParts(date).map((p) => [p.type, p.value]));
  return {
    year: parts.year,
    month: parts.month,
    day: parts.day,
    hour: parts.hour,
    minute: parts.minute,
    second: parts.second,
  };
}

// Brasília has had no daylight saving since 2019, but the offset is read, not assumed: "GMT-03:00".
function offsetAt(date: Date): string {
  return saoPauloOffset.formatToParts(date).find((p) => p.type === "timeZoneName")?.value.replace("GMT", "") || "+00:00";
}

/** The Brasília calendar day ("yyyy-MM-dd", a date input's value) of an instant; "" when unparseable. */
export function toSaoPauloDay(iso?: string | null): string {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const c = wallClock(date);
  return `${c.year}-${c.month}-${c.day}`;
}

/** The instant a Brasília calendar day ("yyyy-MM-dd") starts, as UTC ISO: "2026-05-01" → "2026-05-01T03:00:00.000Z". */
export function startOfSaoPauloDay(day: string): string {
  // The offset is read at that day's 03:00 UTC (Brasília's midnight at -03:00).
  return new Date(`${day}T00:00:00${offsetAt(new Date(`${day}T03:00:00Z`))}`).toISOString();
}

/** "04-30 14:22:01" in Brasília: the dense stamp of the audit and delivery lists. */
export function formatStampSeconds(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  const c = wallClock(date);
  return `${c.month}-${c.day} ${c.hour}:${c.minute}:${c.second}`;
}

/** "04-30 14:22" in Brasília: the dense stamp of the impersonation list. */
export function formatStampMinutes(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  const c = wallClock(date);
  return `${c.month}-${c.day} ${c.hour}:${c.minute}`;
}

/** "2026-04-30 14:22:01.234 -03:00": the forensic stamp of an audit record, Brasília with its offset. */
export function formatStampPrecise(iso?: string | null): string {
  if (!iso) return "-";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  const c = wallClock(date);
  const ms = String(date.getUTCMilliseconds()).padStart(3, "0");
  return `${c.year}-${c.month}-${c.day} ${c.hour}:${c.minute}:${c.second}.${ms} ${offsetAt(date)}`;
}
