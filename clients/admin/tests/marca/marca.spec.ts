import { expect, test } from "@playwright/test";

// Operator console shares the PAF ramp: #0067F0 at the 600 stop.
test("operator console primary is the PAF blue, has pt-BR lang and no Google Fonts request", async ({ page }) => {
  const external: string[] = [];
  page.on("request", (req) => {
    if (/fonts\.googleapis\.com|fonts\.gstatic\.com/.test(req.url())) external.push(req.url());
  });
  await page.addInitScript(() => {
    localStorage.setItem("fsh.theme", "light");
    localStorage.setItem("fsh.accent", "rose");
  });
  await page.goto("/login");
  await page.waitForLoadState("networkidle");

  expect(await page.locator("html").getAttribute("lang")).toBe("pt-BR");
  const rgb = await page.evaluate(() => {
    const probe = document.createElement("div");
    probe.style.color = "var(--primary)";
    document.body.appendChild(probe);
    const ctx = document.createElement("canvas").getContext("2d")!;
    ctx.fillStyle = getComputedStyle(probe).color;
    ctx.fillRect(0, 0, 1, 1);
    probe.remove();
    return Array.from(ctx.getImageData(0, 0, 1, 1).data.slice(0, 3));
  });
  for (const [i, v] of [2, 103, 240].entries()) expect(Math.abs(rgb[i] - v)).toBeLessThanOrEqual(3);
  expect(external).toEqual([]);
});
