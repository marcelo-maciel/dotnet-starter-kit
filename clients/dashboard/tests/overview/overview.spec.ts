import { expect, test, type Page } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks, paged } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

const SUBSCRIPTION = {
  id: "sub-1",
  tenantId: "acme",
  planId: "plan-scale",
  planKey: "Scale",
  startUtc: new Date(Date.UTC(2026, 0, 1)).toISOString(),
  endUtc: null,
  status: "Active",
};

test.describe("overview (/overview)", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/billing/usage**", []);
    await mockJsonResponse(page, "**/api/v1/billing/subscriptions/me**", SUBSCRIPTION);
    await mockJsonResponse(page, "**/api/v1/audits**", paged([]));
  });

  test("renders the greeting header and the four stat cards", async ({ page }) => {
    await page.goto("/overview");
    await expect(page.getByRole("heading", { name: /good (morning|afternoon|evening), alice/i })).toBeVisible();
    await expect(page.getByText("Plan", { exact: true })).toBeVisible();
    await expect(page.getByText("Resources", { exact: true })).toBeVisible();
    await expect(page.getByText("Live events", { exact: true })).toBeVisible();
  });

  test("renders the recent-audits + usage + system-status sections", async ({ page }) => {
    await page.goto("/overview");
    await expect(page.getByText("Recent audits", { exact: true })).toBeVisible();
    await expect(page.getByText("Usage by resource", { exact: true })).toBeVisible();
    await expect(page.getByText("System status", { exact: true })).toBeVisible();
    // Empty usage → calm empty state, not a crash.
    await expect(page.getByText(/no usage captured yet/i)).toBeVisible();
  });

  // Chat, Files, Catalog, Helpdesk, Billing and Trash are not part of the PAF product (31/10/2026): their routes
  // redirect home, so the overview must not offer a shortcut that bounces back.
  const ROTAS_REMOVIDAS = ["/subscription", "/invoices", "/wallet", "/catalog", "/chat", "/files", "/tickets", "/system/trash"];
  const linksParaRotasRemovidas = (page: Page) =>
    page.locator(ROTAS_REMOVIDAS.map((rota) => `a[href^="${rota}"]`).join(", "));

  test("visão geral com assinatura não tem atalho para rota removida", async ({ page }) => {
    await page.goto("/overview");
    const card = page.locator("section", { has: page.getByRole("heading", { name: "Quick actions" }) });
    await expect(card.getByRole("link", { name: /invite users/i })).toBeVisible();
    await expect(card.getByRole("link", { name: /live activity/i })).toBeVisible();
    const assinatura = page.locator("section", { has: page.getByRole("heading", { name: "Subscription" }) });
    await expect(assinatura.getByText("Scale", { exact: true })).toBeVisible();
    await expect(linksParaRotasRemovidas(page)).toHaveCount(0);
  });

  test("visão geral sem assinatura mostra o primeiro acesso sem plano, catálogo nem assinatura", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/billing/subscriptions/me**", null);
    await page.goto("/overview");
    const primeiroAcesso = page.locator("section", { has: page.getByRole("heading", { name: /welcome to/i }) });
    await expect(primeiroAcesso.getByRole("link")).toHaveCount(2);
    await expect(primeiroAcesso.getByRole("link", { name: /invite your team/i })).toBeVisible();
    await expect(primeiroAcesso.getByRole("link", { name: /watch live/i })).toBeVisible();
    await expect(page.getByText("No active subscription", { exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: /view subscription/i })).toHaveCount(0);
    await expect(linksParaRotasRemovidas(page)).toHaveCount(0);
  });

  // GET /audits requires AuditTrails.View; installShellMocks grants no permissions.
  test("recent audits: without Audit trail access, no /audits call and a no-access state", async ({ page }) => {
    let auditCalls = 0;
    page.on("request", (req) => {
      if (new URL(req.url()).pathname.startsWith("/api/v1/audits")) auditCalls++;
    });
    const errors: string[] = [];
    page.on("pageerror", (err) => errors.push(err.message));

    await page.goto("/overview");
    const card = page.locator("section", { has: page.getByRole("heading", { name: "Recent audits" }) });
    await expect(card.getByText("No access to audits")).toBeVisible();
    await expect(card.getByRole("link", { name: /see all/i })).toHaveCount(0);
    await expect(page.getByText(/failure|couldn't load/i)).toHaveCount(0);

    expect(auditCalls).toBe(0);
    expect(errors).toEqual([]);
  });

  test("recent audits: with Audit trail access, rows load from /audits", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", ["Permissions.AuditTrails.View"]);
    await mockJsonResponse(
      page,
      "**/api/v1/audits**",
      paged([
        {
          id: "a-1",
          occurredAtUtc: new Date(Date.now() - 5 * 60 * 1000).toISOString(),
          eventType: "Security",
          severity: "Information",
          tenantId: "acme",
          userId: "u-test-1",
          userName: "Alice Nguyen",
          source: "api.identity.IssueJwtToken",
          tags: 0,
        },
      ]),
    );

    await page.goto("/overview");
    const card = page.locator("section", { has: page.getByRole("heading", { name: "Recent audits" }) });
    await expect(card.getByText("api.identity.IssueJwtToken")).toBeVisible();
    await expect(card.getByRole("link", { name: /see all/i })).toBeVisible();
  });

  test("quick-action tiles are wide enough for their title and description", async ({ page }) => {
    // At desktop widths the card sits in a narrow column beside the 360px
    // rail, so tiles sized off the viewport squeeze titles past their box.
    // A wide card must not spread the tiles more than two-up either.
    for (const width of [1280, 2200]) {
      await page.setViewportSize({ width, height: 800 });
      await page.goto("/overview");
      const card = page.locator("section", { has: page.getByRole("heading", { name: "Quick actions" }) });
      await expect(card.getByRole("link")).toHaveCount(2);
      await page.evaluate(() => document.fonts.ready);

      const columns = await card
        .getByRole("link")
        .first()
        .evaluate((link) => getComputedStyle(link.parentElement!).gridTemplateColumns.split(" ").length);
      expect(columns, `columns at ${width}px`).toBeLessThanOrEqual(2);

      const tiles = await card.getByRole("link").evaluateAll((links) =>
        links.map((link) => {
          const lineCount = (el: Element) => {
            const range = document.createRange();
            range.selectNodeContents(el);
            const tops = new Set<number>();
            for (const rect of Array.from(range.getClientRects())) {
              if (rect.width > 0) tops.add(Math.round(rect.top));
            }
            return tops.size;
          };
          const description = link.querySelector("p")!;
          const title = description.previousElementSibling!;
          return {
            title: title.textContent,
            titleOverflow: title.scrollWidth - title.clientWidth,
            titleLines: lineCount(title),
            descriptionOverflow: description.scrollWidth - description.clientWidth,
            descriptionLines: lineCount(description),
          };
        }),
      );

      for (const tile of tiles) {
        expect(tile, `${tile.title} at ${width}px`).toMatchObject({
          titleOverflow: 0,
          titleLines: 1,
          descriptionOverflow: 0,
        });
        expect(tile.descriptionLines, `${tile.title} description at ${width}px`).toBeLessThanOrEqual(2);
      }
    }
  });

  test("Valid-for card reflects an in-grace tenant", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", {
      id: "acme",
      name: "Acme Corp",
      isActive: true,
      validUpto: new Date(Date.now() - 2 * 24 * 60 * 60 * 1000).toISOString(),
      hasConnectionString: false,
      adminEmail: "admin@acme.com",
      issuer: null,
      plan: "Scale",
      expiryState: "InGrace",
      graceEndsUtc: new Date(Date.now() + 5 * 24 * 60 * 60 * 1000).toISOString(),
    });
    await page.goto("/overview");
    await expect(page.getByText("Valid for", { exact: true })).toBeVisible();
    // Grace surfaces the grace-end caption on the stat card.
    await expect(page.getByText(/grace ends/i)).toBeVisible();
  });

  test("Valid-for card reflects an expired tenant", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", {
      id: "acme",
      name: "Acme Corp",
      isActive: false,
      validUpto: new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString(),
      hasConnectionString: false,
      adminEmail: "admin@acme.com",
      issuer: null,
      plan: "Scale",
      expiryState: "Expired",
      graceEndsUtc: new Date(Date.now() - 16 * 24 * 60 * 60 * 1000).toISOString(),
    });
    await page.goto("/overview");
    await expect(page.getByText("Valid for", { exact: true })).toBeVisible();
    // The stat card reads "Expired" rather than a healthy day count.
    await expect(page.getByText("Expired", { exact: true })).toBeVisible();
  });
});

