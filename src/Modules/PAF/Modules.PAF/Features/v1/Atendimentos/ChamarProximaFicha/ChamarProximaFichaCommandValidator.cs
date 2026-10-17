using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;

// The command has no input: the guichê comes from the session. The class exists for the kit's handler/validator pairing gate.
public sealed class ChamarProximaFichaCommandValidator : AbstractValidator<ChamarProximaFichaCommand>;
