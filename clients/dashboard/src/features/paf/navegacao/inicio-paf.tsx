import { Navigate } from "react-router-dom";
import { useAuth } from "@/auth/use-auth";
import { primeiraTelaPaf } from "@/features/paf/navegacao/menu-paf";

// "/" and the kit screens the product does not ship land here. The PAF opens on the guichê, or on the first PAF
// screen the user's permissions open; whoever has none (an Identity-only admin) gets the kit overview.
export function InicioPaf() {
  const { user, permissionsHydrated } = useAuth();
  // Choosing before the permissions arrive would send a guichê user to the overview.
  if (!permissionsHydrated) return null;
  return <Navigate to={primeiraTelaPaf(user?.permissions ?? []) ?? "/overview"} replace />;
}
