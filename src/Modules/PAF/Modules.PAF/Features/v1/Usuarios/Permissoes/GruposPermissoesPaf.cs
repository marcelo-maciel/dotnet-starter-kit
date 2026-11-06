using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Usuarios;

namespace FSH.Modules.PAF.Features.v1.Usuarios.Permissoes;

/// <summary>
/// The four RF08 blocks over the client-side PAF permissions, the single definition the screen renders. Every
/// non-root entry of <see cref="PafPermissions.All"/> sits in exactly one block (pinned by <c>PAF.Tests</c>); root
/// permissions are Codefi's and never granted per user.
/// </summary>
public static class GruposPermissoesPaf
{
    public const string ControleDeFila = "Controle de Fila";
    public const string Atendimento = "Atendimento";
    public const string PainelChamador = "Painel Chamador";
    public const string Administracao = "Administração";

    public static IReadOnlyList<GrupoPermissoesPafDto> Todos { get; } =
    [
        new(ControleDeFila, null,
        [
            new(PafPermissions.Fichas.Emitir, "Emitir fichas no guichê"),
            new(PafPermissions.FichasDoDia.View, "Ver fichas do dia e consultar ficha"),
            new(PafPermissions.FichasDoDia.Cancelar, "Cancelar atendimento nas fichas do dia"),
            new(PafPermissions.FichasDoDia.Reabilitar, "Reabilitar atendimento nas fichas do dia"),
            new(PafPermissions.FichasEmAtendimento.View, "Ver fichas em atendimento"),
        ]),
        new(Atendimento, null,
        [
            new(PafPermissions.Guiches.Selecionar, "Selecionar guichê"),
            new(PafPermissions.Atendimentos.Operar, "Chamar o próximo e encerrar atendimento"),
            new(PafPermissions.Atendimentos.Encaminhar, "Encaminhar atendimento"),
            new(PafPermissions.Atendimentos.SelecionarFicha, "Fichas em espera: escolher a ficha a chamar"),
            new(PafPermissions.Atendimentos.Vip, "Chamar ficha VIP"),
            new(PafPermissions.Guiches.Liberar, "Liberar guichê preso"),
        ]),

        // The painel and the totem run on the station credential (link redemption), never on a person's session, so
        // "executar painel chamador" has no person permission; who links a station is under Administração.
        new(PainelChamador, "O painel roda pela credencial da estação; quem vincula a estação está em Administração.", []),
        new(Administracao, null,
        [
            new(PafPermissions.Filas.View, "Ver filas"),
            new(PafPermissions.Filas.Create, "Cadastrar filas"),
            new(PafPermissions.Filas.Update, "Alterar filas"),
            new(PafPermissions.Prioridades.View, "Ver prioridades"),
            new(PafPermissions.Prioridades.Create, "Cadastrar prioridades"),
            new(PafPermissions.Prioridades.Update, "Alterar prioridades"),
            new(PafPermissions.Estacoes.View, "Ver estações"),
            new(PafPermissions.Estacoes.Create, "Cadastrar estações"),
            new(PafPermissions.Estacoes.Update, "Alterar e desativar estações"),
            new(PafPermissions.VinculosEstacao.Generate, "Vincular dispositivo à estação"),
            new(PafPermissions.VinculosEstacao.Revoke, "Revogar vínculo de estação"),
            new(PafPermissions.Midias.View, "Ver mídias"),
            new(PafPermissions.Midias.Create, "Enviar mídias"),
            new(PafPermissions.Midias.Update, "Alterar mídias"),
            new(PafPermissions.Programacoes.View, "Ver programação de mídia"),
            new(PafPermissions.Programacoes.Create, "Criar programação de mídia"),
            new(PafPermissions.Programacoes.Update, "Alterar programação de mídia"),
            new(PafPermissions.Programacoes.Delete, "Excluir programação de mídia"),
            new(PafPermissions.Unidades.View, "Ver unidades"),
            new(PafPermissions.Unidades.Create, "Cadastrar unidades (administrador da empresa)"),
            new(PafPermissions.Unidades.Update, "Alterar unidades"),
            new(PafPermissions.Contratos.View, "Ver contrato das unidades"),
            new(PafPermissions.Parametros.View, "Ver parâmetros"),
            new(PafPermissions.Parametros.UpdateUnidade, "Alterar parâmetros da unidade"),
            new(PafPermissions.Parametros.UpdateEmpresa, "Alterar parâmetros da empresa"),
            new(PafPermissions.Estatisticas.View, "Ver estatísticas"),
            new(PafPermissions.Estatisticas.ViewPorAtendente, "Ver estatística por atendente"),
            new(PafPermissions.Usuarios.View, "Ver usuários"),
            new(PafPermissions.Usuarios.Manage, "Gerenciar usuários e permissões"),
            new(PafPermissions.UsuariosUnidade.View, "Ver unidades dos usuários"),
            new(PafPermissions.UsuariosUnidade.Update, "Alterar unidades dos usuários"),
        ]),
    ];

    public static IReadOnlySet<string> Nomes { get; } =
        Todos.SelectMany(g => g.Permissoes).Select(p => p.Nome).ToHashSet(StringComparer.Ordinal);
}
