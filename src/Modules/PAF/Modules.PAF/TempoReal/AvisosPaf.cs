using FSH.Framework.Eventing.Abstractions;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using Microsoft.AspNetCore.SignalR;

namespace FSH.Modules.PAF.TempoReal;

// The outbox stores these types by assembly-qualified name: renaming or moving one dead-letters the
// rows already written. Id is always the payload's EventoId, so both delivery paths carry the same id.

/// <summary>
/// A real-time notice that travels both paths of seção 8 item 6: direct send after commit, and the
/// kit outbox as delivery guarantee. Each notice knows its own target groups.
/// </summary>
public abstract record AvisoPaf(Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source)
    : IIntegrationEvent
{
    public const string Origem = "PAF";

    internal abstract Task EntregarAsync(IHubClients clientes, CancellationToken ct);

    protected string Tenant => TenantId ?? throw new InvalidOperationException("Aviso do PAF sem tenant.");
}

/// <summary>
/// To the guichê that called and to the painéis chosen by the caller (the painel-to-guichê routing rule
/// belongs to the call feature, not to the hub).
/// </summary>
public sealed record SenhaChamadaPaf(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    SenhaChamada Aviso, IReadOnlyList<Guid> Paineis)
    : AvisoPaf(Id, OccurredOnUtc, TenantId, CorrelationId, Source)
{
    public static SenhaChamadaPaf Criar(string tenantId, string correlationId, SenhaChamada aviso, IReadOnlyList<Guid> paineis)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return new(aviso.EventoId, DateTime.UtcNow, tenantId, correlationId, Origem, aviso, paineis);
    }

    internal override Task EntregarAsync(IHubClients clientes, CancellationToken ct) =>
        clientes.Groups(GruposDaChamada(Tenant, Aviso.EstacaoId, Paineis))
            .SendAsync(PafHubMetodos.SenhaChamada, Aviso, ct);

    internal static List<string> GruposDaChamada(string tenantId, Guid guicheId, IReadOnlyList<Guid> paineis)
    {
        var grupos = new List<string>(paineis.Count + 1) { PafGrupos.Guiche(tenantId, guicheId) };
        grupos.AddRange(paineis.Distinct().Select(p => PafGrupos.Painel(tenantId, p)));
        return grupos;
    }
}

public sealed record SenhaRechamadaPaf(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    SenhaRechamada Aviso, IReadOnlyList<Guid> Paineis)
    : AvisoPaf(Id, OccurredOnUtc, TenantId, CorrelationId, Source)
{
    public static SenhaRechamadaPaf Criar(string tenantId, string correlationId, SenhaRechamada aviso, IReadOnlyList<Guid> paineis)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return new(aviso.EventoId, DateTime.UtcNow, tenantId, correlationId, Origem, aviso, paineis);
    }

    internal override Task EntregarAsync(IHubClients clientes, CancellationToken ct) =>
        clientes.Groups(SenhaChamadaPaf.GruposDaChamada(Tenant, Aviso.EstacaoId, Paineis))
            .SendAsync(PafHubMetodos.SenhaRechamada, Aviso, ct);
}

/// <summary>To every connection of the unit: painel drops a cancelled call, guichê and lists refresh.</summary>
public sealed record AtendimentoAtualizadoPaf(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    AtendimentoAtualizado Aviso)
    : AvisoPaf(Id, OccurredOnUtc, TenantId, CorrelationId, Source)
{
    public static AtendimentoAtualizadoPaf Criar(string tenantId, string correlationId, AtendimentoAtualizado aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return new(aviso.EventoId, DateTime.UtcNow, tenantId, correlationId, Origem, aviso);
    }

    internal override Task EntregarAsync(IHubClients clientes, CancellationToken ct) =>
        clientes.Group(PafGrupos.Unidade(Tenant, Aviso.UnidadeId))
            .SendAsync(PafHubMetodos.AtendimentoAtualizado, Aviso, ct);
}

public sealed record FilaAtualizadaPaf(
    Guid Id, DateTime OccurredOnUtc, string? TenantId, string CorrelationId, string Source,
    FilaAtualizada Aviso)
    : AvisoPaf(Id, OccurredOnUtc, TenantId, CorrelationId, Source)
{
    public static FilaAtualizadaPaf Criar(string tenantId, string correlationId, FilaAtualizada aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        return new(aviso.EventoId, DateTime.UtcNow, tenantId, correlationId, Origem, aviso);
    }

    internal override Task EntregarAsync(IHubClients clientes, CancellationToken ct) =>
        clientes.Group(PafGrupos.Unidade(Tenant, Aviso.UnidadeId))
            .SendAsync(PafHubMetodos.FilaAtualizada, Aviso, ct);
}
