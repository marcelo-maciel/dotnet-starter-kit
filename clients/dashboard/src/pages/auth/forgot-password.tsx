import { useState, type FormEvent } from "react";
import { Link, Navigate } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import {
  AlertCircle,
  ArrowRight,
  Building2,
  Check,
  Loader2,
  Mail,
  MailCheck,
} from "lucide-react";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { AuthHeadline, AuthShell } from "@/components/auth/auth-shell";
import { requestPasswordReset } from "@/api/identity";
import { ApiRequestError } from "@/lib/api-client";
import { cn } from "@/lib/cn";

/**
 * Forgot-password — step 1 of the reset flow. Collects (email, tenant)
 * and asks the server to email a one-time reset link to that address.
 *
 * Security note: the backend deliberately returns 200 even when the email
 * doesn't exist (no account-enumeration). The UI must NOT branch on the
 * server response to imply existence — always render the same "check your
 * inbox" success state after a 2xx.
 */
export function ForgotPasswordPage() {
  const { isAuthenticated } = useAuth();
  const [email, setEmail] = useState("");
  // Empty, not env.defaultTenant ("root"), as on the login: customers type their own company; root is the operator's.
  const [tenant, setTenant] = useState("");
  const [submitted, setSubmitted] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: () => requestPasswordReset({ email, tenant }),
    onSuccess: () => setSubmitted(true),
    onError: (err: unknown) => {
      // Most failures here are infra (tenant not resolvable, server down) —
      // we surface those plainly; account-existence is intentionally hidden
      // by the server's uniform 200 response.
      const detail =
        err instanceof ApiRequestError
          ? err.problem?.detail ?? err.problem?.title ?? err.message
          : (err as Error).message;
      setError(detail);
    },
  });

  if (isAuthenticated) {
    return <Navigate to="/" replace />;
  }

  const onSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setError(null);
    mutation.mutate();
  };

  return (
    <AuthShell
      footer={
        <span>
          Lembrou?{" "}
          <Link
            to="/login"
            className="text-[var(--color-foreground)] underline-offset-4 hover:underline"
          >
            Entrar
          </Link>
        </span>
      }
    >
      {submitted ? (
        <div className="fsh-enter space-y-5 text-center">
          <div className="grid place-items-center">
            <span
              aria-hidden
              className="grid size-14 place-items-center rounded-2xl bg-[oklch(from_var(--color-success)_l_c_h_/_0.10)] text-[var(--color-success)]"
            >
              <MailCheck className="size-6" />
            </span>
          </div>
          <div>
            <AuthHeadline lead="Confira sua" accent="caixa de entrada" />
            <p className="text-[13px] leading-relaxed text-[var(--color-muted-foreground)]">
              Se existir uma conta para{" "}
              <span className="text-[var(--color-foreground)]">{email}</span> na
              empresa{" "}
              <span className="text-[var(--color-foreground)]">{tenant}</span>,
              um link de uso único para redefinir a senha está a caminho. O link expira em 30 minutos.
            </p>
          </div>
          <ul className="space-y-1.5 text-left text-[12.5px] text-[var(--color-muted-foreground)]">
            <li className="flex items-start gap-2">
              <Check className="mt-0.5 size-3.5 shrink-0 text-[var(--color-success)]" />
              Não chegou? Espere um minuto e confira a pasta de spam.
            </li>
            <li className="flex items-start gap-2">
              <Check className="mt-0.5 size-3.5 shrink-0 text-[var(--color-success)]" />
              Ainda nada? Confira o e-mail e a empresa e tente de novo.
            </li>
          </ul>
          <div className="flex items-center gap-2 pt-1">
            <Button
              type="button"
              variant="ghost"
              onClick={() => {
                setSubmitted(false);
                setError(null);
              }}
            >
              Tentar outro endereço
            </Button>
            <Link to="/login" className="ml-auto">
              <Button type="button" variant="outline">
                Voltar para a entrada
              </Button>
            </Link>
          </div>
        </div>
      ) : (
        <>
          <div className="mb-6 sm:mb-8">
            <AuthHeadline lead="Recupere sua" accent="senha" />
            <p className="text-[13px] text-[var(--color-muted-foreground)]">
              Informe o e-mail que você usa para entrar. Vamos enviar um link de uso único.
            </p>
          </div>

          <form onSubmit={onSubmit} className="space-y-5" noValidate aria-describedby={error ? "forgot-error" : undefined}>
            <div className="space-y-1.5">
              <Label
                htmlFor="reset-tenant"
                className="block text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
              >
                Empresa
              </Label>
              <div className="relative">
                <Building2 className="pointer-events-none absolute left-3.5 top-1/2 size-4 -translate-y-1/2 text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.6)]" />
                <Input
                  id="reset-tenant"
                  value={tenant}
                  onChange={(e) => setTenant(e.target.value)}
                  placeholder="identificador da empresa"
                  autoComplete="organization"
                  required
                  aria-invalid={error ? true : undefined}
                  aria-describedby={error ? "forgot-error" : undefined}
                  className="h-11 pl-10 text-[14px]"
                />
              </div>
            </div>

            <div className="space-y-1.5">
              <Label
                htmlFor="reset-email"
                className="block text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
              >
                E-mail
              </Label>
              <div className="relative">
                <Mail className="pointer-events-none absolute left-3.5 top-1/2 size-4 -translate-y-1/2 text-[oklch(from_var(--color-muted-foreground)_l_c_h_/_0.6)]" />
                <Input
                  id="reset-email"
                  type="email"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="nome@empresa.com.br"
                  autoComplete="email"
                  required
                  autoFocus
                  aria-invalid={error ? true : undefined}
                  aria-describedby={error ? "forgot-error" : undefined}
                  className="h-11 pl-10 text-[14px]"
                />
              </div>
            </div>

            {error && (
              <div
                id="forgot-error"
                role="alert"
                className={cn(
                  "fsh-enter flex items-start gap-2 rounded-lg border px-3 py-2 text-sm",
                  "border-[oklch(from_var(--color-destructive)_l_c_h_/_0.30)]",
                  "bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.06)]",
                  "text-[var(--color-destructive)]",
                )}
              >
                <AlertCircle className="mt-0.5 size-4 shrink-0" />
                <span className="leading-snug">{error}</span>
              </div>
            )}

            <div className="pt-1.5">
              <Button
                type="submit"
                disabled={mutation.isPending || !email || !tenant}
                className="group h-11 w-full text-[14px] font-semibold"
              >
                {mutation.isPending ? (
                  <>
                    <Loader2 className="size-4 animate-spin" />
                    <span>Enviando o link…</span>
                  </>
                ) : (
                  <>
                    <span>Enviar link de redefinição</span>
                    <ArrowRight className="size-[14px] opacity-60 transition-all duration-200 group-hover:translate-x-0.5 group-hover:opacity-100" />
                  </>
                )}
              </Button>
            </div>
          </form>
        </>
      )}
    </AuthShell>
  );
}
