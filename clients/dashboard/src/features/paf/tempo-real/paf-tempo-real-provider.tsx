import { useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from "react";
import { toast } from "sonner";
import { getMyPermissions } from "@/api/identity";
import { decodeJwt } from "@/auth/jwt";
import { tokenStore } from "@/auth/token-store";
import { env } from "@/env";
import { conectarPafHub } from "@/features/paf/paf-hub";
import {
  avisoDeOutraAba,
  chaveConexaoPafHub,
  lerAvisoTrocaUnidade,
  type AvisoTrocaUnidade,
  type ClaimsConexao,
} from "@/features/paf/tempo-real/conexao-sessao";
import { guardarAvisoParaRecarga, PafTempoRealContexto } from "@/features/paf/tempo-real/paf-tempo-real-contexto";
import { SESSAO_INVALIDADA, tratarSessaoInvalidada } from "@/features/paf/tempo-real/sessao-invalidada";
import { useAuth } from "@/auth/use-auth";

// Every authenticated screen keeps one PafHub connection (REQ RF22 "toda tela aberta mantém a conexão"): the
// connection is what renews the session's licence mark (RenovacaoPelaConexao), so a kit screen left open does
// not release the seat. Mounted around AppShell in routes.tsx (ADR 0008, amended 17/10/2026).

const CANAL_TROCA_UNIDADE = "paf.unidade-corrente";

function lerClaims(): ClaimsConexao | null {
  return decodeJwt(tokenStore.getAccessToken()) as ClaimsConexao | null;
}

function lerChaveConexao(): string | null {
  return chaveConexaoPafHub(lerClaims());
}

export function PafTempoRealProvider({ children }: { children: ReactNode }) {
  const chave = useSyncExternalStore(tokenStore.subscribe, lerChaveConexao);
  // Bumped to rebuild the connection after a unit switch: the server joins the unit group on connect only.
  const [epoca, setEpoca] = useState(0);
  const [abaId] = useState(() => crypto.randomUUID());
  const { logoutLocal } = useAuth();

  useEffect(() => {
    if (chave === null) return;
    const conexao = conectarPafHub({
      tenant: () => tokenStore.getTenant() ?? env.defaultTenant,
      obterToken: () => tokenStore.getAccessToken() ?? "",
      // The kit renews the token only on a 401 of apiFetch, which also deduplicates the refresh; calling
      // refreshAccessToken directly would race that rotation. A screen with no request would otherwise keep an
      // expired token and drop the connection, and the licence with it, after the reconnection grace.
      renovarToken: async () => {
        await getMyPermissions();
      },
      // Session loss is the kit's to handle (logout redirect); the loop keeps retrying until the shell unmounts.
      credencialPerdida: () => false,
      handlers: {
        // The server closes the connection right after this (S04): show why instead of a silent drop, and leave.
        // The session is already revoked, so the exit is local: a server logout would carry a dead token (401).
        [SESSAO_INVALIDADA]: (payload) =>
          tratarSessaoInvalidada(payload, lerClaims()?.sid, {
            pararConexao: () => conexao.parar(),
            avisar: (motivo) => toast.error(motivo),
            sairLocalmente: logoutLocal,
          }),
      },
      aoConectar: () => undefined,
      aoMudarEstado: () => undefined,
      aoPerderCredencial: () => undefined,
    });
    return () => conexao.parar();
  }, [chave, epoca, logoutLocal]);

  useEffect(() => {
    if (chave === null) return;
    // ponytail: this browser's tabs only. S07 (wave 3) sends the switch through the user's hub group, which
    // also reaches other devices; this channel then goes.
    const canal = new BroadcastChannel(CANAL_TROCA_UNIDADE);
    canal.onmessage = (evento: MessageEvent<unknown>) => {
      const aviso = lerAvisoTrocaUnidade(evento.data);
      const claims = lerClaims();
      if (!aviso || !claims?.sub || !claims.tenant) return;
      if (!avisoDeOutraAba(aviso, { tenant: claims.tenant, usuarioId: claims.sub, abaId })) return;
      const texto = `A unidade corrente foi trocada para ${aviso.unidade} em outra aba.`;
      // A PAF screen shows the old unit's data and may hold a stale step: reload it, notice included (RF08).
      if (window.location.pathname.startsWith("/paf")) {
        guardarAvisoParaRecarga(texto);
        window.location.reload();
        return;
      }
      toast.info(texto);
      setEpoca((n) => n + 1);
    };
    return () => canal.close();
  }, [chave, abaId]);

  const valor = useMemo(
    () => ({
      unidadeTrocada: (unidadeId: string, unidade: string) => {
        setEpoca((n) => n + 1);
        const claims = lerClaims();
        if (!claims?.sub || !claims.tenant) return;
        const aviso: AvisoTrocaUnidade = { tenant: claims.tenant, usuarioId: claims.sub, abaId, unidadeId, unidade };
        const canal = new BroadcastChannel(CANAL_TROCA_UNIDADE);
        canal.postMessage(aviso);
        canal.close();
      },
    }),
    [abaId],
  );

  return <PafTempoRealContexto.Provider value={valor}>{children}</PafTempoRealContexto.Provider>;
}
