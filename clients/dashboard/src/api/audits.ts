import { apiFetch } from "@/lib/api-client";

// ────────────────────────────────────────────────────────────────────────
// Enum mirrors of the API contracts. Keep in lockstep with
// src/Modules/Auditing/Modules.Auditing.Contracts/AuditEnums.cs.
// ────────────────────────────────────────────────────────────────────────

// The API serializes single-value enums as their string name
// (global JsonStringEnumConverter). Mirror them as string unions.
export const AuditEventType = {
  None: "None",
  EntityChange: "EntityChange",
  Security: "Security",
  Activity: "Activity",
  Exception: "Exception",
} as const;
export type AuditEventType = (typeof AuditEventType)[keyof typeof AuditEventType];

export const AUDIT_EVENT_TYPE_LABELS: Record<AuditEventType, string> = {
  None: "Desconhecido",
  EntityChange: "Entidade",
  Security: "Segurança",
  Activity: "Atividade",
  Exception: "Exceção",
};

export const AuditSeverity = {
  None: "None",
  Trace: "Trace",
  Debug: "Debug",
  Information: "Information",
  Warning: "Warning",
  Error: "Error",
  Critical: "Critical",
} as const;
export type AuditSeverity = (typeof AuditSeverity)[keyof typeof AuditSeverity];

export const AUDIT_SEVERITY_LABELS: Record<AuditSeverity, string> = {
  None: "—",
  Trace: "Rastreio",
  Debug: "Depuração",
  Information: "Info",
  Warning: "Alerta",
  Error: "Erro",
  Critical: "Crítico",
};

/** Ordinal rank for severities — string severities are not ordered, so use
 *  this for any threshold (`<`/`>`/`>=`/`<=`) comparison. */
const SEVERITY_RANK: Record<AuditSeverity, number> = {
  None: 0,
  Trace: 1,
  Debug: 2,
  Information: 3,
  Warning: 4,
  Error: 5,
  Critical: 6,
};

export function severityRank(severity: AuditSeverity): number {
  return SEVERITY_RANK[severity] ?? 0;
}

/** Bitwise flags — keep in sync with AuditTag enum on the backend. */
export const AuditTag = {
  None: 0,
  PiiMasked: 1 << 0,
  OutOfQuota: 1 << 1,
  Sampled: 1 << 2,
  RetainedLong: 1 << 3,
  HealthCheck: 1 << 4,
  Authentication: 1 << 5,
  Authorization: 1 << 6,
} as const;
export type AuditTag = (typeof AuditTag)[keyof typeof AuditTag];

export const AUDIT_TAG_LABELS: Array<{ flag: number; name: string }> = [
  { flag: AuditTag.PiiMasked, name: "PII mascarado" },
  { flag: AuditTag.OutOfQuota, name: "Cota excedida" },
  { flag: AuditTag.Sampled, name: "Amostrado" },
  { flag: AuditTag.RetainedLong, name: "Retido" },
  { flag: AuditTag.HealthCheck, name: "Verificação de saúde" },
  { flag: AuditTag.Authentication, name: "Autenticação" },
  { flag: AuditTag.Authorization, name: "Autorização" },
];

export function decodeTags(mask: number): string[] {
  return AUDIT_TAG_LABELS.filter((t) => (mask & t.flag) !== 0).map((t) => t.name);
}

// ────────────────────────────────────────────────────────────────────────
// DTOs
// ────────────────────────────────────────────────────────────────────────

export type AuditSummaryDto = {
  id: string;
  occurredAtUtc: string;
  eventType: AuditEventType;
  severity: AuditSeverity;
  tenantId?: string | null;
  userId?: string | null;
  userName?: string | null;
  traceId?: string | null;
  correlationId?: string | null;
  requestId?: string | null;
  source?: string | null;
  tags: number;
};

export type AuditDetailDto = AuditSummaryDto & {
  receivedAtUtc: string;
  spanId?: string | null;
  /** Server returns a JsonElement — comes through as already-parsed JSON. */
  payload: unknown;
};

export type AuditSummaryAggregateDto = {
  /** keys are AuditEventType / AuditSeverity string names */
  eventsByType: Record<string, number>;
  eventsBySeverity: Record<string, number>;
  eventsBySource: Record<string, number>;
  eventsByTenant: Record<string, number>;
};

export type PagedResponse<T> = {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
};

// ────────────────────────────────────────────────────────────────────────
// Query shape — matches GetAuditsQuery on the backend.
// ────────────────────────────────────────────────────────────────────────

export type ListAuditsQuery = {
  pageNumber?: number;
  pageSize?: number;
  fromUtc?: string;
  toUtc?: string;
  tenantId?: string;
  userId?: string;
  eventType?: AuditEventType;
  /** Hide a single event type (e.g. "Activity" to drop system-level HTTP noise). */
  excludeEventType?: AuditEventType;
  severity?: AuditSeverity;
  /** Bitmask of AuditTag values. */
  tags?: number;
  source?: string;
  correlationId?: string;
  traceId?: string;
  search?: string;
  sort?: string;
};

function toQueryString(query: Record<string, unknown>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === "") continue;
    // FluentValidation on the backend uses PascalCase parameter names by
    // default for [AsParameters]; ASP.NET Core's binder is case-insensitive,
    // so we send what's natural for JS callers.
    params.append(toPascal(key), String(value));
  }
  return params.toString();
}

function toPascal(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

// ────────────────────────────────────────────────────────────────────────
// Human-readable summaries — turn the terse `source` (e.g. "api.identity.ListSessions"
// or "IdentityDbContext") into a pt-BR predicate so rows read like an
// activity feed ("Mukesh · consultou sessions") without opening the details.
// The list DTO carries no field-level before/after, so entity changes summarize
// at the record level; the detail drawer still shows the full payload.
// ────────────────────────────────────────────────────────────────────────

// Past-tense pt-BR verb (third person) for the leading word of a PascalCase action name.
const ACTION_VERBS: Record<string, string> = {
  list: "consultou", get: "consultou", fetch: "consultou", read: "consultou", search: "buscou", export: "exportou",
  create: "criou", add: "adicionou", register: "cadastrou", invite: "convidou", upload: "enviou", generate: "gerou",
  update: "atualizou", edit: "atualizou", set: "atualizou", change: "alterou", adjust: "ajustou", rename: "renomeou", reorder: "reordenou", renew: "renovou",
  delete: "excluiu", remove: "removeu", revoke: "revogou", void: "anulou", purge: "expurgou",
  assign: "atribuiu", toggle: "alternou", mark: "marcou", confirm: "confirmou", resend: "reenviou",
  issue: "emitiu", refresh: "renovou", enroll: "ativou", disable: "desativou", enable: "ativou",
  restore: "restaurou", pin: "fixou", unpin: "desafixou", join: "entrou em", leave: "saiu de", react: "reagiu a",
  impersonate: "acessou como", start: "iniciou", end: "encerrou", download: "baixou", capture: "capturou",
};

// Whole-action phrases that don't decompose into a clean verb + object.
const ACTION_OVERRIDES: Record<string, string> = {
  IssueJwtToken: "entrou no sistema",
  RefreshToken: "renovou a sessão",
  SseToken: "conectou à transmissão ao vivo",
  GetUnreadNotificationCount: "consultou as notificações",
};

function splitPascal(value: string): string[] {
  return value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(/[\s._-]+/)
    .filter(Boolean);
}

function humanizeAction(action: string, area?: string): string {
  if (ACTION_OVERRIDES[action]) return ACTION_OVERRIDES[action];
  const words = splitPascal(action);
  if (words.length === 0) return area ? `acessou ${area}` : "executou uma ação";
  const verb = ACTION_VERBS[words[0].toLowerCase()];
  const object = words
    .slice(verb ? 1 : 0)
    .filter((w) => w.toLowerCase() !== "my")
    .join(" ")
    .toLowerCase();
  // An action outside the known verbs (e.g. the PAF's own "EmitirFicha") keeps its words after "executou".
  if (!verb) return object ? `executou ${object}` : "executou uma ação";
  const target = object || area || "";
  return target ? `${verb} ${target}` : verb;
}

/** pt-BR predicate for an audit row — pairs with the actor shown alongside
 *  it: `${actor} ${auditPredicate(row)}` → "Mukesh entrou no sistema". */
export function auditPredicate(row: Pick<AuditSummaryDto, "eventType" | "source">): string {
  const src = (row.source ?? "").trim();

  if (row.eventType === AuditEventType.Exception) {
    const parts = src.replace(/^api\./i, "").split(".").filter(Boolean);
    const where = parts.length ? splitPascal(parts[parts.length - 1]).join(" ").toLowerCase() : "";
    return where ? `teve um erro em ${where}` : "teve um erro";
  }

  if (row.eventType === AuditEventType.EntityChange) {
    const area = src.replace(/DbContext$/i, "").trim();
    const label = area ? splitPascal(area).join(" ").toLowerCase() : "";
    return label ? `alterou registros de ${label}` : "alterou registros";
  }

  // Activity / Security — "api.<area>.<Action>" (or a single segment).
  const parts = src.replace(/^api\./i, "").split(".").filter(Boolean);
  if (parts.length === 0) return "executou uma ação";
  if (parts.length === 1) return `acessou ${splitPascal(parts[0]).join(" ").toLowerCase()}`;
  const action = parts[parts.length - 1];
  const area = parts[0];
  return humanizeAction(action, area);
}

// ────────────────────────────────────────────────────────────────────────
// Fetchers
// ────────────────────────────────────────────────────────────────────────

export async function listAudits(
  query: ListAuditsQuery,
  signal?: AbortSignal,
): Promise<PagedResponse<AuditSummaryDto>> {
  const qs = toQueryString(query);
  return apiFetch<PagedResponse<AuditSummaryDto>>(
    `/api/v1/audits${qs ? `?${qs}` : ""}`,
    { signal },
  );
}

export async function getAuditById(
  id: string,
  signal?: AbortSignal,
): Promise<AuditDetailDto> {
  return apiFetch<AuditDetailDto>(`/api/v1/audits/${id}`, { signal });
}

export async function getAuditSummary(
  query: { fromUtc?: string; toUtc?: string; tenantId?: string },
  signal?: AbortSignal,
): Promise<AuditSummaryAggregateDto> {
  const qs = toQueryString(query);
  return apiFetch<AuditSummaryAggregateDto>(
    `/api/v1/audits/summary${qs ? `?${qs}` : ""}`,
    { signal },
  );
}

export async function getAuditsByCorrelation(
  correlationId: string,
  query: { fromUtc?: string; toUtc?: string } = {},
  signal?: AbortSignal,
): Promise<AuditSummaryDto[]> {
  const qs = toQueryString(query);
  return apiFetch<AuditSummaryDto[]>(
    `/api/v1/audits/by-correlation/${encodeURIComponent(correlationId)}${qs ? `?${qs}` : ""}`,
    { signal },
  );
}

