import { useRealtime } from "@/realtime/realtime-context";
import { cn } from "@/lib/cn";

const LABEL: Record<string, string> = {
  idle: "Desconectado",
  connecting: "Conectando",
  connected: "Ao vivo",
  reconnecting: "Reconectando",
  error: "Desconectado",
};

/**
 * Compact connection-state indicator backed by the shared SignalR hub. Mono
 * caption + colored dot — green when live, amber pulsing while reconnecting,
 * destructive when down. Mounted in the topbar so realtime state stays visible.
 *
 * `announce` opt-in: only one instance of this pill should announce status
 * changes to AT (otherwise reconnect events get spoken twice). Pass
 * `announce={true}` on the canonical mount; leave it off elsewhere.
 */
export function RealtimeStatusPill({
  className,
  announce = false,
}: {
  className?: string;
  announce?: boolean;
}) {
  const { status } = useRealtime();
  const label = LABEL[status] ?? "Desconectado";
  return (
    <span
      className={cn("realtime-status-pill", className)}
      data-status={status}
      {...(announce ? { role: "status", "aria-live": "polite" as const } : {})}
      title={`Tempo real: ${label}`}
    >
      <span aria-hidden className="realtime-status-dot" />
      <span>{label}</span>
    </span>
  );
}
