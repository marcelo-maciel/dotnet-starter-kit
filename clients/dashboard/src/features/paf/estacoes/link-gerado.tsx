import { useId, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

/**
 * The freshly generated link, shown once: the token lives only in the parent's state and is gone when this closes,
 * because the server keeps only its hash.
 */
export function LinkGerado({
  estacao,
  link,
  avisos,
  onFechar,
}: {
  estacao: string;
  link: string;
  avisos: string[];
  onFechar: () => void;
}) {
  const id = useId();
  const [copia, setCopia] = useState<string | null>(null);

  const copiar = async () => {
    try {
      await navigator.clipboard.writeText(link);
      setCopia("Link copiado.");
    } catch {
      // Clipboard needs a secure context and the user's grant; the field stays selectable for a manual copy.
      setCopia("Não foi possível copiar: selecione o link e copie manualmente.");
    }
  };

  return (
    <section className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5" data-link-gerado>
      <h2 className="font-medium">Link da estação {estacao}</h2>
      <div className="space-y-1">
        <Label htmlFor={id}>Link de vínculo</Label>
        <Input id={id} readOnly value={link} onFocus={(e) => e.currentTarget.select()} />
      </div>
      <ul role="status" className="list-disc space-y-1 pl-5 text-sm">
        {avisos.map((a) => (
          <li key={a}>{a}</li>
        ))}
      </ul>
      {copia && (
        <p role="status" className="text-sm">
          {copia}
        </p>
      )}
      <div className="flex gap-2">
        <Button onClick={() => void copiar()}>Copiar</Button>
        <Button variant="outline" onClick={onFechar}>
          Fechar
        </Button>
      </div>
    </section>
  );
}
