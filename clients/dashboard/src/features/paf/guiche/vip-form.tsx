import { useId, useState, type FormEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { useAuth } from "@/auth/use-auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { mensagemAmigavel } from "@/features/paf/paf-erros";
import { formatarInstante } from "@/features/paf/consultar-ficha/consultar-ficha-regras";
import { buscarFichasVip, chamarVip, listarFilasVip, type FichaVipDto } from "@/features/paf/guiche/guiche-api";
import { RecusaGuiche } from "@/features/paf/guiche/guiche-fluxo";
import {
  decidirVip,
  hojeEmSaoPaulo,
  MENSAGEM_NENHUMA_FICHA,
  montarPedidoVip,
  podeChamarVip,
  SENHA_MAX,
} from "@/features/paf/guiche/vip";

const SELECT_CLASSES =
  "h-9 w-full rounded-md border border-[var(--color-input)] bg-[var(--color-card)] px-3 text-sm";

type Props = {
  /** True while another guichê action runs or an atendimento is in progress here (the server refuses it anyway). */
  bloqueado: boolean;
  /** The page's runner: shows the refusal and re-reads the post, so the called senha appears as the current one. */
  executar: (acao: () => Promise<void>) => Promise<void>;
};

/** RF19 "atendimento VIP": search the printed senha and date, then call the ficha (picked when several match) in the fila. */
export function VipForm({ bloqueado, executar }: Props) {
  const { user } = useAuth();
  const permitido = podeChamarVip(user?.permissions ?? []);
  const [senha, setSenha] = useState("");
  const [data, setData] = useState(() => hojeEmSaoPaulo(new Date()));
  const [filaId, setFilaId] = useState("");
  // Two or more fichas print the same senha: the list waits for the pick, with the fila of the search that found it.
  const [escolha, setEscolha] = useState<{ fichas: FichaVipDto[]; filaId: string } | null>(null);
  const idSenha = useId();
  const idData = useId();
  const idFila = useId();
  const filas = useQuery({ queryKey: ["paf", "guiche", "vip", "filas"], queryFn: listarFilasVip, enabled: permitido });

  if (!permitido) return null;

  const enviar = (e: FormEvent) => {
    e.preventDefault();
    void executar(async () => {
      setEscolha(null);
      const resultado = montarPedidoVip(senha, data, filaId);
      if (!resultado.ok) throw new RecusaGuiche(resultado.erro);
      const { pedido } = resultado;
      const decisao = decidirVip(await buscarFichasVip(pedido.senha, pedido.data));
      if (decisao.tipo === "nenhuma") throw new RecusaGuiche(MENSAGEM_NENHUMA_FICHA);
      if (decisao.tipo === "escolher") {
        setEscolha({ fichas: decisao.fichas, filaId: pedido.filaId });
        return;
      }
      await chamarVip({ fichaId: decisao.ficha.fichaId, filaId: pedido.filaId });
      setSenha("");
    });
  };

  const chamarEscolhida = (fichaId: string, filaDaBusca: string) => {
    void executar(async () => {
      await chamarVip({ fichaId, filaId: filaDaBusca });
      setEscolha(null);
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
      {escolha && (
        <div className="space-y-2" aria-live="polite">
          <p className="text-sm">Mais de uma ficha com esta senha nesta data. Escolha qual chamar:</p>
          <ul className="divide-y divide-[var(--color-border)]" aria-label="Fichas com esta senha">
            {escolha.fichas.map((f) => (
              <li key={f.fichaId} className="flex flex-wrap items-center gap-3 py-2">
                <span className="font-medium">{f.senha}</span>
                <span className="text-sm">
                  {f.fila} · {f.prioridade} · emitida em {formatarInstante(f.emitidaEm)}
                </span>
                <Button type="button" size="sm" disabled={bloqueado} onClick={() => chamarEscolhida(f.fichaId, escolha.filaId)}>
                  Chamar esta
                </Button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </form>
  );
}
