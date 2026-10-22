using FSH.Modules.PAF.Conferencia;
using FSH.Modules.PAF.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace FSH.Modules.PAF.Autorizacao;

/// <summary>Exige credencial de estação de um papel, já conferida contra o VinculoEstacao nesta requisição.</summary>
internal sealed record EstacaoRequirement(string Papel) : IAuthorizationRequirement;

/// <summary>
/// Passa só quando o middleware de conferência marcou a credencial deste papel como vigente. Sem a marca
/// (middleware ausente, token de usuário, outro papel), não passa: o padrão é fechado.
/// </summary>
internal sealed class EstacaoAuthorizationHandler : AuthorizationHandler<EstacaoRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, EstacaoRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var httpContext = context.Resource as HttpContext;
        var conferido = httpContext?.Items[ConferenciaRequisicaoMiddleware.ChaveEstacaoConferida] as string;
        if (string.Equals(conferido, requirement.Papel, StringComparison.Ordinal)
            && context.User.HasClaim(PafClaims.Papel, requirement.Papel))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
