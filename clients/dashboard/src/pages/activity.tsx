import { useMemo } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Activity, History, Inbox, Radio, RefreshCw, ScrollText, type LucideIcon } from "lucide-react";
import { useSseEvents, useSseStatus, type SseEvent } from "@/sse/sse-context";
import { SSE_STATUS_LABEL } from "@/sse/sse-status-label";
import { useAuth } from "@/auth/use-auth";
import {
  AUDIT_EVENT_TYPE_LABELS,
  AUDIT_SEVERITY_LABELS,
  AuditEventType,
  AuditSeverity,
  auditPredicate,
  listAudits,
  severityRank,
  type AuditSummaryDto,
} from "@/api/audits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  EntityEmpty,
  EntityListCard,
  EntityListHeader,
  EntityListLoading,
  EntityListRow,
  EntityPageHeader,
  EntityStatusBadge,
  ErrorBand,
  type EntityStatusTone,
} from "@/components/list";
import { describe, formatDateTime, formatRelative, SAO_PAULO } from "@/lib/list-helpers";

// Live SSE events exist only in memory (SseProvider) — by design, so nothing
// from one session can leak into the next after a logout or tenant switch.
// What survives a refresh is the persisted audit trail, so users who may read
// it get recent history loaded once from GET /audits. The two lists are
// rendered as separate sections, never merged: SSE events and audit records
// have unrelated ids and shapes, so any merge would duplicate or mis-order.

/** Mirrors the permission GET /api/v1/audits enforces server-side. */
const AUDIT_VIEW_PERMISSION = "Permissions.AuditTrails.View";
const HISTORY_PAGE_SIZE = 25;
const MAX_LIVE_EVENTS = 200;

const timeFmt = new Intl.DateTimeFormat("pt-BR", {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hourCycle: "h23",
  timeZone: SAO_PAULO,
});

function formatTime(ts: number) {
  return timeFmt.format(new Date(ts));
}

function payloadSummary(data: unknown, raw: string): string {
  if (typeof data === "string") return data;
  if (data && typeof data === "object") {
    try {
      return JSON.stringify(data);
    } catch {
      return raw;
    }
  }
  return raw;
}

// Map an event type to a status badge tone — failures pop red, successes
// green, warnings amber, everything else neutral. Mirrors the heuristic
// the legacy live-feed component used.
function eventTone(type: string): EntityStatusTone {
  const t = type.toLowerCase();
  if (t.includes("fail") || t.includes("error") || t.includes("revoke")) return "danger";
  if (t.includes("warn") || t.includes("retry")) return "warning";
  if (t.includes("login") || t.includes("issued") || t.includes("created")) return "success";
  if (t.includes("token") || t.includes("auth")) return "info";
  return "default";
}

// Try to extract a friendlier "entity" label from the event payload —
// most domain events carry an aggregate id under a predictable field.
function entityLabel(data: unknown): string {
  if (data && typeof data === "object") {
    const obj = data as Record<string, unknown>;
    for (const key of ["entityId", "aggregateId", "id", "tenantId", "userId"]) {
      const v = obj[key];
      if (typeof v === "string" && v.length > 0) return v;
    }
  }
  return "—";
}

function severityTone(severity: AuditSeverity): EntityStatusTone {
  const rank = severityRank(severity);
  if (rank >= severityRank(AuditSeverity.Error)) return "danger";
  if (rank >= severityRank(AuditSeverity.Warning)) return "warning";
  if (rank >= severityRank(AuditSeverity.Information)) return "info";
  return "default";
}

function auditActor(row: AuditSummaryDto): string {
  return row.userName ?? (row.userId ? `${row.userId.slice(0, 8)}…` : "Sistema");
}

function formatAuditTime(iso: string) {
  return Number.isNaN(new Date(iso).getTime()) ? "—" : formatDateTime(iso);
}

// ───────────────────────────────────────────────────────────────────────
//  Page
// ───────────────────────────────────────────────────────────────────────

const LIVE_GRID = "grid-cols-[1fr_240px_120px]";
const HISTORY_GRID = "grid-cols-[1fr_200px_140px]";

export function ActivityPage() {
  const { status, eventCount } = useSseStatus();
  const { events } = useSseEvents();
  const { user, permissionsHydrated } = useAuth();

  const canViewHistory =
    permissionsHydrated && (user?.permissions.includes(AUDIT_VIEW_PERMISSION) ?? false);

  const liveItems = useMemo(() => events.slice(0, MAX_LIVE_EVENTS), [events]);
  const isLive = status === "connected";

  return (
    <div className="space-y-6 sm:space-y-8">
      <EntityPageHeader
        icon={Activity}
        title="Atividade ao vivo"
        total={eventCount}
        unit="evento"
        units="eventos"
        description={
          canViewHistory
            ? "Eventos recebidos por Server-Sent Events enquanto esta aba está conectada, acima do histórico recente de auditoria da empresa."
            : "Eventos recebidos da API por Server-Sent Events enquanto esta aba está conectada."
        }
      >
        {isLive ? (
          <Badge variant="success">ao vivo</Badge>
        ) : status === "error" ? (
          <Badge variant="danger">fora do ar</Badge>
        ) : (
          <Badge variant="default">{SSE_STATUS_LABEL[status]}</Badge>
        )}
      </EntityPageHeader>

      <LiveSection items={liveItems} isLive={isLive} eventCount={eventCount} />

      {!permissionsHydrated ? (
        <EntityListLoading rows={3} desktopColumns={HISTORY_GRID} />
      ) : canViewHistory ? (
        <HistorySection />
      ) : (
        <LiveOnlyNotice />
      )}
    </div>
  );
}

function SectionHeading({
  id,
  icon: Icon,
  title,
  caption,
  children,
}: {
  id: string;
  icon: LucideIcon;
  title: string;
  caption: string;
  children?: React.ReactNode;
}) {
  return (
    <div className="mb-3 flex flex-wrap items-end justify-between gap-2">
      <div className="min-w-0">
        <h2 id={id} className="flex items-center gap-2 font-display text-[15px] font-semibold tracking-tight text-[var(--color-foreground)]">
          <Icon className="size-4 text-[var(--color-muted-foreground)]" />
          {title}
        </h2>
        <p className="mt-0.5 text-[12px] text-[var(--color-muted-foreground)]">{caption}</p>
      </div>
      {children && <div className="flex items-center gap-2">{children}</div>}
    </div>
  );
}

// ───────────────────────────────────────────────────────────────────────
//  Live — SSE events received since this tab connected. In-memory only.
// ───────────────────────────────────────────────────────────────────────

function LiveSection({
  items,
  isLive,
  eventCount,
}: {
  items: SseEvent[];
  isLive: boolean;
  eventCount: number;
}) {
  return (
    <section aria-labelledby="activity-live-heading">
      <SectionHeading
        id="activity-live-heading"
        icon={Radio}
        title="Ao vivo"
        caption="Recebidos desde que esta aba conectou. Somem ao recarregar a página."
      >
        {items.length > 0 && (
          <p className="text-[12px] font-medium text-[var(--color-muted-foreground)]">
            {items.length} evento{items.length === 1 ? "" : "s"} na tela
            <span className="ml-2 opacity-60">
              · {new Intl.NumberFormat("pt-BR").format(eventCount)} no total
            </span>
          </p>
        )}
      </SectionHeading>

      {items.length === 0 ? (
        <EntityListCard>
          <div className="flex items-center gap-3 px-5 py-4">
            <Inbox className="size-4 shrink-0 text-[var(--color-muted-foreground)]" />
            <div className="min-w-0">
              <p className="text-[13px] font-semibold text-[var(--color-foreground)]">
                {isLive ? "Aguardando atividade" : "Nenhum evento ainda"}
              </p>
              <p className="text-[12px] text-[var(--color-muted-foreground)]">
                {isLive
                  ? "A transmissão está aberta. Os eventos vão aparecer aqui conforme o servidor publicar."
                  : "A transmissão de atividade não está conectada. Os eventos vão aparecer quando a conexão voltar."}
              </p>
            </div>
          </div>
        </EntityListCard>
      ) : (
        <>
          {/* Mobile: card list */}
          <div
            className="space-y-2 md:hidden"
            role="log"
            aria-live="polite"
            aria-relevant="additions"
            aria-label="Eventos da atividade ao vivo"
          >
            {items.map((ev) => (
              <LiveMobileCard key={ev.id} ev={ev} />
            ))}
          </div>

          {/* Desktop: table */}
          <EntityListCard
            className="hidden md:block"
            role="log"
            aria-live="polite"
            aria-relevant="additions"
            aria-label="Eventos da atividade ao vivo"
          >
            <EntityListHeader className={LIVE_GRID}>
              <span>Evento</span>
              <span>Entidade</span>
              <span className="text-right">Hora</span>
            </EntityListHeader>
            {items.map((ev, i) => (
              <LiveDesktopRow key={ev.id} ev={ev} isLast={i === items.length - 1} />
            ))}
          </EntityListCard>
        </>
      )}
    </section>
  );
}

// Mobile uses a static div (no navigation target — the activity feed is
// a stream of events, not a list of routable entities).
function LiveMobileCard({ ev }: { ev: SseEvent }) {
  return (
    <div className="rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4 shadow-xs">
      <div className="flex items-center justify-between gap-2">
        <EntityStatusBadge tone={eventTone(ev.type)}>{ev.type}</EntityStatusBadge>
        <span className="font-mono text-[11px] tabular-nums text-[var(--color-muted-foreground)]">
          {formatTime(ev.receivedAt)}
        </span>
      </div>
      <p className="mt-2 line-clamp-2 break-words font-mono text-[11.5px] leading-relaxed text-[var(--color-muted-foreground)]">
        {payloadSummary(ev.data, ev.rawData)}
      </p>
    </div>
  );
}

function LiveDesktopRow({ ev, isLast }: { ev: SseEvent; isLast: boolean }) {
  return (
    <EntityListRow className={LIVE_GRID} isLast={isLast}>
      <div className="flex min-w-0 items-center gap-2">
        <EntityStatusBadge tone={eventTone(ev.type)}>{ev.type}</EntityStatusBadge>
        <span className="truncate font-mono text-[11.5px] text-[var(--color-muted-foreground)]">
          {payloadSummary(ev.data, ev.rawData)}
        </span>
      </div>
      <code
        title={entityLabel(ev.data)}
        className="truncate font-mono text-[12px] text-[var(--color-muted-foreground)]"
      >
        {entityLabel(ev.data)}
      </code>
      <span className="text-right font-mono text-[11.5px] tabular-nums text-[var(--color-muted-foreground)]">
        {formatTime(ev.receivedAt)}
      </span>
    </EntityListRow>
  );
}

// ───────────────────────────────────────────────────────────────────────
//  History — the persisted audit trail, loaded once per visit (plus a
//  manual refresh). Deliberately NOT refetched per SSE event: a busy
//  tenant would otherwise hammer GET /audits.
// ───────────────────────────────────────────────────────────────────────

function HistorySection() {
  const history = useQuery({
    queryKey: ["audits", "activity-history"],
    queryFn: ({ signal }) =>
      listAudits(
        {
          pageNumber: 1,
          pageSize: HISTORY_PAGE_SIZE,
          // Same default as the Audit trail page: drop the per-request system
          // Activity firehose so the history reads as meaningful events.
          excludeEventType: AuditEventType.Activity,
        },
        signal,
      ),
    staleTime: 60_000,
  });

  const rows = history.data?.items ?? [];

  return (
    <section aria-labelledby="activity-history-heading">
      <SectionHeading
        id="activity-history-heading"
        icon={History}
        title="Histórico recente"
        caption={`Os ${HISTORY_PAGE_SIZE} eventos auditados mais recentes da empresa, sem a atividade de sistema de cada requisição.`}
      >
        <Button
          variant="outline"
          size="sm"
          onClick={() => void history.refetch()}
          disabled={history.isFetching}
          aria-label="Atualizar histórico"
        >
          <RefreshCw className={history.isFetching ? "animate-spin" : undefined} />
          Atualizar
        </Button>
        <Button asChild variant="ghost" size="sm">
          <Link to="/system/audits">
            <ScrollText />
            Trilha de auditoria
          </Link>
        </Button>
      </SectionHeading>

      {history.isError && (
        <div className="mb-3">
          <ErrorBand message={describe(history.error)} />
        </div>
      )}

      {history.isPending ? (
        <EntityListLoading rows={5} desktopColumns={HISTORY_GRID} />
      ) : rows.length === 0 ? (
        !history.isError && (
          <EntityEmpty
            icon={ScrollText}
            title="Nenhum histórico ainda"
            body="As alterações auditadas, as entradas no sistema e os erros desta empresa vão aparecer aqui."
          />
        )
      ) : (
        <>
          <div className="space-y-2 md:hidden" aria-label="Histórico recente">
            {rows.map((row) => (
              <HistoryMobileCard key={row.id} row={row} />
            ))}
          </div>

          <EntityListCard className="hidden md:block" aria-label="Histórico recente">
            <EntityListHeader className={HISTORY_GRID}>
              <span>Evento</span>
              <span>Severidade</span>
              <span className="text-right">Hora</span>
            </EntityListHeader>
            {rows.map((row, i) => (
              <HistoryDesktopRow key={row.id} row={row} isLast={i === rows.length - 1} />
            ))}
          </EntityListCard>
        </>
      )}
    </section>
  );
}

function HistoryMobileCard({ row }: { row: AuditSummaryDto }) {
  return (
    <div className="rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-4 shadow-xs">
      <div className="flex items-center justify-between gap-2">
        <EntityStatusBadge tone={severityTone(row.severity)}>
          {AUDIT_EVENT_TYPE_LABELS[row.eventType] ?? row.eventType}
        </EntityStatusBadge>
        <span
          title={formatAuditTime(row.occurredAtUtc)}
          className="font-mono text-[11px] tabular-nums text-[var(--color-muted-foreground)]"
        >
          {formatRelative(row.occurredAtUtc) || formatAuditTime(row.occurredAtUtc)}
        </span>
      </div>
      <p className="mt-2 text-[13px] text-[var(--color-foreground)]">
        <span className="font-semibold">{auditActor(row)}</span> {auditPredicate(row)}
      </p>
    </div>
  );
}

function HistoryDesktopRow({ row, isLast }: { row: AuditSummaryDto; isLast: boolean }) {
  return (
    <EntityListRow className={HISTORY_GRID} isLast={isLast}>
      <div className="flex min-w-0 items-center gap-2">
        <EntityStatusBadge tone={severityTone(row.severity)}>
          {AUDIT_EVENT_TYPE_LABELS[row.eventType] ?? row.eventType}
        </EntityStatusBadge>
        <p className="truncate text-[13px] text-[var(--color-foreground)]">
          <span className="font-semibold">{auditActor(row)}</span> {auditPredicate(row)}
        </p>
      </div>
      <span className="text-[12px] text-[var(--color-muted-foreground)]">
        {AUDIT_SEVERITY_LABELS[row.severity] ?? row.severity}
      </span>
      <span
        title={formatAuditTime(row.occurredAtUtc)}
        className="text-right font-mono text-[11.5px] tabular-nums text-[var(--color-muted-foreground)]"
      >
        {formatAuditTime(row.occurredAtUtc)}
      </span>
    </EntityListRow>
  );
}

// Users without Audit trail access never call GET /audits (it would 403).
// Be upfront that the page only shows what arrives while it's open.
function LiveOnlyNotice() {
  return (
    <div
      role="note"
      className="flex items-start gap-3 rounded-xl border border-dashed border-[var(--color-border)] px-5 py-4"
    >
      <History className="mt-0.5 size-4 shrink-0 text-[var(--color-muted-foreground)]" />
      <div className="min-w-0">
        <p className="text-[13px] font-semibold text-[var(--color-foreground)]">
          Só os eventos ao vivo
        </p>
        <p className="mt-0.5 text-[12px] text-[var(--color-muted-foreground)]">
          Esta lista mostra os eventos recebidos desde que esta aba conectou, então começa vazia
          depois de recarregar. A atividade passada fica na trilha de auditoria, que exige a
          permissão da Trilha de auditoria. Peça a um administrador se precisar.
        </p>
      </div>
    </div>
  );
}
