namespace FSH.Modules.PAF.Contracts.v1.TempoReal;

/// <summary>
/// The session <paramref name="SessaoId"/> was revoked (single session, logout, unit taken out of scope, user
/// deactivated, revocation in a kit screen): sent under <see cref="Metodo"/> to the connections of that session only,
/// right before the server closes them, so the screen shows <paramref name="Motivo"/> instead of a network drop
/// (seção 7, "Revogar sessão encerra a conexão"). A screen whose own session id differs ignores it.
/// No <c>EventoId</c>: it has no outbox copy, and a repeat is dropped by the session id.
/// </summary>
public sealed record SessaoInvalidada(Guid SessaoId, string Motivo)
{
    public const string Metodo = "SessaoInvalidada";
}
