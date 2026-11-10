import { expect, test } from "@playwright/test";
import { installShellMocks } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

// What the PAF user reads: the app's pinned pt-BR, without the suites' en-US override (playwright.config.ts), under
// Playwright's en-US browser.
test.use({ storageState: { cookies: [], origins: [] } });

// Appearance is entirely client-side — the ThemeProvider toggles the
// `dark` class on <html> and persists choices to localStorage. No API
// is hit beyond the shell defaults, so installShellMocks is enough.
test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
});

test.describe("settings/appearance — theme, font, density (client-side)", () => {
  test("renders the theme, font, and density sections", async ({ page }) => {
    await page.goto("/settings/appearance");

    // CardTitle is a styled <div>, not a heading — assert on the text.
    await expect(page.getByText("Tema", { exact: true })).toBeVisible();
    await expect(page.getByText("Fonte", { exact: true })).toBeVisible();
    await expect(page.getByText("Densidade", { exact: true })).toBeVisible();

    // The three theme swatches are buttons with descriptive aria-labels.
    await expect(page.getByRole("button", { name: "Tema Claro" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Tema Sistema" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Tema Escuro" })).toBeVisible();
  });

  test("selecting Dark applies dark mode; Light reverts it", async ({ page }) => {
    await page.goto("/settings/appearance");
    const html = page.locator("html");

    await page.getByRole("button", { name: "Tema Escuro" }).click();
    await expect(html).toHaveClass(/dark/);
    // The chosen mode is the active swatch (aria-pressed).
    await expect(page.getByRole("button", { name: "Tema Escuro" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );

    await page.getByRole("button", { name: "Tema Claro" }).click();
    await expect(html).not.toHaveClass(/dark/);
    await expect(page.getByRole("button", { name: "Tema Claro" })).toHaveAttribute(
      "aria-pressed",
      "true",
    );
  });

  test("persists the selected mode to localStorage", async ({ page }) => {
    await page.goto("/settings/appearance");

    await page.getByRole("button", { name: "Tema Escuro" }).click();
    const stored = await page.evaluate(() => window.localStorage.getItem("fsh.theme"));
    expect(stored).toBe("dark");
  });

  test("has no accent colour choice, only theme, font and density", async ({ page }) => {
    await page.goto("/settings/appearance");
    await expect(page.getByText("Fonte", { exact: true })).toBeVisible();

    await expect(page.getByText("Cor de destaque")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /cor de destaque/i })).toHaveCount(0);
  });
});
