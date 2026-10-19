import { useId, useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { chamarVip, listarFilasVip } from "@/features/paf/guiche/guiche-api";
import { RecusaGuiche } from "@/features/paf/guiche/guiche-fluxo";
import { hojeEmSaoPaulo, montarPedidoVip, podeChamarVip, SENHA_MAX } from "@/features/paf/guiche/vip";

const SELECT_CLASSES =
  "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

type Props = {
  /** True while another guichê action runs or an atendimento is in progress here (the server refuses it anyway). */
  bloqueado: boolean;
  /** The page's runner: shows the refusal and re-reads the post, so the called senha appears as the current one. */
  executar: (acao: () => Promise<void>) => Promise<void>;
};

/** RF19 "atendimento VIP": call a ficha by its printed senha, date and fila, out of the queue order. */
export function VipForm({ bloqueado, executar }: Props) {
  const { user } = useAuth();
  const permitido = podeChamarVip(user?.permissions ?? []);
  const [senha, setSenha] = useState("");
  const [data, setData] = useState(() => hojeEmSaoPaulo(new Date()));
  const [filaId, setFilaId] = useState("");
  const idSenha = useId();
  const idData = useId();
  const idFila = useId();
  const filas = useQuery({ queryKey: ["paf", "guiche", "vip", "filas"], queryFn: listarFilasVip, enabled: permitido });

  if (!permitido) return null;

  const enviar = (e: FormEvent) => {
    e.preventDefault();
    void executar(async () => {
      const resultado = montarPedidoVip(senha, data, filaId);
      if (!resultado.ok) throw new RecusaGuiche(resultado.erro);
      await chamarVip(resultado.pedido);
      setSenha("");
    });
  };

  return (
    <form
      onSubmit={enviar}
      aria-label="Atendimento VIP"
      className="space-y-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-card)] p-5"
    >
      <h2 className="font-semibold">Atendimento VIP</h2>
      {filas.isError && <p role="alert">{mensagemAmigavel(filas.error)}</p>}
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="space-y-1">
          <Label htmlFor={idSenha}>Senha</Label>
          <Input
            id={idSenha}
            value={senha}
            maxLength={SENHA_MAX}
            autoComplete="off"
            onChange={(e) => setSenha(e.target.value)}
          />
        </div>
        <div className="space-y-1">
          <Label htmlFor={idData}>Data da ficha</Label>
          <Input id={idData} type="date" value={data} onChange={(e) => setData(e.target.value)} />
        </div>
        <div className="space-y-1">
          <Label htmlFor={idFila}>Fila</Label>
          <select id={idFila} className={SELECT_CLASSES} value={filaId} onChange={(e) => setFilaId(e.target.value)}>
            <option value="">{filas.isPending ? "Carregando…" : "Selecione a fila"}</option>
            {(filas.data ?? []).map((f) => (
              <option key={f.id} value={f.id}>
                {f.nome}
              </option>
            ))}
          </select>
        </div>
      </div>
      <Button type="submit" disabled={bloqueado}>
        Chamar VIP
      </Button>
    </form>
  );
}
