using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Web.Frontend;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Usuarios;
using FSH.Modules.PAF.Contracts.v1.UsuariosUnidade;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.UsuariosUnidade;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Features.v1.Usuarios.CreateUsuario;

/// <summary>
/// RF08 cadastro (C12, D14: the login is the e-mail). Everything the caller may be refused for is checked before the
/// kit user exists. Identity and PAF are separate contexts, so the steps after <c>RegisterAsync</c> run in this order:
/// role, PAF profile, scope, and the e-mail confirmation LAST, because the kit refuses the login of an unconfirmed
/// e-mail: a failure in between leaves a user who cannot log in, and the compensation also deactivates him.
/// </summary>
public sealed class CreateUsuarioPafCommandHandler(
    PafDbContext db,
    ConcessaoDeEscopo concessao,
    PapeisDoUsuarioPaf papeis,
    IUserProfileService perfis,
    IUserRegistrationService registro,
    IUserStatusService situacao,
    IFrontendOriginResolver origem,
    IMediator mediator,
    ILogger<CreateUsuarioPafCommandHandler> logger)
    : ICommandHandler<CreateUsuarioPafCommand, Guid>
{
    public const string MensagemEmailEmUso = "Já existe usuário com este e-mail.";

    public async ValueTask<Guid> Handle(CreateUsuarioPafCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await concessao.ExigirParaUsuarioNovoAsync(command.Unidades, command.EscopoEmpresa, cancellationToken).ConfigureAwait(false);
        await papeis.ExigirConcedivelAsync(command.Papel, cancellationToken).ConfigureAwait(false);
        if (await perfis.ExistsWithEmailAsync(command.Email, null, cancellationToken).ConfigureAwait(false))
        {
            throw new CustomException(MensagemEmailEmUso, (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        }

        // The kit also enqueues its confirmation e-mail here; the address may be a made-up one of the client's domain
        // (D14), and the confirmation below makes the link moot.
        var usuarioId = Guid.Parse(await registro.RegisterAsync(
            command.Nome.Trim(), command.Sobrenome.Trim(), command.Email.Trim(), command.Email.Trim(), command.Senha,
            command.ConfirmacaoSenha, string.Empty, origem.ResolveDefault(), cancellationToken).ConfigureAwait(false));

        try
        {
            await papeis.DefinirAsync(usuarioId, [command.Papel], cancellationToken).ConfigureAwait(false);

            // The profile row goes first: the scope edit turns it into escopo empresa when asked.
            var perfil = PerfilUsuario.Criar(usuarioId);
            perfil.DefinirFuncaoERamal(command.Funcao, command.Ramal);
            db.PerfisUsuario.Add(perfil);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await mediator.Send(new SetUsuarioUnidadesCommand(usuarioId, command.Unidades, command.EscopoEmpresa), cancellationToken)
                .ConfigureAwait(false);
            await registro.AdminConfirmEmailAsync(usuarioId.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompensarAsync(usuarioId).ConfigureAwait(false);
            throw;
        }

        return usuarioId;
    }

    // The unconfirmed e-mail already refuses the login; deactivating is the second barrier and marks the leftover user
    // for the admin. It runs under its own deadline: the request's token may be the very cause of the failure.
    private async Task CompensarAsync(Guid usuarioId)
    {
        using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await situacao.ToggleStatusAsync(false, usuarioId.ToString(), prazo.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CustomException or OperationCanceledException or DbUpdateException)
        {
            // The original failure is what the caller must see; this one is only logged, without PII.
            logger.LogWarning(ex, "PAF: compensação do cadastro não desativou o usuário user=u_{UsuarioId}", usuarioId);
        }
    }
}
