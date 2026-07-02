using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Users.ForgotPassword;
using FSH.Modules.Identity.Services;
using Mediator;

namespace FSH.Modules.Identity.Features.v1.Users.ForgotPassword;

public sealed class ForgotPasswordCommandHandler : ICommandHandler<ForgotPasswordCommand, string>
{
    private readonly IUserService _userService;
    private readonly IOriginResolver _originResolver;

    public ForgotPasswordCommandHandler(IUserService userService, IOriginResolver originResolver)
    {
        _userService = userService;
        _originResolver = originResolver;
    }

    public async ValueTask<string> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The reset link must land on the SPA that made the request.
        var origin = _originResolver.FrontendOrigin();

        await _userService.ForgotPasswordAsync(command.Email, origin, cancellationToken).ConfigureAwait(false);

        return "Password reset email sent.";
    }
}