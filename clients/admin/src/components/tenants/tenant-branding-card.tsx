import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Palette, RotateCcw, Save } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { ErrorBand, LoadingRow, SettingsSection } from "@/components/list";
import {
  DEFAULT_DARK_PALETTE,
  DEFAULT_LIGHT_PALETTE,
  getTenantTheme,
  resetTenantTheme,
  updateTenantTheme,
  type PaletteDto,
  type TenantThemeDto,
} from "@/api/tenants";
import { ApiRequestError, describeError } from "@/lib/api-client";
import { cn } from "@/lib/cn";

/**
 * TenantBrandingCard — operator-facing theme editor for a single tenant.
 *
 * Scopes every API call to `tenantId` via the `tenant:` header override.
 * The endpoints are current-tenant-scoped server-side, so the operator
 * has to explicitly declare which tenant they're targeting — only root
 * operators get past the override middleware.
 *
 * Scope: palette (light + dark) + brand asset URLs. Typography and layout
 * fields exist on the server-side DTO but are intentionally omitted from
 * the v1 editor — they would bloat the page beyond the audit's scope and
 * are rarely tweaked in practice. Wire them up here if the need lands.
 */
export function TenantBrandingCard({ tenantId }: { tenantId: string }) {
  const queryClient = useQueryClient();

  const themeQueryKey = useMemo(
    () => ["tenant", tenantId, "theme"] as const,
    [tenantId],
  );

  const themeQuery = useQuery({
    queryKey: themeQueryKey,
    queryFn: () => getTenantTheme(tenantId),
    // Re-fetch when the page regains focus so other admins' edits are
    // picked up without a manual refresh.
    refetchOnWindowFocus: true,
  });

  const [draft, setDraft] = useState<TenantThemeDto | null>(null);

  // Seed draft state when the server payload arrives. We always replace
  // the draft on a fresh fetch so server-driven changes (other admin's
  // edits, reset action) are reflected in the editor.
  useEffect(() => {
    if (themeQuery.data) {
      setDraft(themeQuery.data);
    }
  }, [themeQuery.data]);

  const saveMutation = useMutation({
    mutationFn: (theme: TenantThemeDto) => updateTenantTheme(tenantId, theme),
    onSuccess: () => {
      toast.success("Branding saved");
      void queryClient.invalidateQueries({ queryKey: themeQueryKey });
    },
    onError: (err) =>
      toast.error("Save failed", { description: apiErr(err) }),
  });

  const resetMutation = useMutation({
    mutationFn: () => resetTenantTheme(tenantId),
    onSuccess: () => {
      toast.success("Branding reset to defaults");
      void queryClient.invalidateQueries({ queryKey: themeQueryKey });
    },
    onError: (err) =>
      toast.error("Reset failed", { description: apiErr(err) }),
  });

  if (themeQuery.isLoading) {
    return (
      <SettingsSection
        title="Branding"
        icon={Palette}
        description="Operator-controlled colors + brand assets that drive this tenant's UI."
      >
        <LoadingRow label="Loading branding" />
      </SettingsSection>
    );
  }

  if (themeQuery.isError) {
    return (
      <SettingsSection title="Branding" icon={Palette}>
        <ErrorBand message={apiErr(themeQuery.error)} />
      </SettingsSection>
    );
  }

  if (!draft) return null;

  const dirty =
    themeQuery.data && JSON.stringify(themeQuery.data) !== JSON.stringify(draft);

  const onLight = (next: Partial<PaletteDto>) =>
    setDraft((d) => (d ? { ...d, lightPalette: { ...d.lightPalette, ...next } } : d));
  const onDark = (next: Partial<PaletteDto>) =>
    setDraft((d) => (d ? { ...d, darkPalette: { ...d.darkPalette, ...next } } : d));

  const footer = (
    <div className="flex flex-wrap items-center justify-between gap-2">
      <div className="flex items-center gap-2">
        {draft.isDefault && !dirty && (
          <Badge variant="outline" className="font-mono uppercase tracking-[0.14em]">
            default
          </Badge>
        )}
        {dirty && (
          <Badge variant="warning" className="font-mono uppercase tracking-[0.14em]">
            unsaved
          </Badge>
        )}
      </div>
      <div className="flex items-center gap-2">
        <Button
          type="button"
          variant="ghost"
          onClick={() => resetMutation.mutate()}
          disabled={resetMutation.isPending || saveMutation.isPending}
          aria-label="Reset branding to defaults"
        >
          <RotateCcw className="mr-1.5 h-3.5 w-3.5" />
          {resetMutation.isPending ? "Resetting…" : "Reset to defaults"}
        </Button>
        <Button
          type="button"
          variant="signal"
          onClick={() => draft && saveMutation.mutate(draft)}
          disabled={!dirty || saveMutation.isPending}
        >
          {saveMutation.isPending ? (
            <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />
          ) : (
            <Save className="mr-1.5 h-3.5 w-3.5" />
          )}
          {saveMutation.isPending ? "Saving…" : "Save branding"}
        </Button>
      </div>
    </div>
  );

  return (
    <SettingsSection
      title="Branding"
      icon={Palette}
      description="Theme tokens consumed by this tenant's apps on sign-in. Live preview reflects the primary action with the chosen palette."
      footer={footer}
    >
      <div className="space-y-6">
        <ThemePreview palette={draft.lightPalette} label="Light preview" />

        <div className="grid gap-5 lg:grid-cols-2">
          <PaletteEditor
            title="Light palette"
            palette={draft.lightPalette}
            onChange={onLight}
            defaults={DEFAULT_LIGHT_PALETTE}
          />
          <PaletteEditor
            title="Dark palette"
            palette={draft.darkPalette}
            onChange={onDark}
            defaults={DEFAULT_DARK_PALETTE}
          />
        </div>

        {/* No brand assets editor (logo, dark logo, favicon): the PAF uses neither, and an uploaded file's URL points
            at R2's S3 API endpoint, which has no anonymous read (decided 08/11/2026). The saved assets go back as read. */}
      </div>
    </SettingsSection>
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Palette editor — color swatches paired with hex inputs
// ─────────────────────────────────────────────────────────────────────────

const PALETTE_FIELDS: ReadonlyArray<{ key: keyof PaletteDto; label: string }> = [
  { key: "primary", label: "Primary" },
  { key: "secondary", label: "Secondary" },
  { key: "tertiary", label: "Tertiary" },
  { key: "background", label: "Background" },
  { key: "surface", label: "Surface" },
  { key: "error", label: "Error" },
  { key: "warning", label: "Warning" },
  { key: "success", label: "Success" },
  { key: "info", label: "Info" },
];

function PaletteEditor({
  title,
  palette,
  onChange,
  defaults,
}: {
  title: string;
  palette: PaletteDto;
  onChange: (next: Partial<PaletteDto>) => void;
  defaults: PaletteDto;
}) {
  return (
    <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-2)]">
      <div className="flex items-center justify-between border-b border-[oklch(from_var(--color-border)_l_c_h_/_0.5)] px-4 py-2.5">
        <h4 className="text-[12.5px] font-semibold tracking-tight text-[var(--color-foreground)]">
          {title}
        </h4>
        <button
          type="button"
          className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-[11px] font-medium text-[var(--color-muted-foreground)] transition-colors hover:bg-[var(--color-muted)] hover:text-[var(--color-foreground)]"
          onClick={() => onChange(defaults)}
        >
          <RotateCcw className="h-2.5 w-2.5" aria-hidden />
          Reset palette
        </button>
      </div>
      <div className="grid gap-2 p-4 sm:grid-cols-2">
        {PALETTE_FIELDS.map(({ key, label }) => (
          <ColorRow
            key={key}
            label={label}
            value={palette[key]}
            onChange={(v) => onChange({ [key]: v } as Partial<PaletteDto>)}
          />
        ))}
      </div>
    </div>
  );
}

function ColorRow({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (next: string) => void;
}) {
  const valid = /^#[0-9a-f]{6}$/i.test(value);
  return (
    <div className="flex items-center gap-2.5">
      {/* Color chip — clicking opens the native color picker */}
      <label
        className="relative grid h-8 w-8 shrink-0 cursor-pointer place-items-center overflow-hidden rounded-lg shadow-sm ring-1 ring-inset ring-[var(--color-border)]"
        style={{ backgroundColor: valid ? value : undefined }}
        title={`Pick ${label} color`}
      >
        <input
          type="color"
          value={valid ? value : "#000000"}
          onChange={(e) => onChange(e.target.value.toUpperCase())}
          className="sr-only"
          aria-label={`${label} color`}
        />
      </label>
      <div className="min-w-0 flex-1">
        <div className="mb-0.5 text-[10px] font-semibold uppercase tracking-[0.12em] text-[var(--color-muted-foreground)]">
          {label}
        </div>
        <Input
          value={value}
          onChange={(e) => onChange(e.target.value.toUpperCase())}
          spellCheck={false}
          autoComplete="off"
          maxLength={9}
          className={cn(
            "h-7 px-2 font-mono text-[11.5px]",
            !valid && "border-[var(--color-destructive)]/60 focus-visible:ring-[var(--color-destructive)]/40",
          )}
        />
      </div>
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Live preview — shows how primary-action buttons + surface tokens render
// ─────────────────────────────────────────────────────────────────────────

function ThemePreview({ palette, label }: { palette: PaletteDto; label: string }) {
  return (
    <div
      className="overflow-hidden rounded-xl border border-[var(--color-border)]"
      style={{ backgroundColor: palette.background }}
    >
      {/* Preview header bar */}
      <div
        className="flex items-center justify-between border-b px-4 py-2"
        style={{
          borderColor: `${palette.surface}55`,
          backgroundColor: palette.surface,
        }}
      >
        <span
          className="text-[11px] font-semibold uppercase tracking-[0.12em] opacity-60"
          style={{ color: palette.secondary }}
        >
          {label}
        </span>
        <span
          className="rounded-full px-2 py-0.5 text-[10px] font-medium uppercase tracking-[0.1em]"
          style={{ backgroundColor: palette.success, color: palette.surface }}
        >
          live
        </span>
      </div>

      {/* Preview body */}
      <div className="p-4">
        <div
          className="rounded-xl p-4"
          style={{ backgroundColor: palette.surface }}
        >
          <div className="mb-3 flex items-center justify-between gap-2">
            <span
              className="text-[13px] font-semibold"
              style={{ color: palette.secondary }}
            >
              Sample tenant page
            </span>
            <span
              className="rounded-full px-2 py-0.5 text-[10px] font-medium uppercase tracking-[0.1em]"
              style={{ backgroundColor: palette.success, color: palette.surface }}
            >
              active
            </span>
          </div>
          <p
            className="mb-4 text-[12.5px] leading-relaxed"
            style={{ color: palette.secondary, opacity: 0.72 }}
          >
            A short paragraph rendered with the chosen body color over the chosen
            surface, on the chosen page background. Action buttons use the primary token.
          </p>
          <div className="flex flex-wrap gap-2">
            {/* Primary action */}
            <span
              className="inline-flex items-center rounded-lg px-3 py-1.5 text-xs font-medium shadow-sm"
              style={{ backgroundColor: palette.primary, color: palette.surface }}
            >
              Primary action
            </span>
            {/* Outline secondary */}
            <span
              className="inline-flex items-center rounded-lg border px-3 py-1.5 text-xs font-medium"
              style={{
                borderColor: palette.primary,
                color: palette.primary,
                backgroundColor: "transparent",
              }}
            >
              Secondary
            </span>
            {/* Warning pill */}
            <span
              className="inline-flex items-center rounded-lg px-2.5 py-1 text-[10.5px] font-mono font-medium uppercase tracking-[0.1em]"
              style={{ backgroundColor: palette.warning, color: palette.background }}
            >
              warn
            </span>
            {/* Error pill */}
            <span
              className="inline-flex items-center rounded-lg px-2.5 py-1 text-[10.5px] font-mono font-medium uppercase tracking-[0.1em]"
              style={{ backgroundColor: palette.error, color: palette.surface }}
            >
              error
            </span>
          </div>
        </div>
      </div>
    </div>
  );
}

function apiErr(err: unknown): string {
  if (err instanceof ApiRequestError) {
    return describeError(err);
  }
  if (err instanceof Error) return err.message;
  return "Unknown error";
}
