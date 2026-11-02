using FSH.Framework.Core.Context;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Users.ChangePassword;
using FSH.Modules.Identity.Localization;
using Mediator;
using Microsoft.Extensions.Localization;

namespace FSH.Modules.Identity.Features.v1.Users.ChangePassword;

public sealed class ChangePasswordCommandHandler : ICommandHandler<ChangePasswordCommand, string>
{
    private readonly IUserService _userService;
    private readonly ICurrentUser _currentUser;
    private readonly IStringLocalizer<IdentityResources> _localizer;

    public ChangePasswordCommandHandler(
        IUserService userService,
        ICurrentUser currentUser,
        IStringLocalizer<IdentityResources> localizer)
    {
        _userService = userService;
        _currentUser = currentUser;
        _localizer = localizer;
    }

    public async ValueTask<string> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_currentUser.IsAuthenticated())
        {
            throw new InvalidOperationException("User is not authenticated.");
        }

        var userId = _currentUser.GetUserId().ToString();

        await _userService.ChangePasswordAsync(command.Password, command.NewPassword, command.ConfirmNewPassword, userId, cancellationToken).ConfigureAwait(false);

        return _localizer["Identity.PasswordChanged"];
    }
}