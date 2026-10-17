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
