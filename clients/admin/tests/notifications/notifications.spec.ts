import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS } from "../helpers/shell-mocks";

// Nothing creates notifications since the Chat module was removed, so the bell, the inbox and the kit AppHub
// connection (/api/v1/realtime/hub) are gone from the admin panel.
test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
});

test.describe("notificações removidas", () => {
  test("o topo não tem sino e o painel não abre conexão com o AppHub do kit", async ({ page }) => {
    const hubRequests: string[] = [];
    const websockets: string[] = [];
    page.on("request", (r) => {
      if (r.url().includes("/api/v1/realtime")) hubRequests.push(r.url());
    });
    // Vite HMR also opens a websocket in dev: only the kit hub counts.
    page.on("websocket", (ws) => {
      if (ws.url().includes("/api/v1/realtime")) websockets.push(ws.url());
    });

    await page.goto("/tenants");
    const topbar = page.getByRole("banner");
    await expect(topbar.getByRole("button", { name: "Open profile menu" })).toBeVisible();
    await expect(topbar.getByRole("button", { name: /notifica/i })).toHaveCount(0);
    await expect(topbar.getByRole("status")).toHaveCount(0);

    // Give a late-mounting provider time to connect before asserting the absence.
    await page.waitForLoadState("networkidle");
    expect(hubRequests).toEqual([]);
    expect(websockets).toEqual([]);
  });

  test("/notifications volta para o início", async ({ page }) => {
    await page.goto("/notifications");
    await expect(page).toHaveURL(/\/$/);
  });
});
