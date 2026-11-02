import type { SseStatus } from "@/sse/sse-context";

/** pt-BR wording of the live stream state, for the screens that print it. */
export const SSE_STATUS_LABEL: Record<SseStatus, string> = {
  idle: "inativo",
  connecting: "conectando",
  connected: "conectado",
  reconnecting: "reconectando",
  error: "fora do ar",
};
