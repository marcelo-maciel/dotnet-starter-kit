using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.PAF.Data.Configurations;

// Same-unit rule of the media map: every reference between media rows carries UnidadeId in the foreign
// key, against an alternate key (UnidadeId, ...) of the parent, so a row of unidade A can never point to
// a mídia, envio or estação of unidade B even through raw SQL that skips the query filter.

public sealed class EnvioMidiaConfiguration : IEntityTypeConfiguration<EnvioMidia>
{
    public void Configure(EntityTypeBuilder<EnvioMidia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("EnviosMidia", t =>
        {
            t.HasCheckConstraint("CK_EnviosMidia_Tipo", MidiaSql.TipoValido);
            t.HasCheckConstraint("CK_EnviosMidia_Desfecho", "\"ConfirmadoEm\" IS NULL OR \"RecusadoEm\" IS NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ChaveObjeto).IsRequired().HasMaxLength(EnvioMidia.ChaveObjetoMaxLength);
        builder.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ContentType).IsRequired().HasMaxLength(EnvioMidia.ContentTypeMaxLength);
        builder.Property(x => x.CriadoEm).HasDefaultValueSql(PafSql.Agora);

        // The object key is never reused (RF03): two envios racing for one key, one fails here.
        builder.HasIndex(x => x.ChaveObjeto).IsUnique().HasDatabaseName("UX_EnviosMidia_ChaveObjeto");

        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class MidiaConfiguration : IEntityTypeConfiguration<Midia>
{
    public void Configure(EntityTypeBuilder<Midia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Midias", t =>
        {
            t.HasCheckConstraint("CK_Midias_Tipo", MidiaSql.TipoValido);
            t.HasCheckConstraint("CK_Midias_Duracao",
                $"\"DuracaoSegundos\" BETWEEN 1 AND {Midia.DuracaoMaximaSegundos}");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Descricao).IsRequired().HasMaxLength(Midia.DescricaoMaxLength);
        builder.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ChaveObjeto).IsRequired().HasMaxLength(EnvioMidia.ChaveObjetoMaxLength);
        builder.Property(x => x.Versao).HasDefaultValue(0);
        builder.Property(x => x.CriadoEm).HasDefaultValueSql(PafSql.Agora);

        // One object serves one mídia: confirming the same envio twice finds the mídia already created.
        builder.HasIndex(x => x.ChaveObjeto).IsUnique().HasDatabaseName("UX_Midias_ChaveObjeto");

        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        // The principal key includes Id: an alternate key on (UnidadeId, ChaveObjeto) alone is created before
        // UX_EnviosMidia_ChaveObjeto and would be the constraint PostgreSQL names for a duplicated key.
        builder.HasOne<EnvioMidia>().WithMany()
            .HasForeignKey(x => new { x.UnidadeId, x.EnvioId, x.ChaveObjeto })
            .HasPrincipalKey(e => new { e.UnidadeId, e.Id, e.ChaveObjeto })
            .HasConstraintName("FK_Midias_EnviosMidia_MesmaUnidade")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class ProgramacaoMidiaConfiguration : IEntityTypeConfiguration<ProgramacaoMidia>
{
    public void Configure(EntityTypeBuilder<ProgramacaoMidia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ProgramacoesMidia", t =>
        {
            t.HasCheckConstraint("CK_ProgramacoesMidia_Periodo", "\"DataFinal\" >= \"DataInicial\"");
            t.HasCheckConstraint("CK_ProgramacoesMidia_Repeticoes", "\"Repeticoes\" >= 1");
            t.HasCheckConstraint("CK_ProgramacoesMidia_Dias",
                $"\"Dias\" BETWEEN {(int)DiasSemana.Domingo} AND {(int)DiasSemana.Todos}");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Nome).IsRequired().HasMaxLength(ProgramacaoMidia.NomeMaxLength);
        builder.Property(x => x.Versao).HasDefaultValue(0);
        builder.Property(x => x.CriadoEm).HasDefaultValueSql(PafSql.Agora);

        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Midia>().WithMany()
            .HasForeignKey(x => new { x.UnidadeId, x.MidiaId })
            .HasPrincipalKey(m => new { m.UnidadeId, m.Id })
            .HasConstraintName("FK_ProgramacoesMidia_Midias_MesmaUnidade")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class PainelMidiaConfiguration : IEntityTypeConfiguration<PainelMidia>
{
    public void Configure(EntityTypeBuilder<PainelMidia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("PainelMidias");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => new { x.EstacaoId, x.Ordem }).IsUnique().HasDatabaseName("UX_PainelMidias_Ordem");

        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        // Declared from this side so EstacaoConfigurations.cs stays untouched; EF adds AK (UnidadeId, Id) on Estacoes.
        builder.HasOne<Estacao>().WithMany()
            .HasForeignKey(x => new { x.UnidadeId, x.EstacaoId })
            .HasPrincipalKey(e => new { e.UnidadeId, e.Id })
            .HasConstraintName("FK_PainelMidias_Estacoes_MesmaUnidade")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Midia>().WithMany()
            .HasForeignKey(x => new { x.UnidadeId, x.MidiaId })
            .HasPrincipalKey(m => new { m.UnidadeId, m.Id })
            .HasConstraintName("FK_PainelMidias_Midias_MesmaUnidade")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal static class MidiaSql
{
    public static readonly string TipoValido =
        $"\"Tipo\" IN ({string.Join(", ", Enum.GetNames<TipoMidia>().Select(n => $"'{n}'"))})";
}
