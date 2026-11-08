import { useEffect, useRef, useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Fingerprint, UserCircle2 } from "lucide-react";
import { toast } from "sonner";
import { useAuth } from "@/auth/use-auth";
import { getMyProfileWithETag, updateMyProfile } from "@/api/identity";
import type { UserDto } from "@/api/identity";
import { ApiRequestError } from "@/lib/api-client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { SettingsSection } from "@/pages/settings/settings-layout";

const PROFILE_KEY = ["identity", "me"] as const;

export function ProfileSettings() {
  const { user } = useAuth();
  const queryClient = useQueryClient();

  const profileQuery = useQuery({
    queryKey: PROFILE_KEY,
    queryFn: getMyProfileWithETag,
  });

  const profile = profileQuery.data?.profile;
  const loading = profileQuery.isLoading;

  // The version this form is editing against, captured when the form is seeded. Deliberately a
  // ref and not `profileQuery.data`: a background refetch would otherwise move it to a version
  // the user never saw, and the save would carry an If-Match that matches whatever someone else
  // just wrote — silently overwriting it, which is exactly what the ETag exists to prevent. It
  // moves only on a deliberate step: a successful save, or the user being told about a conflict.
  const editingVersionRef = useRef<{ profile: UserDto; etag: string | null } | null>(null);
  const [firstName, setFirstName] = useState("");
  const [lastName, setLastName] = useState("");
  const [phone, setPhone] = useState("");

  // Seed the form from the fetched profile exactly once. The JWT-derived
  // `user` provides an immediate non-empty paint; the authoritative profile
  // then seeds and locks. Seeding once means a later background refetch
  // can't clobber edits the user has already made.
  const seededRef = useRef(false);
  useEffect(() => {
    if (seededRef.current) return;
    if (profileQuery.data) {
      const { profile: seeded } = profileQuery.data;
      setFirstName(seeded.firstName ?? "");
      setLastName(seeded.lastName ?? "");
      setPhone(seeded.phoneNumber ?? "");
      editingVersionRef.current = profileQuery.data;
      seededRef.current = true;
    } else if (user && loading) {
      setFirstName(user.name?.split(" ")[0] ?? "");
      setLastName(user.name?.split(" ").slice(1).join(" ") ?? "");
    }
  }, [profileQuery.data, user, loading]);

  // Re-reads the profile and adopts it as the version the form edits against, so the next save
  // carries a tag the server will accept.
  const adoptCurrentVersion = async () => {
    const fresh = await queryClient.fetchQuery({
      queryKey: PROFILE_KEY,
      queryFn: getMyProfileWithETag,
      // Must reach the network. The client's default staleTime would hand back the cached copy,
      // and the cached copy carries the very tag the server just rejected.
      staleTime: 0,
    });
    editingVersionRef.current = fresh;
    return fresh;
  };

  const saveMutation = useMutation({
    mutationFn: updateMyProfile,
    onSuccess: async () => {
      toast.success("Perfil salvo");
      // The save moved the profile on, so the tag the form holds is spent: adopt the new one or
      // a second save in the same sitting would 412 against the user's own write. Awaited rather
      // than fire-and-forget: isPending has to stay true until the new tag is in hand, or the
      // button re-enables over a spent one and a quick second click 412s against the user's own
      // save. It also keeps a failed refetch from becoming an unhandled rejection that would
      // strand the form on a tag the server has already rejected.
      await adoptCurrentVersion();
    },
    onError: async (err: unknown) => {
      // 412 means someone else wrote the profile after this form was seeded. Do NOT resend: the
      // only body available is the one typed against the old values, and pushing it through
      // against a fresh tag performs the overwrite the 412 just prevented. Keep the user's
      // edits on screen, adopt the current version, and let them decide whether to save again.
      if (err instanceof ApiRequestError && err.status === 412) {
        await adoptCurrentVersion();
        toast.warning("Perfil alterado em outro lugar", {
          description:
            "Alguém atualizou este perfil enquanto você editava. Revise suas alterações e salve de novo para aplicá-las.",
        });
        return;
      }

      const message =
        err instanceof ApiRequestError
          ? err.problem?.detail ?? err.problem?.title ?? err.message
          : "Falha ao salvar o perfil";
      toast.error("Falha ao salvar", { description: message });
    },
  });

  const onSubmit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const editing = editingVersionRef.current;
    if (!editing) return;
    // Everything the save needs travels through mutate(), never through state the callbacks
    // close over: the values that go out must be the ones on screen when the button was pressed.
    saveMutation.mutate({
      profile: editing.profile,
      expectedETag: editing.etag,
      firstName: firstName.trim() || null,
      lastName: lastName.trim() || null,
      // "" (not null) when the field was emptied: updateMyProfile reads null as "keep the stored
      // value", so null here made a phone number impossible to remove.
      phoneNumber: phone.trim(),
    });
  };

  const onReset = () => {
    if (profile) {
      setFirstName(profile.firstName ?? "");
      setLastName(profile.lastName ?? "");
      setPhone(profile.phoneNumber ?? "");
    }
  };

  const saving = saveMutation.isPending;
  const dirty =
    (profile?.firstName ?? "") !== firstName ||
    (profile?.lastName ?? "") !== lastName ||
    (profile?.phoneNumber ?? "") !== phone;
  // A save carries the unedited fields and the version tag off the profile read, so until that
  // read lands there is nothing to save against. Disabled rather than silently doing nothing.
  const canSave = profileQuery.isSuccess;

  return (
    <form onSubmit={onSubmit} className="space-y-5 fsh-enter">
      {profileQuery.isError && (
        <div
          role="alert"
          className="flex items-start gap-2 rounded-lg border border-[oklch(from_var(--color-destructive)_l_c_h_/_0.30)] bg-[oklch(from_var(--color-destructive)_l_c_h_/_0.06)] px-3 py-2 text-[13px] text-[var(--color-destructive)]"
        >
          <span>
            Não foi possível carregar seu perfil. Os dados mostrados vêm da sua sessão; salvar fica
            desligado até o perfil carregar, porque o salvamento precisa levar a versão lida.
          </span>
        </div>
      )}
      {/* No photo section: the PAF never shows the profile photo, and the URL an upload stores points at R2's S3 API
          endpoint, which has no anonymous read (decided 08/11/2026). The topbar falls back to the initials. */}
      <SettingsSection
        title="Identificação"
        icon={UserCircle2}
        description="Seu nome e seus dados de contato, visíveis em todo o sistema."
        footer={
          <div className="flex items-center justify-end gap-2">
            <Button
              type="button"
              variant="ghost"
              onClick={onReset}
              disabled={saving || !dirty || !canSave}
              size="sm"
            >
              Desfazer
            </Button>
            <Button type="submit" disabled={saving || !dirty || !canSave} size="sm">
              {saving ? "Salvando…" : "Salvar alterações"}
            </Button>
          </div>
        }
      >
        <div className="grid gap-5 sm:grid-cols-2">
          <Field id="first-name" label="Nome">
            <Input
              id="first-name"
              value={firstName}
              onChange={(e) => setFirstName(e.target.value)}
              autoComplete="given-name"
              disabled={loading}
              className="h-10 text-[13px]"
            />
          </Field>
          <Field id="last-name" label="Sobrenome">
            <Input
              id="last-name"
              value={lastName}
              onChange={(e) => setLastName(e.target.value)}
              autoComplete="family-name"
              disabled={loading}
              className="h-10 text-[13px]"
            />
          </Field>
          <Field id="email" label="E-mail">
            <Input
              id="email"
              type="email"
              value={profile?.email ?? user?.email ?? ""}
              readOnly
              disabled
              className="h-10 cursor-not-allowed bg-[var(--color-muted)] text-[13px]"
            />
            <p className="mt-1 text-[11px] text-[var(--color-muted-foreground)]">
              Fale com o administrador da empresa para trocar o e-mail de entrada.
            </p>
          </Field>
          <Field id="phone" label="Telefone">
            <Input
              id="phone"
              type="tel"
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
              autoComplete="tel"
              placeholder="+1 (555) 123-4567"
              disabled={loading}
              className="h-10 text-[13px]"
            />
          </Field>
        </div>
      </SettingsSection>

      <SettingsSection
        title="Identificador da conta"
        icon={Fingerprint}
        description="O ID único desta conta dentro do sistema. Somente leitura."
      >
        <code className="block w-full overflow-x-auto rounded-lg border border-[var(--color-border)] bg-[var(--color-muted)] px-3 py-2 font-mono text-xs">
          {profile?.id ?? user?.id ?? "—"}
        </code>
      </SettingsSection>
    </form>
  );
}

function Field({
  id,
  label,
  children,
}: {
  id: string;
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <Label
        htmlFor={id}
        className="mb-1.5 block text-[11.5px] font-semibold uppercase tracking-wider text-[var(--color-muted-foreground)]"
      >
        {label}
      </Label>
      {children}
    </div>
  );
}
