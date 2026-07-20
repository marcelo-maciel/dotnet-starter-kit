using System.Security.Claims;
using FSH.Framework.Core.Context;
using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Identity.Contracts.v1.Impersonation;
using FSH.Modules.Identity.Contracts.v1.Impersonation.StartImpersonation;
using FSH.Modules.Identity.Features.v1.Impersonation.StartImpersonation;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.IdentityModel.Tokens.Jwt;

namespace Identity.Tests.Handlers;

/// <summary>
/// The impersonation token must NOT carry the target user's `locale` claim — language is a
/// presentation concern, so the operator keeps reading in their own language.
/// </summary>
public sealed class StartImpersonationCommandHandlerTests
{
    private const string TenantId = "codefi";
    private const string TargetUserId = "target-user";

    private readonly IIdentityService _identityService = Substitute.For<IIdentityService>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly ISecurityAudit _securityAudit = Substitute.For<ISecurityAudit>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRequestContext _requestContext = Substitute.For<IRequestContext>();
    private readonly IImpersonationGrantService _grantService = Substitute.For<IImpersonationGrantService>();

    private StartImpersonationCommandHandler CreateSut() =>
        new(_identityService, _tokenService, _securityAudit, _currentUser, _requestContext,
            _grantService, TimeProvider.System, Substitute.For<ILogger<StartImpersonationCommandHandler>>());

    [Fact]
    public async Task Handle_strips_the_targets_locale_claim_from_the_impersonation_token()
    {
        // Arrange — an authenticated operator in the same tenant as the target.
        _currentUser.IsAuthenticated().Returns(true);
        _currentUser.GetUserId().Returns(Guid.NewGuid());
        _currentUser.GetTenant().Returns(TenantId);
        _currentUser.Name.Returns("operator");
        _currentUser.GetUserClaims().Returns(new List<Claim>());

        // The target user has a persisted locale, so BuildClaimsForUserAsync returns a `locale` claim.
        var targetClaims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, "orig-jti"),
            new(ClaimTypes.Name, "Target User"),
            new("locale", "pt-BR"),
        };
        _identityService
            .BuildClaimsForUserAsync(TargetUserId, TenantId, Arg.Any<CancellationToken>())
            .Returns(((string, IEnumerable<Claim>)?)(TargetUserId, targetClaims));

        IEnumerable<Claim>? issuedClaims = null;
        _tokenService
            .IssueAccessOnlyAsync(
                Arg.Any<string>(),
                Arg.Do<IEnumerable<Claim>>(c => issuedClaims = c),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(("access-token", DateTime.UtcNow.AddMinutes(15)));

        var sut = CreateSut();

        // Act
        await sut.Handle(new StartImpersonationCommand(TargetUserId, TenantId, "reason", 15), CancellationToken.None);

        // Assert
        issuedClaims.ShouldNotBeNull();
        issuedClaims.Any(c => c.Type == "locale").ShouldBeFalse();
    }
}
