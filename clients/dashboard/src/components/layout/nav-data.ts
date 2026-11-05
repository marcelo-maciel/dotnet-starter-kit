import {
  Activity,
  HeartPulse,
  LayoutDashboard,
  ScrollText,
  Settings,
  Wifi,
} from "lucide-react";
// PAF (ADR 0008, menu entry amended 17/10/2026).
import { secaoMenuPaf } from "@/features/paf/navegacao/menu-paf";

export type NavSpec = {
  to: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  /**
   * Permission required to see this item. Items without a `perm` are visible to
   * every authenticated tenant user; gated items are hidden when the current
   * user (or impersonated user) lacks the permission, so they never land on a
   * page the API will reject with 403.
   */
  perm?: string;
  /**
   * Visible only if the user holds *at least one* of these permissions. Use for
   * an item that fronts several independently-gated sub-views (e.g. Trash, whose
   * tabs each require a different permission) — the entry should show as long as
   * the user can reach any one of them. Combined with `perm` via AND.
   */
  anyPerm?: readonly string[];
};

export type NavSection = {
  id: string;
  caption: string;
  /** Section-level icon used as a fallback when the sidebar is
   *  collapsed and the section is rendered as a stack of item icons. */
  icon: React.ComponentType<{ className?: string }>;
  items: NavSpec[];
};

// Top-level items live OUTSIDE any section. Overview opens the app;
// Settings is account-scoped and lives at the very bottom.
export const topNavTop: NavSpec[] = [
  // "/" opens the PAF (InicioPaf); the kit overview lives at /overview. Files and Billing are not part of
  // the PAF product (31/10/2026), nor the kit's Users, Roles and Groups screens (02/11/2026: the client manages its
  // users on /paf/usuarios); Chat, Catalog and Helpdesk left the code (04/11/2026). Out of the menu, and their routes
  // redirect home.
  { to: "/overview", label: "Visão geral", icon: LayoutDashboard },
];

export const topNavBottom: NavSpec[] = [
  { to: "/settings", label: "Minha conta", icon: Settings },
];

// Section accordion. Single-select — only one section open at a time.
export const sections: NavSection[] = [
  secaoMenuPaf,
  {
    id: "operations",
    caption: "Operações",
    icon: Activity,
    items: [
      // Live activity is SSE-backed; the stream is auth-only (no permission), so no gate.
      { to: "/activity", label: "Atividade ao vivo", icon: Activity },
    ],
  },
  {
    id: "system",
    caption: "Sistema",
    icon: HeartPulse,
    items: [
      // Health hits the anonymous /health/ready probe — visible to everyone.
      { to: "/system/health", label: "Saúde", icon: HeartPulse },
      { to: "/system/audits", label: "Trilha de auditoria", icon: ScrollText, perm: "Permissions.AuditTrails.View" },
      { to: "/system/sessions", label: "Sessões", icon: Wifi, perm: "Permissions.Sessions.ViewAll" },
    ],
  },
];

/** True when the user satisfies the item's gates: the single `perm` (if any)
 *  AND at least one of `anyPerm` (if any). Ungated items are always visible. */
function isNavItemVisible(item: NavSpec, permissions: readonly string[]): boolean {
  if (item.perm && !permissions.includes(item.perm)) return false;
  if (item.anyPerm && !item.anyPerm.some((p) => permissions.includes(p))) return false;
  return true;
}

/** Drop items the user can't access, then drop any section left empty. */
export function visibleSections(permissions: readonly string[]): NavSection[] {
  return sections
    .map((s) => ({ ...s, items: s.items.filter((i) => isNavItemVisible(i, permissions)) }))
    .filter((s) => s.items.length > 0);
}

/** Filter a flat nav list (top/bottom) by permission. */
export function visibleItems(items: NavSpec[], permissions: readonly string[]): NavSpec[] {
  return items.filter((i) => isNavItemVisible(i, permissions));
}

/** Find the section whose items contain the given path (best prefix match). */
export function findSectionForPath(pathname: string): string | null {
  let bestId: string | null = null;
  let bestLen = 0;
  for (const s of sections) {
    for (const item of s.items) {
      if (
        (item.to === "/" && pathname === "/") ||
        (item.to !== "/" && pathname.startsWith(item.to))
      ) {
        if (item.to.length > bestLen) {
          bestLen = item.to.length;
          bestId = s.id;
        }
      }
    }
  }
  return bestId;
}
