/// <reference types="node" />
// Run: node --test src/lib/format.test.ts (Node 22 strips the types, D21).
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  formatStampMinutes,
  formatStampPrecise,
  formatStampSeconds,
  startOfSaoPauloDay,
  toSaoPauloDay,
} from "./format.ts";

test("the picked day starts at 00:00 in Brasília, 03:00 UTC, not at 00:00 UTC (21:00 of the day before)", () => {
  assert.equal(startOfSaoPauloDay("2026-05-01"), "2026-05-01T03:00:00.000Z");
  assert.equal(startOfSaoPauloDay("2026-12-31"), "2026-12-31T03:00:00.000Z");
});

test("the prefill is the Brasília day of the stored instant", () => {
  assert.equal(toSaoPauloDay("2026-05-01T02:59:00Z"), "2026-04-30");
  assert.equal(toSaoPauloDay("2026-05-01T03:00:00Z"), "2026-05-01");
  assert.equal(toSaoPauloDay(startOfSaoPauloDay("2026-10-31")), "2026-10-31");
  assert.equal(toSaoPauloDay(null), "");
  assert.equal(toSaoPauloDay("not a date"), "");
});

test("dense stamps are Brasília wall clock, midnight as 00 and never 24", () => {
  assert.equal(formatStampSeconds("2026-05-01T03:05:09Z"), "05-01 00:05:09");
  assert.equal(formatStampMinutes("2026-05-01T02:59:00Z"), "04-30 23:59");
  assert.equal(formatStampSeconds("garbage"), "garbage");
});

test("the precise stamp carries the offset it was read with", () => {
  assert.equal(formatStampPrecise("2026-05-01T03:00:00.042Z"), "2026-05-01 00:00:00.042 -03:00");
  assert.equal(formatStampPrecise(null), "—");
});
