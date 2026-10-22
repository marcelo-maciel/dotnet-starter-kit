namespace FSH.Modules.PAF.Data;

/// <summary>
/// Names of the PAF query filters. The aggregation path turns off only <see cref="Unidade"/> with
/// IgnoreQueryFilters([PafQueryFilters.Unidade]); the parameterless IgnoreQueryFilters() would also
/// drop the tenant filter and is forbidden in this module (ADR 0003).
/// </summary>
public static class PafQueryFilters
{
    public const string Unidade = "PafUnidade";
}
