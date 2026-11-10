using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Sessao;

namespace FSH.Modules.PAF.Features.v1.Sessao.EndSessao;

// The command has no input: the session comes from the token. The class exists for the kit's handler/validator pairing gate.
public sealed class EndSessaoCommandValidator : AbstractValidator<EndSessaoCommand>;
