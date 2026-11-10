import { expect, test, type Page } from "@playwright/test";
import { installShellMocks } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// PAF brand: #0067F0 at the 600 stop (light) and the 500 stop (dark). The ramp is
// authored in OKLCH, so the expected sRGB is what the browser itself resolves.
const PAF_BLUE_600 = [2, 103, 240];
const PAF_BLUE_500 = [62, 135, 255];

/** Resolve a CSS colour expression to sRGB through a canvas, whatever syntax the browser serialises. */
async function resolvedRgb(page: Page, cssColor: string): Promise<number[]> {
  return page.evaluate((expr) => {
    const probe = document.createElement("div");
    probe.style.color = expr;
    document.body.appendChild(probe);
    const resolved = getComputedStyle(probe).color;
    probe.remove();
    const ctx = document.createElement("canvas").getContext("2d")!;
    ctx.fillStyle = resolved;
    ctx.fillRect(0, 0, 1, 1);
    return Array.from(ctx.getImageData(0, 0, 1, 1).data.slice(0, 3));
  }, cssColor);
}

function expectNear(actual: number[], expected: number[]) {
  for (let i = 0; i < 3; i++) expect(Math.abs(actual[i] - expected[i])).toBeLessThanOrEqual(3);
}

test.describe("marca do PAF", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
  });

  test("primary is the PAF blue in the light theme", async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem("fsh.theme", "light"));
    await page.goto("/settings/appearance");
    await expect(page.getByText("Fonte", { exact: true })).toBeVisible();
    expectNear(await resolvedRgb(page, "var(--primary)"), PAF_BLUE_600);
  });

  test("a saved rose accent is ignored (light theme)", async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem("fsh.theme", "light");
      localStorage.setItem("fsh.accent", "rose");
      localStorage.setItem("fsh.accent.custom", JSON.stringify({ h: 12, c: 1 }));
    });
    await page.goto("/settings/appearance");
    await expect(page.getByText("Fonte", { exact: true })).toBeVisible();
    expectNear(await resolvedRgb(page, "var(--primary)"), PAF_BLUE_600);
    await expect(page.locator("html")).not.toHaveClass(/accent-/);
  });

  test("a saved violet accent is ignored (dark theme), primary is the 500 stop with dark text", async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem("fsh.theme", "dark");
      localStorage.setItem("fsh.accent", "violet");
    });
    await page.goto("/settings/appearance");
    await expect(page.locator("html")).toHaveClass(/dark/);
    expectNear(await resolvedRgb(page, "var(--primary)"), PAF_BLUE_500);
    const fg = await resolvedRgb(page, "var(--primary-foreground)");
    // Dark tint, not white: white on the 500 stop is 3.3:1, under WCAG AA.
    expect(Math.max(...fg)).toBeLessThan(80);
  });

  test("appearance offers font, theme and density but no accent colour", async ({ page }) => {
    await page.goto("/settings/appearance");
    await expect(page.getByText("Fonte", { exact: true })).toBeVisible();
    await expect(page.getByText("Cor de destaque")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /cor de destaque/i })).toHaveCount(0);
  });

  test("no request leaves for Google Fonts, and the chosen font comes from /fonts", async ({ page }) => {
    const external: string[] = [];
    const fontFiles: string[] = [];
    page.on("request", (req) => {
      const url = req.url();
      if (/fonts\.googleapis\.com|fonts\.gstatic\.com/.test(url)) external.push(url);
      if (/\/fonts\/.*\.woff2/.test(url)) fontFiles.push(url);
    });
    await page.goto("/settings/appearance");
    await page.getByRole("button", { name: "Fonte Sora" }).click();
    await page.evaluate(() => document.fonts.load("16px Sora", "Passo a Frente"));
    await page.waitForLoadState("networkidle");

    expect(external).toEqual([]);
    expect(fontFiles.some((u) => /sora-latin\.woff2/.test(u))).toBe(true);
  });

  test("favicons carry the cache-busting version and the files exist", async ({ page, request }) => {
    await page.goto("/settings/appearance");
    const hrefs = await page.locator('link[rel="icon"], link[rel="apple-touch-icon"]').evaluateAll((els) =>
      els.map((e) => e.getAttribute("href") ?? ""),
    );
    expect(hrefs).toHaveLength(4);
    for (const href of hrefs) {
      expect(href).toContain("?v=paf1");
      const res = await request.get(href);
      expect(res.status(), href).toBe(200);
    }
  });

  test("the sign-in chrome shows the PAF logo with its accessible name", async ({ page }) => {
    await page.addInitScript(() => localStorage.clear());
    await page.goto("/forgot-password");
    const logo = page.getByRole("img", { name: "Passo a Frente" });
    await expect(logo).toBeVisible();
    const natural = await logo.evaluate((img: HTMLImageElement) => img.naturalWidth);
    expect(natural).toBeGreaterThan(0);
  });
});
