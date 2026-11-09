import { Building, MonitorSmartphone } from "lucide-react";
import type { NavSection } from "@/components/layout/nav-items";
import { PermissoesOperadorPaf } from "./api-operador";

/** PAF section of the admin menu (ADR 0008, menu item): listed by one line in the kit's `nav-items.ts`. */
export const secaoOperadorPaf: NavSection = {
  id: "paf",
  caption: "paf",
  icon: Building,
  items: [
    {
      to: "/paf/unidades",
      label: "pafUnits",
      icon: Building,
      perms: [PermissoesOperadorPaf.UnidadesOperar],
    },
    {
      to: "/paf/estacoes",
      label: "pafStations",
      icon: MonitorSmartphone,
      perms: [PermissoesOperadorPaf.UnidadesOperar],
    },
  ],
};
