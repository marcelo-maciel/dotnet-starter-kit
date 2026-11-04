import { cn } from "@/lib/cn";

/**
 * BrandMark — the compact inline lockup used in the sidebar brand row and
 * any surface that needs a sub-header-sized reference to the product.
 *
 * Matches the dashboard's brand treatment: the PAF symbol on a small white
 * plate, paired with the "Passo a Frente" wordmark and a small "Operador" sub-label.
 *
 * The chartreuse signal colour from the old Console identity is retired here.
 * Colour-identity is now driven purely by the shared `--color-primary` token.
 */
export function BrandMark({ className }: { className?: string }) {
  return (
    <div className={cn("inline-flex select-none items-center gap-2.5", className)}>
      <span
        aria-hidden
        className="grid size-8 shrink-0 place-items-center rounded-lg bg-white p-0.5 ring-1 ring-[var(--color-border)]"
      >
        {/* ponytail: logo files are rasters cut from a 373x507 PNG; swap for the vector logo when Marcelo has one. */}
        <img src="/logo-paf-simbolo.png" alt="" className="size-full object-contain" />
      </span>
      <div className="flex flex-col">
        <span className="whitespace-nowrap font-display text-[15px] font-bold leading-none tracking-tight text-[var(--color-foreground)]">
          Passo a <span className="text-[var(--color-primary)]">Frente</span>
        </span>
        <span className="mt-0.5 text-[10px] font-semibold uppercase tracking-wider text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.7)]">
          Operador
        </span>
      </div>
    </div>
  );
}

/**
 * BrandMarkXL — splash version for the Login page. Leads with the FSH logo
 * mark + "fullstackhero" wordmark, then a display monogram and a one-line
 * system blurb.
 */
export function BrandMarkXL({ className }: { className?: string }) {
  return (
    <div className={cn("space-y-3", className)}>
      <div className="flex items-center gap-2.5">
        <span
          aria-hidden
          className="grid size-7 shrink-0 place-items-center rounded-lg bg-white p-0.5 ring-1 ring-[var(--color-border)]"
        >
          {/* ponytail: logo files are rasters cut from a 373x507 PNG; swap for the vector logo when Marcelo has one. */}
          <img src="/logo-paf-simbolo.png" alt="" className="size-full object-contain" />
        </span>
        <span className="font-display text-[18px] font-semibold tracking-tight text-[var(--color-foreground)]">
          Passo a <span className="text-[var(--color-primary)]">Frente</span>
        </span>
        <span className="font-mono text-[10px] font-medium uppercase tracking-wider text-[var(--color-muted-foreground)]">
          · operador
        </span>
      </div>
      <h1 className="font-display text-[clamp(3rem,7vw,5.5rem)] font-semibold leading-[0.95] tracking-[var(--tracking-display)]">
        Operador<span className="text-[var(--color-primary)]">.</span>
      </h1>
      <p className="max-w-md text-sm leading-relaxed text-[var(--color-muted-foreground)]">
        Clientes, unidades, contratos e estações do Passo a Frente, num lugar só.
      </p>
    </div>
  );
}
