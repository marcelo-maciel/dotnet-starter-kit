import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { CalendarCog } from "lucide-react";
import { toast } from "sonner";
import { adjustTenantValidity } from "@/api/tenants";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Field } from "@/components/list";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { ApiRequestError, describeError } from "@/lib/api-client";
import { formatDate, startOfSaoPauloDay, toSaoPauloDay } from "@/lib/format";
import type { TFunction } from "i18next";

type TFn = TFunction<"tenants">;

// A `type="date"` input yields a `YYYY-MM-DD` string. zod validates the shape
// and that it parses to a real calendar date.
const makeSchema = (t: TFn) =>
  z.object({
    validUpto: z
      .string()
      .min(1, t("adjust.validation.pickDate"))
      .refine((v) => !Number.isNaN(new Date(v).getTime()), t("adjust.validation.invalidDate")),
  });

type FormValues = z.infer<ReturnType<typeof makeSchema>>;

function describe(err: unknown, fallback: string): string {
  if (err instanceof ApiRequestError) return describeError(err);
  if (err instanceof Error) return err.message;
  return fallback;
}

/**
 * Operator override that sets a tenant's ValidUpto directly with NO invoice —
 * a comp/correction, distinct from Renew (which issues a term invoice).
 * Backdating is permitted server-side. Root-operator only.
 */
export function AdjustValidityDialog({
  open,
  onOpenChange,
  tenantId,
  validUpto,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tenantId: string;
  validUpto?: string;
}) {
  const { t } = useTranslation("tenants");
  const queryClient = useQueryClient();
  const schema = useMemo(() => makeSchema(t), [t]);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { validUpto: "" },
  });

  // Prefill with the tenant's current validity each time the dialog opens: the Brasília day of the
  // stored instant, the same day every page shows.
  useEffect(() => {
    if (open) reset({ validUpto: toSaoPauloDay(validUpto) });
  }, [open, validUpto, reset]);

  const mutation = useMutation({
    // Pass the date via mutate(arg) — never close over form state at submit time.
    // The picked day means that day in Brasília: its 00:00 there, 03:00 UTC.
    mutationFn: (value: string) => adjustTenantValidity(tenantId, startOfSaoPauloDay(value)),
    onSuccess: (result) => {
      toast.success(t("adjust.toast.title"), {
        description: t("adjust.toast.description", { date: formatDate(result.validUpto) }),
      });
      queryClient.invalidateQueries({ queryKey: ["tenant", tenantId] });
      queryClient.invalidateQueries({ queryKey: ["tenants"] });
      handleClose();
    },
    onError: (err) => toast.error(t("adjust.toast.failed"), { description: describe(err, t("adjust.toast.failedFallback")) }),
  });

  function handleClose() {
    reset({ validUpto: "" });
    onOpenChange(false);
  }

  const onSubmit = handleSubmit((values) => mutation.mutate(values.validUpto));
  const submitting = isSubmitting || mutation.isPending;

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        if (!o) handleClose();
        else onOpenChange(true);
      }}
    >
      <DialogContent size="md">
        <DialogHeader>
          <div className="flex items-center gap-3">
            <span
              aria-hidden
              className="grid h-9 w-9 shrink-0 place-items-center rounded-xl
                bg-[oklch(from_var(--color-primary)_l_c_h_/_0.12)] text-[var(--color-primary)]
                ring-1 ring-inset ring-[oklch(from_var(--color-primary)_l_c_h_/_0.18)]"
            >
              <CalendarCog className="h-[18px] w-[18px]" />
            </span>
            <DialogTitle className="text-[16px]">{t("adjust.title")}</DialogTitle>
          </div>
          <DialogDescription className="mt-1">
            {t("adjust.description", { date: formatDate(validUpto) })}
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={onSubmit}>
          <DialogBody className="space-y-4">
            <Field
              id="av-validUpto"
              label={t("adjust.field.validUntil")}
              required
              hint={t("adjust.field.hint")}
              error={errors.validUpto?.message}
            >
              <Input id="av-validUpto" type="date" {...register("validUpto")} />
            </Field>
          </DialogBody>

          <DialogFooter>
            <Button type="button" variant="outline" onClick={handleClose} disabled={submitting}>
              {t("adjust.cancel")}
            </Button>
            <Button type="submit" disabled={submitting}>
              {submitting ? t("adjust.saving") : t("adjust.submit")}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
