// Altered copies of real contract types for ContratoEstacaoTests: same simple name, so the shape line matches the real
// one, each in its own namespace. Public because they are only reached through typeof (an internal one trips CA1812).

namespace PAF.Tests.Contrato.SemSenha
{
    public sealed record FichaEmitidaDto(
        Guid FichaId, Guid AtendimentoId, Guid IdentificadorPublico, string Letra, int Numero, string Unidade, string Fila,
        string Prioridade, DateTimeOffset EmitidaEm, string Titulo, string Rodape, bool ImprimirQr)
    {
        public FSH.Modules.PAF.Contracts.v1.Midias.UrlLeituraMidiaDto? Logotipo { get; init; }
    }
}

namespace PAF.Tests.Contrato.SenhaAnulavel
{
    public sealed record FichaEmitidaDto(
        Guid FichaId, Guid AtendimentoId, Guid IdentificadorPublico, string? Senha, string Letra, int Numero, string Unidade,
        string Fila, string Prioridade, DateTimeOffset EmitidaEm, string Titulo, string Rodape, bool ImprimirQr)
    {
        public FSH.Modules.PAF.Contracts.v1.Midias.UrlLeituraMidiaDto? Logotipo { get; init; }
    }
}

namespace PAF.Tests.Contrato.ComCampoNovo
{
    public sealed record FichaEmitidaDto(
        Guid FichaId, Guid AtendimentoId, Guid IdentificadorPublico, string Senha, string Letra, int Numero, string Unidade,
        string Fila, string Prioridade, DateTimeOffset EmitidaEm, string Titulo, string Rodape, bool ImprimirQr,
        string Canal)
    {
        public FSH.Modules.PAF.Contracts.v1.Midias.UrlLeituraMidiaDto? Logotipo { get; init; }
    }
}

namespace PAF.Tests.Contrato.PedidoObrigatorio
{
    public sealed record EmitirFichaRequest(Guid FilaId, Guid PrioridadeId, string Canal);
}

namespace PAF.Tests.Contrato.PedidoAnulavel
{
    public sealed record EmitirFichaRequest(Guid FilaId, Guid PrioridadeId, string? Canal);
}

namespace PAF.Tests.Contrato.MetodoRenomeado
{
    public static class PafHubMetodos
    {
        public const string SenhaChamadaNoPainel = "SenhaChamadaNoPainel";
        public const string SenhaRechamada = "SenhaRechamada";
        public const string AtendimentoAtualizado = "AtendimentoAtualizado";
        public const string FilaAtualizada = "FilaAtualizada";
    }
}
