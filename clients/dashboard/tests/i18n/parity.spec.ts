import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";

// Every namespace must carry identical key sets across locales. A key present
// in one catalog but missing in the other silently falls back to the default
// language at runtime, shipping a mixed-locale UI. This guards each catalog as
// new namespaces land (add one assertion per namespace here per wave).
const load = (locale: string, ns: string): Record<string, string> =>
  JSON.parse(
    readFileSync(
      fileURLToPath(new URL(`../../src/locales/${locale}/${ns}.json`, import.meta.url)),
      "utf8",
    ),
  );

test.describe("i18n catalog parity", () => {
  test("common: en-US and pt-BR expose the same keys", () => {
    const en = load("en-US", "common");
    const pt = load("pt-BR", "common");
    expect(Object.keys(en).sort()).toEqual(Object.keys(pt).sort());
  });
});
