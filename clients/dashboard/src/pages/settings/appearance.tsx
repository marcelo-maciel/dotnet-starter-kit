import { Monitor, Moon, Sun } from "lucide-react";
import type { ParseKeys } from "i18next";
import { useTranslation } from "react-i18next";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Switch } from "@/components/ui/switch";
import { useTheme, type ThemeMode } from "@/components/theme/theme-provider";
import { fonts, type FontOption } from "@/components/theme/appearance-options";
import { cn } from "@/lib/cn";

const themeOptions: Array<{
  value: ThemeMode;
  labelKey: ParseKeys<"settings">;
  descriptionKey: ParseKeys<"settings">;
  Icon: React.ComponentType<{ className?: string }>;
}> = [
  { value: "light", labelKey: "appearance.theme.light", descriptionKey: "appearance.theme.lightBlurb", Icon: Sun },
  { value: "system", labelKey: "appearance.theme.system", descriptionKey: "appearance.theme.systemBlurb", Icon: Monitor },
  { value: "dark", labelKey: "appearance.theme.dark", descriptionKey: "appearance.theme.darkBlurb", Icon: Moon },
];

export function AppearanceSettings() {
  const { t } = useTranslation("settings");
  const {
    mode, setMode,
    font, setFont,
    density, setDensity,
    reducedMotion, setReducedMotion,
  } = useTheme();

  return (
    <div className="space-y-6 fsh-enter">
      {/* Theme */}
      <Card>
        <CardHeader>
          <CardTitle>{t("appearance.theme.title")}</CardTitle>
          <CardDescription>
            {t("appearance.theme.description")}
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3 px-6 pb-5 pt-1 sm:grid-cols-3">
          {themeOptions.map(({ value, labelKey, descriptionKey, Icon }) => {
            const active = mode === value;
            const label = t(labelKey);
            return (
              <SwatchButton
                key={value}
                active={active}
                onClick={() => setMode(value)}
                aria-pressed={active}
                aria-label={t("appearance.theme.ariaLabel", { label })}
              >
                <div className="mb-3 flex items-center justify-between">
                  <Icon
                    className={cn(
                      "h-4 w-4",
                      active
                        ? "text-[var(--color-primary)]"
                        : "text-[var(--color-muted-foreground)]",
                    )}
                  />
                  {active && <ActiveTag />}
                </div>
                <SwatchTitle active={active}>{label}</SwatchTitle>
                <SwatchSubtitle>{t(descriptionKey)}</SwatchSubtitle>
              </SwatchButton>
            );
          })}
        </CardContent>
      </Card>

      {/* Font — four selectable families */}
      <Card>
        <CardHeader>
          <CardTitle>{t("appearance.font.title")}</CardTitle>
          <CardDescription>
            {t("appearance.font.description")}
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3 px-6 pb-5 pt-1 sm:grid-cols-2 lg:grid-cols-4">
          {fonts.map((f) => (
            <FontCard
              key={f.id}
              option={f}
              active={font === f.id}
              onSelect={() => setFont(f.id)}
            />
          ))}
        </CardContent>
      </Card>

      {/* Density */}
      <Card>
        <CardHeader>
          <CardTitle>{t("appearance.density.title")}</CardTitle>
          <CardDescription>
            {t("appearance.density.description")}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex items-center justify-between gap-4 px-6 pb-5 pt-1">
          <div className="text-sm text-[var(--color-muted-foreground)]">
            {t("appearance.density.toggle")}
          </div>
          <Switch
            checked={density === "compact"}
            onCheckedChange={(checked) => setDensity(checked ? "compact" : "comfortable")}
            aria-label={t("appearance.density.ariaLabel")}
          />
        </CardContent>
      </Card>

      {/* Motion */}
      <Card>
        <CardHeader>
          <CardTitle>{t("appearance.motion.title")}</CardTitle>
          <CardDescription>
            {t("appearance.motion.descPre")}{" "}
            <code className="rounded bg-[var(--color-muted)] px-1 font-mono text-[11px]">
              prefers-reduced-motion
            </code>{" "}
            {t("appearance.motion.descPost")}
          </CardDescription>
        </CardHeader>
        <CardContent className="flex items-center justify-between gap-4 px-6 pb-5 pt-1">
          <div className="text-sm text-[var(--color-muted-foreground)]">
            {t("appearance.motion.toggle")}
          </div>
          <Switch
            checked={reducedMotion}
            onCheckedChange={setReducedMotion}
            aria-label={t("appearance.motion.ariaLabel")}
          />
        </CardContent>
      </Card>
    </div>
  );
}

// ───────────────────────────────────────────────────────────────────────
// Building blocks
// ───────────────────────────────────────────────────────────────────────

type SwatchProps = React.ButtonHTMLAttributes<HTMLButtonElement> & {
  active: boolean;
};

function SwatchButton({ active, className, children, ...props }: SwatchProps) {
  return (
    <button
      type="button"
      className={cn(
        "group/swatch relative overflow-hidden rounded-xl border p-4 text-left",
        "transition-colors duration-[var(--duration-default)] ease-[var(--ease-out-cubic)]",
        "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--color-ring)] focus-visible:ring-offset-2",
        active
          ? "border-[var(--color-primary)] bg-[var(--color-primary-soft)]"
          : "border-[var(--color-border)] bg-[var(--color-card)] hover:bg-[var(--color-muted)]",
        className,
      )}
      {...props}
    >
      {children}
    </button>
  );
}

function ActiveTag() {
  const { t } = useTranslation("settings");
  return (
    <span className="text-[10px] font-semibold uppercase tracking-wider text-[var(--color-primary)]">
      {t("appearance.active")}
    </span>
  );
}

function SwatchTitle({ active, children }: { active: boolean; children: React.ReactNode }) {
  return (
    <div
      className={cn(
        "text-sm font-semibold tracking-tight",
        active && "text-[var(--color-primary)]",
      )}
    >
      {children}
    </div>
  );
}

function SwatchSubtitle({ children }: { children: React.ReactNode }) {
  return (
    <div className="mt-0.5 text-xs leading-relaxed text-[var(--color-muted-foreground)]">
      {children}
    </div>
  );
}

function FontCard({
  option,
  active,
  onSelect,
}: {
  option: FontOption;
  active: boolean;
  onSelect: () => void;
}) {
  const { t } = useTranslation("settings");
  return (
    <SwatchButton
      active={active}
      onClick={onSelect}
      aria-pressed={active}
      aria-label={t("appearance.font.ariaLabel", { label: option.label })}
      title={option.label}
    >
      {/* Live-preview sample rendered in the candidate font. */}
      <div
        className={cn(
          "mb-3 grid h-12 w-full place-items-center rounded-lg border",
          active
            ? "border-[var(--color-primary)] bg-[oklch(from_var(--color-primary)_l_c_h_/_0.06)]"
            : "border-[var(--color-border)] bg-[var(--color-muted)]",
        )}
        style={{ fontFamily: option.family }}
        aria-hidden
      >
        <span className="font-display text-2xl font-semibold tracking-tight">
          {t("appearance.font.sample")} <span className="font-normal text-[var(--color-muted-foreground)]">0123</span>
        </span>
      </div>
      <div className="flex items-center justify-between">
        <SwatchTitle active={active}>{option.label}</SwatchTitle>
        {active && <ActiveTag />}
      </div>
      <SwatchSubtitle>{option.description}</SwatchSubtitle>
    </SwatchButton>
  );
}
