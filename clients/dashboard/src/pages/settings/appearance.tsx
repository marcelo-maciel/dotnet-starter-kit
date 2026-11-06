import { Monitor, Moon, Sun } from "lucide-react";
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
  label: string;
  description: string;
  Icon: React.ComponentType<{ className?: string }>;
}> = [
  { value: "light", label: "Claro", description: "Fundo claro, confortável durante o dia.", Icon: Sun },
  { value: "system", label: "Sistema", description: "Segue a preferência do sistema operacional.", Icon: Monitor },
  { value: "dark", label: "Escuro", description: "Menos brilho para longas jornadas.", Icon: Moon },
];

export function AppearanceSettings() {
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
          <CardTitle>Tema</CardTitle>
          <CardDescription>
            Escolha o modo de cor do sistema. Sistema segue o seu sistema operacional.
          </CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3 px-6 pb-5 pt-1 sm:grid-cols-3">
          {themeOptions.map(({ value, label, description, Icon }) => {
            const active = mode === value;
            return (
              <SwatchButton
                key={value}
                active={active}
                onClick={() => setMode(value)}
                aria-pressed={active}
                aria-label={`Tema ${label}`}
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
                <SwatchSubtitle>{description}</SwatchSubtitle>
              </SwatchButton>
            );
          })}
        </CardContent>
      </Card>

      {/* Font — four selectable families */}
      <Card>
        <CardHeader>
          <CardTitle>Fonte</CardTitle>
          <CardDescription>
            A fonte da interface. Blocos de código usam sempre a JetBrains Mono.
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
          <CardTitle>Densidade</CardTitle>
          <CardDescription>
            O modo compacto reduz o espaçamento dos cartões e a altura das linhas nas telas com muitos dados.
          </CardDescription>
        </CardHeader>
        <CardContent className="flex items-center justify-between gap-4 px-6 pb-5 pt-1">
          <div className="text-sm text-[var(--color-muted-foreground)]">
            Usar espaçamento compacto em todo o sistema.
          </div>
          <Switch
            checked={density === "compact"}
            onCheckedChange={(checked) => setDensity(checked ? "compact" : "comfortable")}
            aria-label="Densidade compacta"
          />
        </CardContent>
      </Card>

      {/* Motion */}
      <Card>
        <CardHeader>
          <CardTitle>Animações</CardTitle>
          <CardDescription>
            Substitui a configuração{" "}
            <code className="rounded bg-[var(--color-muted)] px-1 font-mono text-[11px]">
              prefers-reduced-motion
            </code>{" "}
            do sistema.
          </CardDescription>
        </CardHeader>
        <CardContent className="flex items-center justify-between gap-4 px-6 pb-5 pt-1">
          <div className="text-sm text-[var(--color-muted-foreground)]">
            Desligar transições e animações decorativas.
          </div>
          <Switch
            checked={reducedMotion}
            onCheckedChange={setReducedMotion}
            aria-label="Reduzir animações"
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
  return (
    <span className="text-[10px] font-semibold uppercase tracking-wider text-[var(--color-primary)]">
      Ativo
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
  return (
    <SwatchButton
      active={active}
      onClick={onSelect}
      aria-pressed={active}
      aria-label={`Fonte ${option.label}`}
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
          Aa <span className="font-normal text-[var(--color-muted-foreground)]">0123</span>
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
