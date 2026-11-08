import { useMemo } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import type { ParseKeys } from "i18next";
import {
  Activity,
  ArrowRight,
  ArrowUpRight,
  ChevronRight,
  Lock,
  ScrollText,
  Server,
  ShieldCheck,
  Sparkles,
  Wifi,
  WifiOff,
  Zap,
} from "lucide-react";
import {
  AuditEventType,
  AuditSeverity,
  AUDIT_EVENT_TYPE_LABELS,
  severityRank,
  listAudits,
  type AuditSummaryDto,
} from "@/api/audits";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { EntityDetailSection } from "@/components/list";
import { useAuth } from "@/auth/use-auth";
import { useSseEvents, useSseStatus, type SseEvent, type SseStatus } from "@/sse/sse-context";
import { cn } from "@/lib/cn";
import { SAO_PAULO } from "@/lib/list-helpers";

// ────────────────────────────────────────────────────────────────────────
// Shaping helpers — pure, tested via memoization at the call sites.
// ────────────────────────────────────────────────────────────────────────

const numberFmt = new Intl.NumberFormat("pt-BR");
const formatNumber = (n: number) => numberFmt.format(n);

/**
 * Time-of-day greeting. Three buckets — morning (<12), afternoon (<17),
 * evening (rest). Mirrors the dentalOS dashboard greeting helper so the
 * tone of voice matches across products.
 */
function greetingKey(): "greeting.morning" | "greeting.afternoon" | "greeting.evening" {
  const hour = new Date().getHours();
  if (hour < 12) return "greeting.morning";
  if (hour < 17) return "greeting.afternoon";
  return "greeting.evening";
}

function relativeTime(iso: string, now: number = Date.now()): string {
  const delta = Math.max(0, Math.floor((now - Date.parse(iso)) / 1000));
  if (delta < 60) return `${delta}s`;
  const m = Math.floor(delta / 60);
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} h`;
  return `${Math.floor(h / 24)} d`;
}

const timeFmt = new Intl.DateTimeFormat("pt-BR", {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hourCycle: "h23",
  timeZone: SAO_PAULO,
});
function formatClock(ts: number) {
  return timeFmt.format(new Date(ts));
}

function eventTone(type: string): "default" | "success" | "warning" | "danger" | "brand" {
  const t = type.toLowerCase();
  if (t.includes("fail") || t.includes("error") || t.includes("revoke")) return "danger";
  if (t.includes("warn") || t.includes("retry")) return "warning";
  if (t.includes("login") || t.includes("issued") || t.includes("created")) return "success";
  if (t.includes("token") || t.includes("auth")) return "brand";
  return "default";
}

// ────────────────────────────────────────────────────────────────────────
// StatCard — flat, calm. Icon tile on the left, label + big
// tabular-nums value stacked on the right. Tone tints the icon plate.
// ────────────────────────────────────────────────────────────────────────

type StatTone = "primary" | "success" | "warning" | "danger" | "info";

const STAT_TONE_BG: Record<StatTone, string> = {
  primary: "bg-[oklch(from_var(--color-primary)_l_c_h_/_0.10)] text-[var(--color-primary)]",
  success: "bg-[oklch(from_var(--color-success)_l_c_h_/_0.10)] text-[var(--color-success)]",
  warning: "bg-[oklch(from_var(--color-warning)_l_c_h_/_0.12)] text-[var(--color-warning)]",
  danger: "bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.10)] text-[var(--color-destructive)]",
  info: "bg-[oklch(from_var(--color-info)_l_c_h_/_0.10)] text-[var(--color-info)]",
};

function StatCard({
  index,
  label,
  value,
  sublabel,
  icon: Icon,
  tone,
  href,
}: {
  index: number;
  label: string;
  value: React.ReactNode;
  sublabel?: React.ReactNode;
  icon: React.ComponentType<{ className?: string }>;
  tone: StatTone;
  href?: string;
}) {
  const body = (
    <div
      className={cn(
        "fsh-enter group/stat flex h-full items-start gap-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] px-4 py-3.5 shadow-xs",
        "transition-colors duration-200 hover:border-[var(--color-border-strong)]",
      )}
      style={{ animationDelay: `${index * 50}ms` }}
    >
      <span
        aria-hidden
        className={cn(
          "grid size-9 shrink-0 place-items-center rounded-lg",
          STAT_TONE_BG[tone],
        )}
      >
        <Icon className="size-4" />
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          {label}
        </p>
        <p className="mt-1 font-display text-[20px] font-bold leading-none tracking-tight tabular-nums text-foreground sm:text-[22px]">
          {value}
        </p>
        {sublabel && (
          <p className="mt-1.5 truncate text-[11px] text-muted-foreground">
            {sublabel}
          </p>
        )}
      </div>
      {href && (
        <ArrowUpRight
          aria-hidden
          className="size-3.5 shrink-0 text-muted-foreground opacity-0 transition-opacity group-hover/stat:opacity-100"
        />
      )}
    </div>
  );
  return href ? (
    <Link to={href} className="block">
      {body}
    </Link>
  ) : (
    body
  );
}

// ────────────────────────────────────────────────────────────────────────
// System status — SSE pulse + event count + connection state.
// ────────────────────────────────────────────────────────────────────────

function SystemStatusBody({
  sseStatus,
  eventCount,
}: {
  sseStatus: SseStatus;
  eventCount: number;
}) {
  const { t } = useTranslation("overview");
  const live = sseStatus === "connected";
  const errored = sseStatus === "error";
  const Icon = live ? Wifi : WifiOff;
  const tone: "success" | "danger" | "default" = live ? "success" : errored ? "danger" : "default";

  const toneBg =
    tone === "success"
      ? "bg-[oklch(from_var(--color-success)_l_c_h_/_0.10)] text-[var(--color-success)]"
      : tone === "danger"
        ? "bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.10)] text-[var(--color-destructive)]"
        : "bg-[var(--color-muted)] text-muted-foreground";

  return (
    <div className="space-y-3">
      <div className="flex items-start gap-3">
        <span
          aria-hidden
          className={cn(
            "relative grid size-9 shrink-0 place-items-center rounded-lg",
            toneBg,
          )}
        >
          <Icon className="size-4" />
          {live && (
            <span
              aria-hidden
              className="pulse-dot absolute -right-0.5 -top-0.5 size-2 rounded-full"
              style={{
                backgroundColor: "var(--color-success)",
                color: "var(--color-success)",
              }}
            />
          )}
        </span>
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="text-[13px] font-semibold capitalize tracking-tight text-foreground">
              {live ? t("system.streamLive") : t(`connState.${sseStatus}`)}
            </span>
            {live && <Badge variant="success">SSE</Badge>}
            {errored && <Badge variant="danger">{t("system.offline")}</Badge>}
          </div>
          <p className="mt-0.5 text-[11.5px] text-muted-foreground">
            {live
              ? t("system.liveDesc")
              : errored
                ? t("system.erroredDesc")
                : t("system.waitingDesc")}
          </p>
        </div>
      </div>

      <div className="flex items-center justify-between border-t border-[oklch(from_var(--color-border)_l_c_h_/_0.5)] pt-3">
        <span className="text-[11px] uppercase tracking-wider text-muted-foreground">
          {t("system.eventsThisSession")}
        </span>
        <span className="font-display text-[16px] font-bold tabular-nums text-foreground">
          {formatNumber(eventCount)}
        </span>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Recent audits — top 5 of the last 24h. Severity stripe, type icon,
// actor + relative timestamp. Click row to deep-link into the trail.
// ────────────────────────────────────────────────────────────────────────

function recentSeverityColor(severity: AuditSeverity): string {
  const rank = severityRank(severity);
  if (rank >= severityRank(AuditSeverity.Error)) return "var(--color-destructive)";
  if (rank >= severityRank(AuditSeverity.Warning)) return "var(--color-warning)";
  if (rank >= severityRank(AuditSeverity.Information)) return "var(--color-info)";
  return "var(--color-muted-foreground)";
}

function recentEventTypeIcon(eventType: AuditEventType): React.ComponentType<{ className?: string }> {
  if (eventType === AuditEventType.Security) return ShieldCheck;
  if (eventType === AuditEventType.Exception) return Activity;
  if (eventType === AuditEventType.EntityChange) return Server;
  return Activity;
}

/** Mirrors the permission GET /api/v1/audits enforces server-side. */
const AUDIT_VIEW_PERMISSION = "Permissions.AuditTrails.View";

function RecentAuditsSkeleton() {
  return (
    <ul className="space-y-2.5">
      {[0, 1, 2, 3, 4].map((i) => (
        <li key={i} className="flex items-center gap-3">
          <Skeleton className="size-7 rounded-md" />
          <Skeleton className="h-3 w-32" />
          <Skeleton className="ml-auto h-3 w-16" />
        </li>
      ))}
    </ul>
  );
}

function RecentAuditsNoAccess() {
  const { t } = useTranslation("overview");
  return (
    <div className="flex flex-col items-center gap-2 py-6 text-center">
      <Lock className="size-4 text-muted-foreground" />
      <div className="text-[13px] font-semibold tracking-tight text-foreground">
        {t("audits.noAccessTitle")}
      </div>
      <p className="max-w-sm text-[11.5px] text-muted-foreground">
        {t("audits.noAccessBody")}
      </p>
    </div>
  );
}

function RecentAuditsBody() {
  const { t } = useTranslation("overview");
  const recentAudits = useQuery({
    queryKey: ["audits", "recent", "overview"],
    queryFn: ({ signal }) => {
      // 24h window, page size 5 — matches the visual capacity below.
      const to = new Date();
      const from = new Date(to.getTime() - 24 * 60 * 60 * 1000);
      return listAudits(
        { pageNumber: 1, pageSize: 5, fromUtc: from.toISOString(), toUtc: to.toISOString() },
        signal,
      );
    },
    staleTime: 30_000,
  });

  const items = recentAudits.data?.items ?? [];

  if (recentAudits.isLoading) {
    return <RecentAuditsSkeleton />;
  }

  if (items.length === 0) {
    return (
      <div className="flex flex-col items-center gap-2 py-6 text-center">
        <ScrollText className="size-4 text-muted-foreground" />
        <div className="text-[13px] font-semibold tracking-tight text-foreground">
          {t("audits.emptyTitle")}
        </div>
        <p className="max-w-sm text-[11.5px] text-muted-foreground">
          {t("audits.emptyBody")}
        </p>
      </div>
    );
  }

  return (
    <ul className="-my-1 divide-y divide-[oklch(from_var(--color-border)_l_c_h_/_0.5)]">
      {items.map((row) => (
        <RecentAuditRow key={row.id} row={row} />
      ))}
    </ul>
  );
}

function RecentAuditRow({ row }: { row: AuditSummaryDto }) {
  const { t } = useTranslation("overview");
  const Icon = recentEventTypeIcon(row.eventType);
  const tone = recentSeverityColor(row.severity);
  return (
    <li>
      <Link
        to="/system/audits"
        className="group/row -mx-1 flex items-center gap-3 rounded-md px-1 py-2.5 transition-colors hover:bg-[var(--color-accent)]"
      >
        <span
          aria-hidden
          className="grid size-7 shrink-0 place-items-center rounded-md"
          style={{
            color: tone,
            background: `oklch(from ${tone} l c h / 0.10)`,
          }}
        >
          <Icon className="size-3.5" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[12.5px] font-medium tracking-tight text-foreground">
            {row.source ?? AUDIT_EVENT_TYPE_LABELS[row.eventType] ?? t("audits.eventFallback")}
          </div>
          <div className="mt-0.5 flex items-center gap-1.5 text-[11px] text-muted-foreground">
            <span className="truncate">
              {row.userName ?? row.userId?.slice(0, 8) ?? t("audits.systemActor")}
            </span>
            <span aria-hidden>·</span>
            <span className="tabular-nums">{t("audits.ago", { value: relativeTime(row.occurredAtUtc) })}</span>
          </div>
        </div>
        <ChevronRight className="size-3.5 shrink-0 text-muted-foreground transition-transform group-hover/row:translate-x-0.5" />
      </Link>
    </li>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Quick actions — shortcut tiles to the most useful destinations.
// Subscription is not part of the PAF product (31/10/2026), nor the kit's
// Users screen (02/11/2026, the client invites users on /paf/usuarios); their
// routes redirect home, so they have no tile.
// ────────────────────────────────────────────────────────────────────────

type QuickAction = {
  to: string;
  titleKey: ParseKeys<"overview">;
  descriptionKey: ParseKeys<"overview">;
  icon: React.ComponentType<{ className?: string }>;
  tone: StatTone;
};

const QUICK_ACTIONS: QuickAction[] = [
  {
    to: "/activity",
    titleKey: "quick.liveActivity.title",
    descriptionKey: "quick.liveActivity.description",
    icon: Activity,
    tone: "warning",
  },
];

// The card sits in one cell of a 2-up grid beside a 360px rail, so its width
// tracks that column, not the viewport: go two-up only when the card itself has
// room for two 14rem tiles, instead of squeezing two into a sliver.
function QuickActionsBody() {
  const { t } = useTranslation("overview");
  return (
    <div className="@container">
      <div className="grid grid-cols-1 gap-2 @[28.5rem]:grid-cols-2">
        {QUICK_ACTIONS.map((a) => (
          <Link
            key={a.to}
            to={a.to}
            className={cn(
              "group/qa flex items-start gap-2.5 rounded-lg border border-[var(--color-border)] bg-[var(--color-card)] p-3",
              "transition-colors duration-200 hover:border-[var(--color-border-strong)] hover:bg-[var(--color-accent)]",
            )}
          >
            <span
              aria-hidden
              className={cn(
                "grid size-8 shrink-0 place-items-center rounded-md",
                STAT_TONE_BG[a.tone],
              )}
            >
              <a.icon className="size-3.5" />
            </span>
            <div className="min-w-0 flex-1">
              <div className="text-[12.5px] font-semibold tracking-tight text-foreground">
                {t(a.titleKey)}
              </div>
              <p className="mt-0.5 text-[11px] leading-snug text-muted-foreground">
                {t(a.descriptionKey)}
              </p>
            </div>
            <ArrowRight className="size-3 shrink-0 text-muted-foreground opacity-0 transition-all group-hover/qa:translate-x-0.5 group-hover/qa:opacity-100" />
          </Link>
        ))}
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Live feed body — slim version of the LiveFeed widget that drops the
// outer Card chrome so it can be embedded inside an EntityDetailSection.
// ────────────────────────────────────────────────────────────────────────

function LiveFeedBody({ events }: { events: SseEvent[] }) {
  const { t } = useTranslation("overview");
  const visible = useMemo(() => events.slice(0, 5), [events]);

  if (visible.length === 0) {
    return (
      <div className="flex flex-col items-center gap-2 py-6 text-center">
        <Activity className="size-4 text-muted-foreground" />
        <div className="text-[13px] font-semibold tracking-tight text-foreground">
          {t("liveFeed.emptyTitle")}
        </div>
        <p className="max-w-sm text-[11.5px] text-muted-foreground">
          {t("liveFeed.emptyBody")}
        </p>
      </div>
    );
  }

  return (
    <ul className="-my-1 divide-y divide-[oklch(from_var(--color-border)_l_c_h_/_0.5)]">
      {visible.map((ev) => (
        <li key={ev.id} className="flex items-center gap-3 py-2.5">
          <span className="shrink-0 font-mono text-[10.5px] tabular-nums text-muted-foreground">
            {formatClock(ev.receivedAt)}
          </span>
          <Badge variant={eventTone(ev.type)}>{ev.type}</Badge>
        </li>
      ))}
    </ul>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Page
// ────────────────────────────────────────────────────────────────────────

export function OverviewPage() {
  const { t } = useTranslation("overview");
  const { user, permissionsHydrated } = useAuth();
  // GET /audits requires AuditTrails.View — only mount the query for users who
  // hold it, so everyone else gets a quiet no-access state instead of a 403.
  const canViewAudits =
    permissionsHydrated && (user?.permissions.includes(AUDIT_VIEW_PERMISSION) ?? false);
  const { status: sseStatus, eventCount } = useSseStatus();
  const { events } = useSseEvents();

  // ── Header strings ────────────────────────────────────────────────────
  const now = new Date();
  const dateCaption = now.toLocaleDateString("pt-BR", {
    timeZone: SAO_PAULO,
    weekday: "long",
    day: "2-digit",
    month: "long",
    year: "numeric",
  });
  const firstName = (user?.name ?? user?.email?.split("@")[0] ?? t("operator"))
    .toString()
    .split(" ")[0];
  // No tenant-status query on the PAF overview (it went with Billing), so the JWT identifier names the tenant.
  const tenantLabel = user?.tenant ?? t("yourTenant");

  // No Billing here (plan, validity, usage, subscription, first-run "pick a plan"): the PAF does not use it and
  // tenant roles lose Permissions.Billing.View (31/10/2026), so those calls would only 403. The global
  // ExpiryBanner still reports tenant validity.
  return (
    <div className="space-y-5">
      {/* ── Editorial greeting header ───────────────────────────────────
          Direct text — no card chrome. Small caption above (date + tenant),
          big greeting below, action buttons on the right. */}
      <header className="fsh-enter flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
        <div className="min-w-0">
          <p className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
            {dateCaption} · {tenantLabel}
          </p>
          <h1 className="mt-1 font-display text-display-page font-bold leading-tight tracking-tight text-foreground">
            {t(greetingKey(), { name: firstName })}
          </h1>
        </div>
        <div className="flex shrink-0 flex-wrap items-center gap-2">
          <Button asChild variant="outline" size="sm">
            <Link to="/activity">
              <Activity className="mr-1.5 size-3.5" />
              {t("viewActivity")}
            </Link>
          </Button>
          {canViewAudits && (
            <Button asChild variant="outline" size="sm">
              <Link to="/system/audits">
                <ScrollText className="mr-1.5 size-3.5" />
                {t("viewAudits")}
              </Link>
            </Button>
          )}
        </div>
      </header>

      {/* ── Stats row ───────────────────────────────────────────────── */}
      <div className="grid grid-cols-2 gap-2.5 md:grid-cols-4">
        <StatCard
          index={0}
          tone="info"
          icon={Zap}
          label={t("stat.liveEvents")}
          value={<span className="tabular-nums">{formatNumber(eventCount)}</span>}
          sublabel={
            <span className="inline-flex items-center gap-1.5">
              <span
                aria-hidden
                className={cn(
                  "inline-block size-1.5 shrink-0 rounded-full",
                  // Contained opacity pulse — the design-system .pulse-dot halo
                  // (an overflowing ::before ring) gets clipped by this sublabel's
                  // `truncate` (overflow-hidden), so it can't be used here.
                  sseStatus === "connected" && "animate-pulse",
                )}
                style={{
                  backgroundColor:
                    sseStatus === "connected"
                      ? "var(--color-success)"
                      : sseStatus === "error"
                        ? "var(--color-destructive)"
                        : "var(--color-muted-foreground)",
                }}
              />
              <span className="capitalize">{t(`connState.${sseStatus}`)}</span>
            </span>
          }
          href="/activity"
        />
      </div>

      {/* ── Multi-column widget grid ────────────────────────────────────
          Left rail (360px) holds the system status. The right side fills
          with a 2-up grid of secondary widgets that all read at the same
          density. */}
      <div className="flex flex-col gap-4 lg:flex-row">
        {/* Left rail */}
        <aside className="w-full space-y-4 lg:w-[360px] lg:shrink-0">
          <EntityDetailSection title={t("section.systemStatus")} icon={Wifi}>
            <SystemStatusBody sseStatus={sseStatus} eventCount={eventCount} />
          </EntityDetailSection>
        </aside>

        {/* Right column — 2-up widget grid */}
        <div className="grid w-full min-w-0 flex-1 grid-cols-1 gap-4 md:grid-cols-2">
          <EntityDetailSection
            title={t("section.recentAudits")}
            icon={ScrollText}
            description={t("section.recentAuditsDesc")}
            action={
              canViewAudits ? (
                <Link
                  to="/system/audits"
                  className="inline-flex items-center gap-1 text-[11px] font-medium text-muted-foreground transition-colors hover:text-foreground"
                >
                  {t("seeAll")} <ArrowUpRight className="size-3" />
                </Link>
              ) : undefined
            }
          >
            {!permissionsHydrated ? (
              <RecentAuditsSkeleton />
            ) : canViewAudits ? (
              <RecentAuditsBody />
            ) : (
              <RecentAuditsNoAccess />
            )}
          </EntityDetailSection>

          <EntityDetailSection
            title={t("section.quickActions")}
            icon={Sparkles}
            description={t("section.quickActionsDesc")}
          >
            <QuickActionsBody />
          </EntityDetailSection>

          <EntityDetailSection
            title={t("section.liveFeed")}
            icon={Activity}
            description={t("section.liveFeedDesc")}
            action={
              <Link
                to="/activity"
                className="inline-flex items-center gap-1 text-[11px] font-medium text-muted-foreground transition-colors hover:text-foreground"
              >
                {t("open")} <ArrowUpRight className="size-3" />
              </Link>
            }
          >
            <LiveFeedBody events={events} />
          </EntityDetailSection>
        </div>
      </div>
    </div>
  );
}
