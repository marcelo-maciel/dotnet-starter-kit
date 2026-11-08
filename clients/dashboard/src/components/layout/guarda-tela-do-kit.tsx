import type { ReactNode } from "react";
import { useLocation } from "react-router-dom";
import { useAuth } from "@/auth/use-auth";
import { AvisoSemAcesso } from "@/components/layout/aviso-sem-acesso";
import { telaDoKitLiberada } from "@/components/layout/nav-data";

// Wraps a kit screen opened by URL: without the permission its menu entry carries, the notice replaces the page.
export function GuardaTelaDoKit({ children }: { children: ReactNode }) {
  const { user, permissionsHydrated } = useAuth();
  const { pathname } = useLocation();
  // Deciding before the permissions arrive would flash the notice at someone who has the permission.
  if (!permissionsHydrated) return <p className="text-[var(--color-muted-foreground)]">Carregando…</p>;
  if (!telaDoKitLiberada(pathname, user?.permissions ?? [])) {
    return <AvisoSemAcesso texto="Você não tem permissão para ver esta tela." />;
  }
  return <>{children}</>;
}
