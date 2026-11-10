import type { Page, Route } from "@playwright/test";
import { mockJsonResponse } from "./api-mocks";

/**
 * A profile body that satisfies the topbar avatar + settings/profile read.
 * Matches the runtime UserDto shape the dashboard expects.
 */
export const DEFAULT_PROFILE = {
  id: "u-test-1",
  userName: "alice",
  email: "alice@acme.com",
  firstName: "Alice",
  lastName: "Nguyen",
  phoneNumber: "",
  isActive: true,
  emailConfirmed: true,
  twoFactorEnabled: false,
  imageUrl: null,
} as const;

/** Basic-role grants behind the shell's own queries. */
export const SHELL_PERMISSIONS = [
  "Permissions.Notifications.Inbox.View",
  "Permissions.Chat.Channels.View",
  "Permissions.Billing.View",
];

/**
 * Mock every API call the authenticated AppShell fires on load so any
 * protected page can be visited in isolation without hanging on the
 * SSE provider.
 *
 * ORDERING: Playwright matches the MOST RECENTLY registered route first.
 * We register broad globs first and the more-specific ones last. Callers
 * register their page-specific mocks AFTER calling this, so those win over
 * these defaults (e.g. a spec can return real rows).
 */
export async function installShellMocks(page: Page): Promise<void> {
  // Long-lived transports — abort so they neither keep the network busy nor spew reconnect noise.
  await page.route("**/api/v1/sse/**", (r: Route) => r.abort());
  await page.route("**/negotiate**", (r: Route) => r.abort());

  // Profile + permissions. The default grants match the Basic role and are
  // the ones the shell's own queries need (topbar badges, overview usage);
  // without them those queries never fire. Permission-gated specs re-mock
  // this route with the exact grants they need.
  await mockJsonResponse(page, "**/api/v1/identity/profile", DEFAULT_PROFILE);
  await mockJsonResponse(page, "**/api/v1/identity/permissions", SHELL_PERMISSIONS);

  // Tenant status drives the global expiry/grace banner mounted in the
  // AppShell. Default to a healthy, far-future tenant so the banner stays
  // hidden; specs that exercise the banner override this after the call.
  await mockJsonResponse(page, "**/api/v1/tenants/me/status**", {
    id: "acme",
    name: "Acme Corp",
    isActive: true,
    validUpto: new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString(),
    hasConnectionString: false,
    adminEmail: "admin@acme.com",
    issuer: null,
    plan: "Scale",
    expiryState: "Active",
    graceEndsUtc: new Date(Date.now() + 372 * 24 * 60 * 60 * 1000).toISOString(),
  });
}

/** Build a Playwright-shaped paged response body. */
export function paged<T>(items: T[], overrides: Partial<{ pageNumber: number; pageSize: number; totalCount: number; totalPages: number }> = {}) {
  const pageSize = overrides.pageSize ?? 20;
  const totalCount = overrides.totalCount ?? items.length;
  return {
    items,
    pageNumber: overrides.pageNumber ?? 1,
    pageSize,
    totalCount,
    totalPages: overrides.totalPages ?? Math.max(1, Math.ceil(totalCount / pageSize)),
    hasPrevious: (overrides.pageNumber ?? 1) > 1,
    hasNext: (overrides.pageNumber ?? 1) < (overrides.totalPages ?? Math.max(1, Math.ceil(totalCount / pageSize))),
  };
}
