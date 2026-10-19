namespace FSH.Modules.PAF.Data;

/// <summary>
/// Marks a <c>PafDbContext</c> migration as the CONTRACT step of expand and contract (RNF16): the only kind allowed to
/// drop, rename, make a column required or remove a unique index, because it ships only after every running image
/// (API instance, totem and painel bundles) stopped using what it removes. <paramref name="motivo"/> names what is
/// removed and which earlier release already stopped using it; the PR review checks that claim.
/// The barrier test is <c>PAF.Tests/Esquema/BarreiraExpandirContrairTests</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MigracaoDeContracaoAttribute(string motivo) : Attribute
{
    public string Motivo { get; } = motivo;
}
