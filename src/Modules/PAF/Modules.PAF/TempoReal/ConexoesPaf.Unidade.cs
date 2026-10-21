namespace FSH.Modules.PAF.TempoReal;

// Own file: the unit move belongs to S07, while ConexoesPaf.cs keeps the guichê and revocation paths (PLANO-FATIA-2, seção 2).
public sealed partial class ConexoesPaf
{
    /// <summary>
    /// Manual unit switch (S07, seção 7): every connection of the session, on any instance, leaves the group of
    /// <paramref name="origem"/> and joins the group of <paramref name="destino"/>, so the other windows receive the
    /// new unit's events without reconnecting. Call after the switch is committed. Returns how many were moved.
    /// </summary>
    public async Task<int> MoverSessaoParaUnidadeAsync(string tenantId, Guid sessaoId, Guid? origem, Guid destino, CancellationToken ct)
    {
        var grupoDestino = PafGrupos.Unidade(tenantId, destino);
        var grupoOrigem = origem is { } id && id != destino ? PafGrupos.Unidade(tenantId, id) : null;
        var conexoes = await DaSessaoAsync(tenantId, sessaoId, ct).ConfigureAwait(false);
        // Every connection at once: one without an owner costs its own wait, never the sum over the session.
        await Task.WhenAll(conexoes.Select(connectionId => MoverParaUnidadeAsync(tenantId, connectionId, grupoOrigem, grupoDestino, ct)))
            .ConfigureAwait(false);
        return conexoes.Count;
    }

    private async Task MoverParaUnidadeAsync(string tenantId, string connectionId, string? grupoOrigem, string grupoDestino, CancellationToken ct)
    {
        using var limite = Limite(ct);
        if (grupoOrigem is not null)
        {
            if (!await ConfirmadoAsync(connectionId, hub.Groups.RemoveFromGroupAsync(connectionId, grupoOrigem, limite.Token), limite.Token).ConfigureAwait(false))
            {
                await EsquecerSemDonoAsync(tenantId, connectionId, limite.Token).ConfigureAwait(false);
                return;
            }

            await EsquecerGrupoAsync(connectionId, tenantId, grupoOrigem, limite.Token).ConfigureAwait(false);
        }

        if (!await ConfirmadoAsync(connectionId, hub.Groups.AddToGroupAsync(connectionId, grupoDestino, limite.Token), limite.Token).ConfigureAwait(false))
        {
            await EsquecerSemDonoAsync(tenantId, connectionId, limite.Token).ConfigureAwait(false);
            return;
        }

        await LembrarGrupoAsync(connectionId, tenantId, grupoDestino, limite.Token).ConfigureAwait(false);
    }
}
