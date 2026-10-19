namespace FSH.Modules.PAF.Domain;

// Persisted as strings: the partial unique indexes on Atendimentos filter on the literal state names.
public enum EstadoAtendimento
{
    Aguardando,
    Chamado,
    EmAtendimento,
    Encerrado,
    Cancelado,
}

public enum TipoEstacao
{
    Administracao,
    Emissao,
    EmissaoGuiche,
    EmissaoPainel,
    Guiche,
    GuichePainel,
    Painel,
}

public enum PapelEstacao
{
    Emissao,
    Guiche,
    Painel,
}

public enum CanalEmissao
{
    Guiche,
    Totem,
}

public enum NivelParametro
{
    Empresa,
    Unidade,
}

public enum PoliticaLimiteLicenca
{
    Recusar,
    UltrapassarComAviso,
}

// Persisted as strings: CK_Midias_Tipo and CK_EnviosMidia_Tipo list the literal names.
public enum TipoMidia
{
    Audio,
    Imagem,
    Video,
}

/// <summary>Bit de cada dia na máscara de <see cref="ProgramacaoMidia"/>, na ordem de <see cref="DayOfWeek"/>.</summary>
[Flags]
public enum DiasSemana
{
    None = 0,
    Domingo = 1,
    Segunda = 2,
    Terca = 4,
    Quarta = 8,
    Quinta = 16,
    Sexta = 32,
    Sabado = 64,
    Todos = 127,
}
