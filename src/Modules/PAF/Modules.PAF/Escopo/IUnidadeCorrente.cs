namespace FSH.Modules.PAF.Escopo;

/// <summary>
/// Unidade corrente da requisição, uma só (ADR 0006). Quem resolve a credencial (sessão no Valkey,
/// claim da estação, concessão de impersonação) chama <see cref="Definir"/>; o filtro de unidade do
/// PafDbContext lê <see cref="UnidadeId"/>. Sem unidade definida, o filtro não devolve nenhuma linha.
/// </summary>
public interface IUnidadeCorrente
{
    Guid? UnidadeId { get; }

    void Definir(Guid unidadeId);
}
