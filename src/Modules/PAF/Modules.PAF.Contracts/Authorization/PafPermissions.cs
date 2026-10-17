using FSH.Framework.Shared.Constants;

namespace FSH.Modules.PAF.Contracts.Authorization;

/// <summary>
/// Person permissions of the PAF, named <c>Permissions.PAF.{Recurso}.{Acao}</c> (arquitetura seção 5).
/// Stations never pass through these: they use the station policy on their own claim.
/// </summary>
public static class PafPermissions
{
    public static class Unidades
    {
        public const string Resource = "PAF.Unidades";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";

        /// <summary>Root-only: the Codefi operator reads the units and contracts of any client tenant (RF23).</summary>
        public const string Operar = $"Permissions.{Resource}.Operar";
    }

    public static class Filas
    {
        public const string Resource = "PAF.Filas";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Prioridades
    {
        public const string Resource = "PAF.Prioridades";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Estacoes
    {
        public const string Resource = "PAF.Estacoes";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Contratos
    {
        public const string Resource = "PAF.Contratos";
        public const string View = $"Permissions.{Resource}.View";

        /// <summary>Root-only: plan and licence limit are written by Codefi, never by the client (RF23).</summary>
        public const string Manage = $"Permissions.{Resource}.Manage";
    }

    public static class UsuariosUnidade
    {
        public const string Resource = "PAF.UsuariosUnidade";
        public const string View = $"Permissions.{Resource}.View";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class VinculosEstacao
    {
        public const string Resource = "PAF.VinculosEstacao";
        public const string Generate = $"Permissions.{Resource}.Generate";
        public const string Revoke = $"Permissions.{Resource}.Revoke";
    }

    public static class Guiches
    {
        public const string Resource = "PAF.Guiches";
        public const string Selecionar = $"Permissions.{Resource}.Selecionar";

        /// <summary>Manager or unit admin releases a guichê held by a dead or absent session (RF08, seção 5).</summary>
        public const string Liberar = $"Permissions.{Resource}.Liberar";
    }

    /// <summary>Call, recall, start, close and cancel on the guichê selected by the session (RF15, RF16, RF21).</summary>
    public static class Atendimentos
    {
        public const string Resource = "PAF.Atendimentos";
        public const string Operar = $"Permissions.{Resource}.Operar";
        public const string Encaminhar = $"Permissions.{Resource}.Encaminhar";
        public const string SelecionarFicha = $"Permissions.{Resource}.SelecionarFicha";

        /// <summary>Jumps the queue order (RF18), so it is its own permission and never basic (D01).</summary>
        public const string Vip = $"Permissions.{Resource}.Vip";
    }

    /// <summary>Manual emission at the guichê (RF11).</summary>
    public static class Fichas
    {
        public const string Resource = "PAF.Fichas";
        public const string Emitir = $"Permissions.{Resource}.Emitir";
    }

    /// <summary>Today's tickets screen and ficha lookup (RF12, RF18).</summary>
    public static class FichasDoDia
    {
        public const string Resource = "PAF.FichasDoDia";
        public const string View = $"Permissions.{Resource}.View";
        public const string Cancelar = $"Permissions.{Resource}.Cancelar";
        public const string Reabilitar = $"Permissions.{Resource}.Reabilitar";
    }

    public static class FichasEmAtendimento
    {
        public const string Resource = "PAF.FichasEmAtendimento";
        public const string View = $"Permissions.{Resource}.View";
    }

    public static class Estatisticas
    {
        public const string Resource = "PAF.Estatisticas";
        public const string View = $"Permissions.{Resource}.View";

        /// <summary>Type 4 report, personal data of employees: granted to no client role until D10 (requisitos, open item on the per-attendant report).</summary>
        public const string ViewPorAtendente = $"Permissions.{Resource}.ViewPorAtendente";
    }

    public static class Midias
    {
        public const string Resource = "PAF.Midias";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
    }

    public static class Programacoes
    {
        public const string Resource = "PAF.Programacoes";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    /// <summary>Parameters exist at unit level and at company level (UnidadeId NULL); editing the company level is a separate grant.</summary>
    public static class Parametros
    {
        public const string Resource = "PAF.Parametros";
        public const string View = $"Permissions.{Resource}.View";
        public const string UpdateUnidade = $"Permissions.{Resource}.UpdateUnidade";
        public const string UpdateEmpresa = $"Permissions.{Resource}.UpdateEmpresa";
    }

    public static class Usuarios
    {
        public const string Resource = "PAF.Usuarios";
        public const string View = $"Permissions.{Resource}.View";
        public const string Manage = $"Permissions.{Resource}.Manage";
    }

    // Guiches.Selecionar, Atendimentos.Operar, Fichas.Emitir, Atendimentos.Encaminhar and Atendimentos.SelecionarFicha are basic: the ordinary attendant operates a guichê, and the kit's basic role is their starting point.
    // Estatisticas.ViewPorAtendente is IsRoot only because the kit hands every non-root permission to each tenant's Admin role on startup, see the kit RolePermissionSyncer:
    // root keeps it out of every client role and catalog until D10 decides who sees it, then the flag flips.
    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View PAF units", ActionConstants.View, Unidades.Resource),
        new("Create PAF units", ActionConstants.Create, Unidades.Resource),
        new("Update PAF units", ActionConstants.Update, Unidades.Resource),
        new("View PAF queues", ActionConstants.View, Filas.Resource),
        new("Create PAF queues", ActionConstants.Create, Filas.Resource),
        new("Update PAF queues", ActionConstants.Update, Filas.Resource),
        new("View PAF priorities", ActionConstants.View, Prioridades.Resource),
        new("Create PAF priorities", ActionConstants.Create, Prioridades.Resource),
        new("Update PAF priorities", ActionConstants.Update, Prioridades.Resource),
        new("View PAF stations", ActionConstants.View, Estacoes.Resource),
        new("Create PAF stations", ActionConstants.Create, Estacoes.Resource),
        new("Update PAF stations", ActionConstants.Update, Estacoes.Resource),
        new("View PAF unit contracts", ActionConstants.View, Contratos.Resource),
        new("Manage PAF unit contracts", "Manage", Contratos.Resource, IsRoot: true),
        new("View PAF user units", ActionConstants.View, UsuariosUnidade.Resource),
        new("Update PAF user units", ActionConstants.Update, UsuariosUnidade.Resource),
        new("Generate PAF station link", "Generate", VinculosEstacao.Resource),
        new("Revoke PAF station link", "Revoke", VinculosEstacao.Resource),
        new("Selecionar guichê", "Selecionar", Guiches.Resource, IsBasic: true),
        new("Operar atendimento no guichê", "Operar", Atendimentos.Resource, IsBasic: true),
        new("Operar unidades de qualquer cliente", "Operar", Unidades.Resource, IsRoot: true),
        new("Liberar guichê preso", "Liberar", Guiches.Resource),
        new("Encaminhar atendimento", "Encaminhar", Atendimentos.Resource, IsBasic: true),
        new("Selecionar ficha para atendimento", "SelecionarFicha", Atendimentos.Resource, IsBasic: true),
        new("Chamar ficha VIP", "Vip", Atendimentos.Resource),
        new("Emitir fichas no guichê", "Emitir", Fichas.Resource, IsBasic: true),
        new("Ver fichas do dia", ActionConstants.View, FichasDoDia.Resource),
        new("Cancelar atendimento das fichas do dia", "Cancelar", FichasDoDia.Resource),
        new("Reabilitar atendimento das fichas do dia", "Reabilitar", FichasDoDia.Resource),
        new("Ver fichas em atendimento", ActionConstants.View, FichasEmAtendimento.Resource),
        new("Ver estatísticas", ActionConstants.View, Estatisticas.Resource),
        new("Ver estatística por atendente", "ViewPorAtendente", Estatisticas.Resource, IsRoot: true),
        new("View PAF media", ActionConstants.View, Midias.Resource),
        new("Create PAF media", ActionConstants.Create, Midias.Resource),
        new("Update PAF media", ActionConstants.Update, Midias.Resource),
        new("View PAF schedules", ActionConstants.View, Programacoes.Resource),
        new("Create PAF schedules", ActionConstants.Create, Programacoes.Resource),
        new("Update PAF schedules", ActionConstants.Update, Programacoes.Resource),
        new("Delete PAF schedules", ActionConstants.Delete, Programacoes.Resource),
        new("Ver parâmetros", ActionConstants.View, Parametros.Resource),
        new("Alterar parâmetros da unidade", "UpdateUnidade", Parametros.Resource),
        new("Alterar parâmetros da empresa", "UpdateEmpresa", Parametros.Resource),
        new("Ver usuários do PAF", ActionConstants.View, Usuarios.Resource),
        new("Gerenciar usuários do PAF", "Manage", Usuarios.Resource),
    ];
}
