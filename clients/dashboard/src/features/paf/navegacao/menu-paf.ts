import { Building2, Layers, ListOrdered, Monitor, MonitorSmartphone } from "lucide-react";
import { ClipboardList } from "lucide-react";
import { Hourglass } from "lucide-react";
import { Search } from "lucide-react";
import { Printer } from "lucide-react";
import { SlidersHorizontal } from "lucide-react";
import { Users } from "lucide-react";
import { ArrowUpWideNarrow } from "lucide-react";
import { ChartColumn } from "lucide-react";
import { CalendarClock, Film } from "lucide-react";
import type { NavSection } from "@/components/layout/nav-data";

// PAF section of the dashboard menu (ADR 0008, amended 17/10/2026); nav-data.ts only lists it. Each entry is
// gated by the permission of the endpoint its page lists from, so nobody lands on a page the API refuses.
// Every later PAF screen adds its line here. Station screens (totem, painel) are opened by the device link,
// never from the menu.
export const secaoMenuPaf: NavSection = {
  id: "paf",
  caption: "Atendimento",
  icon: ListOrdered,
  items: [
    // GET guiche/guiches and guiche/posto: Guiches.Selecionar.
    { to: "/paf/guiche", label: "Guichê", icon: MonitorSmartphone, perm: "Permissions.PAF.Guiches.Selecionar" },
    // GET fichas/atendimentos: FichasDoDia.View (F06).
    { to: "/paf/fichas-do-dia", label: "Fichas do dia", icon: ClipboardList, perm: "Permissions.PAF.FichasDoDia.View" },
    // GET unidades: Unidades.View (C02).
    { to: "/paf/unidades", label: "Unidades", icon: Building2, perm: "Permissions.PAF.Unidades.View" },
    // GET filas: Filas.View (C09).
    { to: "/paf/filas", label: "Filas", icon: Layers, perm: "Permissions.PAF.Filas.View" },
    // GET estacoes: Estacoes.View (C04).
    { to: "/paf/estacoes", label: "Estações", icon: Monitor, perm: "Permissions.PAF.Estacoes.View" },
    // GET fichas/em-atendimento: FichasEmAtendimento.View (F07).
    { to: "/paf/fichas-em-atendimento", label: "Fichas em atendimento", icon: Hourglass, perm: "Permissions.PAF.FichasEmAtendimento.View" },
    // GET fichas/consulta: FichasDoDia.View (F14).
    { to: "/paf/consultar-ficha", label: "Consultar ficha", icon: Search, perm: "Permissions.PAF.FichasDoDia.View" },
    // GET fichas/reimpressao: FichasDoDia.View (F15).
    { to: "/paf/ficha-de-espera", label: "Ficha de espera", icon: Printer, perm: "Permissions.PAF.FichasDoDia.View" },
    // GET parametros: Parametros.View (C10).
    { to: "/paf/parametros", label: "Parâmetros", icon: SlidersHorizontal, perm: "Permissions.PAF.Parametros.View" },
    // GET usuarios: Usuarios.View (C12).
    { to: "/paf/usuarios", label: "Usuários", icon: Users, perm: "Permissions.PAF.Usuarios.View" },
    // GET prioridades: Prioridades.View (C14).
    { to: "/paf/prioridades", label: "Prioridades", icon: ArrowUpWideNarrow, perm: "Permissions.PAF.Prioridades.View" },
    // GET estatistica: Estatisticas.View (E5).
    { to: "/paf/estatistica", label: "Estatística", icon: ChartColumn, perm: "Permissions.PAF.Estatisticas.View" },
    // GET midias: Midias.View (M10).
    { to: "/paf/midias", label: "Arquivos de mídia", icon: Film, perm: "Permissions.PAF.Midias.View" },
    // GET programacoes-midia: Programacoes.View (M10).
    { to: "/paf/programacoes", label: "Programação de mídias", icon: CalendarClock, perm: "Permissions.PAF.Programacoes.View" },
  ],
};
