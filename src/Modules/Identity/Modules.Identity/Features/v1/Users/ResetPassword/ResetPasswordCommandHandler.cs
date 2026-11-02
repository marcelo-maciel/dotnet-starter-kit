using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Users.ResetPassword;
using FSH.Modules.Identity.Localization;
using Mediator;
using Microsoft.Extensions.Localization;

namespace FSH.Modules.Identity.Features.v1.Users.ResetPassword;

public sealed class ResetPasswordCommandHandler : ICommandHandler<ResetPasswordCommand, string>
{
    private readonly IUserService _userService;
    private readonly IStringLocalizer<IdentityResources> _localizer;

    public ResetPasswordCommandHandler(IUserService userService, IStringLocalizer<IdentityResources> localizer)
    {
        _userService = userService;
        _localizer = localizer;
    }

    public async ValueTask<string> Handle(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await _userService.ResetPasswordAsync(command.Email, command.Password, command.Token, cancellationToken).ConfigureAwait(false);

        return _localizer["Identity.PasswordHasBeenReset"];
    }
}