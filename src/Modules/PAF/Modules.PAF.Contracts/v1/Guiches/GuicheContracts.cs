using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Guiches;

// Reads of the guichê screen for the ordinary attendant, who holds no cadastro permission (seção 5).

/// <summary>An active unidade of the caller's scope, as the guichê screen offers it: nothing beyond what the choice needs.</summary>
public sealed record UnidadeDoGuicheDto(Guid Id, string Nome);

/// <summary>Active guichês of the current unit, with who holds each one.</summary>
public sealed record ListGuichesDaUnidadeQuery : IQuery<IReadOnlyList<GuicheDaUnidadeDto>>;

/// <summary>
/// <paramref name="Ocupante"/> is the user name of another session holding it ("a tela informa quem está no guichê"),
/// null when free or when <paramref name="Meu"/>, held by the caller's own session.
/// </summary>
public sealed record GuicheDaUnidadeDto(Guid Id, string Nome, string Tipo, string? Ocupante, bool Meu);

/// <summary>Where the session stands: current unit, selected guichê and the atendimento in progress on it, so a reload resumes.</summary>
public sealed record ObterPostoGuicheQuery : IQuery<PostoGuicheDto>;

public sealed record PostoGuicheDto(Guid? UnidadeId, string? Unidade, Guid? EstacaoId, string? Guiche, AtendimentoEmCursoDto? Atendimento);

/// <summary><paramref name="Estado"/> is Chamado or EmAtendimento.</summary>
public sealed record AtendimentoEmCursoDto(Guid AtendimentoId, Guid FichaId, string Senha, string Estado, int Versao);
