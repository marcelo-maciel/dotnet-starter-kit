import { expect, test } from "@playwright/test";

// The operator's sign-in screens show the PAF symbol, not the old "P" tile, with "Passo a Frente" as the name.
const PAGES = [
  "/login",
  "/forgot-password",
  "/reset-password?token=abc&email=ana%40root.test&tenant=root",
  "/confirm-email?userId=u-1&code=abc&tenant=root",
];

for (const path of PAGES) {
  test(`${path.split("?")[0]} shows the PAF symbol and the "Passo a Frente" name`, async ({ page }) => {
    await page.route("**/api/v1/**", (route) =>
      route.fulfill({ status: 200, headers: { "Content-Type": "application/json" }, body: '""' }),
    );
    await page.goto(path);

    const symbol = page.locator('img[src="/logo-paf-simbolo.png"]').first();
    await expect(symbol).toBeVisible();
    expect(await symbol.evaluate((img: HTMLImageElement) => img.complete && img.naturalWidth > 0)).toBe(true);
    await expect(page.getByText("Passo a Frente", { exact: true }).first()).toBeVisible();
    await expect(page.locator(".brand-mark")).toHaveCount(0);
  });
}
