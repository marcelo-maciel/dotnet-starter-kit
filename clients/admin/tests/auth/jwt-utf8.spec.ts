import { expect, test } from "@playwright/test";

// C4: the server signs the JWT payload as UTF-8. Decoding it with atob alone reads it as Latin-1, so a PAF
// user named "João Conceição" showed as "JoÃ£o ConceiÃ§Ã£o". The token here is built the way the server
// builds it (UTF-8 bytes, base64url) and decoded by the app's own module, served by the Vite dev server.

const b64url = (value: unknown) => Buffer.from(JSON.stringify(value), "utf8").toString("base64url");

test("decodeJwt reads a UTF-8 payload, accents intact", async ({ page }) => {
  const token = [b64url({ alg: "HS256", typ: "JWT" }), b64url({ sub: "u-1", name: "João Conceição", tenant: "acme" }), "sig"].join(".");
  await page.goto("/login");

  const name = await page.evaluate(async (jwt) => {
    // A path the Vite dev server resolves in the page, not one tsc can: a variable keeps it out of the type check.
    const modulo = "/src/auth/jwt.ts";
    const mod = (await import(/* @vite-ignore */ modulo)) as typeof import("../../src/auth/jwt");
    return mod.decodeJwt(jwt)?.name;
  }, token);

  expect(name).toBe("João Conceição");
});
