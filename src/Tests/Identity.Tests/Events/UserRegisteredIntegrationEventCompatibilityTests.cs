using FSH.Framework.Eventing.Serialization;
using FSH.Modules.Identity.Contracts.Events;

namespace Identity.Tests.Events;

/// <summary>
/// Locale joined the event on 22/10/2026. An outbox or inbox row written before that has no "locale" in its
/// payload, and it must still deserialize, with a null Locale, so the welcome e-mail falls back to the
/// deployment's default culture instead of the row failing for good.
/// </summary>
public sealed class UserRegisteredIntegrationEventCompatibilityTests
{
    [Fact]
    public void Deserialize_Should_GiveANullLocale_When_ThePayloadPredatesTheField()
    {
        const string payloadBeforeLocale =
            """{"id":"6f1c2b9e-1d2a-4c55-9a0e-3b7f6a1d2c4e","occurredOnUtc":"2026-10-21T12:00:00Z","tenantId":"root","correlationId":"c-1","source":"Identity","userId":"u-1","email":"pessoa@example.com","firstName":"Ana","lastName":"Souza"}""";

        var @event = new JsonEventSerializer().Deserialize(
            payloadBeforeLocale, typeof(UserRegisteredIntegrationEvent).AssemblyQualifiedName!);

        var registered = @event.ShouldBeOfType<UserRegisteredIntegrationEvent>();
        registered.Email.ShouldBe("pessoa@example.com");
        registered.Locale.ShouldBeNull();
    }
}
