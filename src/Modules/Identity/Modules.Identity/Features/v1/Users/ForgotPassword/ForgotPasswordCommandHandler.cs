using FSH.Framework.Web.Frontend;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Users.ForgotPassword;
using FSH.Modules.Identity.Localization;
using Mediator;
using Microsoft.Extensions.Localization;

namespace FSH.Modules.Identity.Features.v1.Users.ForgotPassword;

public sealed class ForgotPasswordCommandHandler : ICommandHandler<ForgotPasswordCommand, string>
{
    private readonly IUserService _userService;
    private readonly IFrontendOriginResolver _originResolver;
    private readonly IStringLocalizer<IdentityResources> _localizer;

    public ForgotPasswordCommandHandler(
        IUserService userService,
        IFrontendOriginResolver originResolver,
        IStringLocalizer<IdentityResources> localizer)
    {
        _userService = userService;
        _originResolver = originResolver;
        _localizer = localizer;
    }

    public async ValueTask<string> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Self-service flow: the reset link must land on the SPA the user is currently using.
        var origin = _originResolver.ResolveForCurrentRequest();

        await _userService.ForgotPasswordAsync(command.Email, origin, cancellationToken).ConfigureAwait(false);

        return _localizer["Identity.PasswordResetEmailSent"];
    }
}