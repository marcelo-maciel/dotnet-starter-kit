import { expect, test, type Page } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks, paged } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

test.describe("overview (/overview)", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/audits**", paged([]));
  });

  test("renders the greeting header and the live-events card", async ({ page }) => {
    await page.goto("/overview");
    await expect(page.getByRole("heading", { name: /(bom dia|boa tarde|boa noite), alice/i })).toBeVisible();
    await expect(page.getByText("Eventos ao vivo", { exact: true })).toBeVisible();
  });

  test("renders the recent-audits + system-status sections", async ({ page }) => {
    await page.goto("/overview");
    await expect(page.getByText("Auditoria recente", { exact: true })).toBeVisible();
    await expect(page.getByText("Estado do sistema", { exact: true })).toBeVisible();
  });

  // Chat, Files, Catalog, Helpdesk, Billing and Trash are not part of the PAF product (31/10/2026), nor the kit's
  // Users, Roles and Groups screens (02/11/2026): their routes redirect home, so the overview must not offer a
  // shortcut that bounces back.
  const ROTAS_REMOVIDAS = ["/subscription", "/invoices", "/wallet", "/catalog", "/chat", "/files", "/tickets", "/system/trash", "/identity"];
  const linksParaRotasRemovidas = (page: Page) =>
    page.locator(ROTAS_REMOVIDAS.map((rota) => `a[href^="${rota}"]`).join(", "));

  test("visão geral não tem atalho para rota removida", async ({ page }) => {
    await page.goto("/overview");
    const card = page.locator("section", { has: page.getByRole("heading", { name: "Atalhos" }) });
    await expect(card.getByRole("link", { name: /atividade ao vivo/i })).toBeVisible();
    await expect(card.getByRole("link", { name: /invite users|convidar/i })).toHaveCount(0);
    await expect(linksParaRotasRemovidas(page)).toHaveCount(0);
  });

  // Tenant roles lose Permissions.Billing.View (31/10/2026): a Billing call from the overview would only 403.
  test("visão geral não chama Billing nem mostra plano, validade, uso ou assinatura", async ({ page }) => {
    const chamadasBilling: string[] = [];
    page.on("request", (req) => {
      if (req.url().includes("/api/v1/billing/")) chamadasBilling.push(req.url());
    });
    const errors: string[] = [];
    page.on("pageerror", (err) => errors.push(err.message));

    await page.goto("/overview");
    await expect(page.getByText("Eventos ao vivo", { exact: true })).toBeVisible();
    await expect(page.getByText("Auditoria recente", { exact: true })).toBeVisible();
    for (const billing of ["Plan", "Valid for", "Resources", "Subscription", "Usage by resource", "No active subscription"]) {
      await expect(page.getByText(billing, { exact: true })).toHaveCount(0);
    }
    await expect(page.getByText(/pick a plan|welcome to|couldn't load/i)).toHaveCount(0);
    expect(chamadasBilling).toEqual([]);
    expect(errors).toEqual([]);
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
    const card = page.locator("section", { has: page.getByRole("heading", { name: "Auditoria recente" }) });
    await expect(card.getByText("Sem acesso à auditoria")).toBeVisible();
    await expect(card.getByRole("link", { name: /ver tudo/i })).toHaveCount(0);
    await expect(page.getByText(/falha|failure|couldn't load/i)).toHaveCount(0);

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
    const card = page.locator("section", { has: page.getByRole("heading", { name: "Auditoria recente" }) });
    await expect(card.getByText("api.identity.IssueJwtToken")).toBeVisible();
    await expect(card.getByRole("link", { name: /ver tudo/i })).toBeVisible();
  });

  test("quick-action tiles are wide enough for their title and description", async ({ page }) => {
    // At desktop widths the card sits in a narrow column beside the 360px
    // rail, so tiles sized off the viewport squeeze titles past their box.
    // A wide card must not spread the tiles more than two-up either.
    for (const width of [1280, 2200]) {
      await page.setViewportSize({ width, height: 800 });
      await page.goto("/overview");
      const card = page.locator("section", { has: page.getByRole("heading", { name: "Atalhos" }) });
      await expect(card.getByRole("link")).toHaveCount(1);
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

  // Tenant validity left the overview with Billing; the global ExpiryBanner reports it (tests/billing/subscription.spec.ts).
});

