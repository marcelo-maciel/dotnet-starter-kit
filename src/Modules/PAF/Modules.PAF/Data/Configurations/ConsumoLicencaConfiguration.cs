using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.PAF.Data.Configurations;

public sealed class ConsumoLicencaConfiguration : IEntityTypeConfiguration<ConsumoLicenca>
{
    /// <summary>One sample per unidade and sampling window: a second run in the same window inserts nothing.</summary>
    public const string IndiceAmostra = "UX_ConsumosLicenca_Amostra";

    public const string IndiceSessao = "IX_ConsumosLicenca_Sessao";

    public void Configure(EntityTypeBuilder<ConsumoLicenca> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ConsumosLicenca");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.RegistradoEm).HasDefaultValueSql(PafSql.Agora);
        builder.HasIndex(x => new { x.UnidadeId, x.RegistradoEm }).IsUnique().HasFilter("\"Tipo\" = 'Amostra'")
            .HasDatabaseName(IndiceAmostra);
        builder.HasIndex(x => x.SessaoId).HasDatabaseName(IndiceSessao);
        // No FK on SessaoId: the session is a kit row (UserSession) in another schema, and the record outlives it.
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}
