import { Building, MonitorSmartphone } from "lucide-react";
import type { NavSection } from "@/components/layout/nav-items";
import { PermissoesOperadorPaf } from "./api-operador";

/** PAF section of the admin menu (ADR 0008, menu item): listed by one line in the kit's `nav-items.ts`. */
export const secaoOperadorPaf: NavSection = {
  id: "paf",
  caption: "PAF",
  icon: Building,
  items: [
    {
      to: "/paf/unidades",
      label: "Unidades e contratos",
      icon: Building,
      perms: [PermissoesOperadorPaf.UnidadesOperar],
    },
    {
      to: "/paf/estacoes",
      label: "Estações",
      icon: MonitorSmartphone,
      perms: [PermissoesOperadorPaf.UnidadesOperar],
    },
  ],
};
