import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ShieldCheck } from "lucide-react";

// ────────────────────────────────────────────────────────────────────────
// AuthShell — calm centered-card chrome for unauthenticated pages
// (forgot-password, reset-password, confirm-email). Mirrors login.tsx's
// dentalOS vocabulary: atmospheric rose+saffron orbs, PAF logo
// lockup, warm-paper card with backdrop blur. No parallax, no
// brackets, no graph paper, no dialog-script eyebrow.
// ────────────────────────────────────────────────────────────────────────

/**
 * Display headline — Outfit, semibold, calm tracking. Mirrors the
 * "Welcome back" line in login.tsx. `lead`/`accent`/`trail` compose
 * but the accent is now a flat foreground-strong colour rather than a
 * gradient sweep (the gradient read too "demo-y" against the new palette).
 */
export function AuthHeadline({
  lead,
  accent,
  trail,
}: {
  lead?: string;
  accent?: string;
  trail?: string;
}) {
  return (
    <h1 className="mb-1.5 font-display text-[22px] font-semibold tracking-tight text-[var(--color-foreground)]">
      {lead && <>{lead} </>}
      {accent && <span className="text-[var(--color-primary)]">{accent}</span>}
      {trail && <>{trail}</>}
    </h1>
  );
}

export function AuthShell({
  children,
  footer,
}: {
  /** Card body */
  children: ReactNode;
  /** Optional row beneath the card — e.g. "Back to sign in" link */
  footer?: ReactNode;
}) {
  const { t } = useTranslation("auth");
  return (
    <div className="relative flex min-h-screen items-center justify-center overflow-hidden bg-[var(--color-background)] px-5 py-8 sm:py-12">
      {/* Atmospheric background — three rose/saffron blur orbs at
          descending opacities. No parallax, no animation — calm. */}
      <div className="pointer-events-none absolute inset-0" aria-hidden>
        <div
          className="absolute -top-[25%] -left-[15%] h-[70vw] w-[70vw] rounded-full blur-[140px]"
          style={{ backgroundColor: "oklch(from var(--color-primary) l c h / 0.05)" }}
        />
        <div
          className="absolute -bottom-[20%] -right-[10%] h-[55vw] w-[55vw] rounded-full blur-[120px]"
          style={{ backgroundColor: "oklch(from var(--color-saffron) l c h / 0.07)" }}
        />
        <div
          className="absolute top-[10%] right-[5%] h-[30vw] w-[30vw] rounded-full blur-[80px]"
          style={{ backgroundColor: "oklch(from var(--color-primary) l c h / 0.025)" }}
        />
      </div>

      {/* Card column */}
      <div className="relative z-10 w-full max-w-[420px] fsh-enter fsh-enter-1">
        {/* Brand lockup: the logo has a navy wordmark, so it sits on a white plate to stay legible in the dark theme.
            ponytail: logo files are rasters cut from a 373x507 PNG; swap for the vector logo when Marcelo has one. */}
        <div className="mb-8 flex flex-col items-center">
          <div className="rounded-xl bg-white px-6 py-4 shadow-[0_1px_3px_oklch(0_0_0_/_0.06)]">
            <img src="/logo-paf.png" alt="Passo a Frente" className="h-36 w-auto object-contain" />
          </div>
        </div>

        {/* Form card — warm-paper with backdrop blur */}
        <div className="rounded-xl border border-[var(--color-border)] bg-[oklch(from_var(--color-card)_l_c_h_/_0.85)] backdrop-blur-xl shadow-[0_1px_3px_oklch(0_0_0_/_0.04),0_8px_24px_oklch(0_0_0_/_0.06)]">
          <div className="px-6 py-7 sm:px-8 sm:py-9">{children}</div>
        </div>

        {footer && (
          <div className="mt-6 text-center text-[12.5px] text-[var(--color-muted-foreground)]">
            {footer}
          </div>
        )}

        <div className="mt-6 flex items-center justify-center gap-1.5 text-[11px] text-[var(--color-muted-foreground)]">
          <ShieldCheck className="size-3" />
          <span>{t("shell.encrypted")}</span>
        </div>
        <p className="mt-4 text-center text-[10px] font-medium uppercase tracking-wider text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.5)]">
          {t("shell.footer")}
        </p>
      </div>
    </div>
  );
}
