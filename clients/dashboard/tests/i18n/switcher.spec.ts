import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// Task 10 — the topbar language switcher. Switching to Português must:
//  (a) localize the UI in place (the "Language" section label becomes "Idioma"),
//  (b) PUT the chosen locale to /identity/profile with the name preserved
//      (a locale-only save must not wipe FirstName/LastName), and
//  (c) trigger a token refresh so the new `locale` JWT claim is minted.

/** Minimal decodable JWT for the refreshed session (auth-context decodes it). */
function fakeJwt(payload: Record<string, unknown>): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return [b64url({ alg: "HS256", typ: "JWT" }), b64url(payload), "sig"].join(".");
}

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
});

test.describe("language switcher", () => {
  test("switching to Português localizes the UI, persists the locale and refreshes the token", async ({
    page,
  }) => {
    let putBody: { locale?: string; firstName?: string; lastName?: string } | null = null;
    let refreshCalled = false;

    // GET returns the current (en-US) profile with a name so we can assert it is
    // preserved; PUT captures the body. Registered AFTER installShellMocks so
    // this handler wins (LIFO) over the default profile stub. updateMyProfile
    // itself issues a GET before the PUT, so both methods route through here.
    await page.route("**/api/v1/identity/profile", async (route) => {
      const method = route.request().method();
      if (method === "PUT") {
        putBody = route.request().postDataJSON();
        await route.fulfill({ status: 200 });
        return;
      }
      if (method === "GET") {
        await route.fulfill({
          status: 200,
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            id: "u-test-1",
            firstName: "Alice",
            lastName: "Nguyen",
            phoneNumber: "",
            email: "alice@acme.com",
            isActive: true,
            emailConfirmed: true,
            locale: "en-US",
          }),
        });
        return;
      }
      await route.fallback();
    });

    // The onSuccess token refresh — capture the call and return a fresh session
    // carrying the new locale claim so the auth context stays valid.
    await page.route("**/api/v1/identity/token/refresh", async (route) => {
      refreshCalled = true;
      const token = fakeJwt({
        sub: "u-test-1",
        email: TEST_USER.email,
        name: "Alice Nguyen",
        tenant: "acme",
        locale: "pt-BR",
        exp: Math.floor(Date.now() / 1000) + 3600,
        iat: Math.floor(Date.now() / 1000),
      });
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token, refreshToken: "fresh-refresh-token" }),
      });
    });

    await page.goto("/");

    // Open the profile dropdown, then the (default en-US) language section.
    await page.getByRole("button", { name: /open profile menu/i }).click();
    await expect(page.getByText("Language", { exact: true })).toBeVisible();

    await page.getByRole("menuitem", { name: "Português (BR)" }).click();

    // (b) the chosen locale was persisted, name preserved (no data loss). Poll
    // the captured body: the route handler that assigns it runs asynchronously.
    await expect.poll(() => putBody?.locale).toBe("pt-BR");
    expect(putBody?.firstName).toBe("Alice");
    expect(putBody?.lastName).toBe("Nguyen");

    // (a) the section label localized in place (menu kept open on select).
    await expect(page.getByText("Idioma", { exact: true })).toBeVisible();

    // (c) the token refresh fired to re-mint the locale claim.
    await expect.poll(() => refreshCalled).toBe(true);
  });
});
