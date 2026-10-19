using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.PAF.Data.Configurations;

public sealed class UnidadeConfiguration : IEntityTypeConfiguration<Unidade>
{
    public const string IndiceInicial = "UX_Unidades_Inicial";

    public void Configure(EntityTypeBuilder<Unidade> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Unidades");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Nome).IsRequired().HasMaxLength(160);
        builder.Property(x => x.DocumentoFiscal).HasMaxLength(32);
        builder.Property(x => x.CriadoEm).HasDefaultValueSql(PafSql.Agora);
        // Two deliveries of the tenant creation event race here: the second insert fails on this index (RF23, C03).
        builder.HasIndex(x => x.Inicial).IsUnique().HasFilter("\"Inicial\"").HasDatabaseName(UnidadeConfiguration.IndiceInicial);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class SetorConfiguration : IEntityTypeConfiguration<Setor>
{
    public void Configure(EntityTypeBuilder<Setor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Setores");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Nome).IsRequired().HasMaxLength(120);
        // Two setores of the same name would split one report line in two (RF07); another unidade may repeat it.
        builder.HasIndex(x => new { x.UnidadeId, x.Nome }).IsUnique().HasDatabaseName("UX_Setores_Nome");
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class FilaConfiguration : IEntityTypeConfiguration<Fila>
{
    public void Configure(EntityTypeBuilder<Fila> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Filas");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Nome).IsRequired().HasMaxLength(120);
        builder.Property(x => x.NomeExibicao).IsRequired().HasMaxLength(120);
        builder.Property(x => x.Fonte).HasMaxLength(64);
        builder.Property(x => x.CorFonte).HasMaxLength(32);
        builder.Property(x => x.CorBotaoSelecionado).HasMaxLength(32);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Setor>().WithMany().HasForeignKey(x => x.SetorId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class PrioridadeConfiguration : IEntityTypeConfiguration<Prioridade>
{
    public void Configure(EntityTypeBuilder<Prioridade> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Prioridades");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Nome).IsRequired().HasMaxLength(120);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class FilaPrioridadeConfiguration : IEntityTypeConfiguration<FilaPrioridade>
{
    public void Configure(EntityTypeBuilder<FilaPrioridade> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("FilaPrioridades");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Letra).IsRequired().HasMaxLength(FilaPrioridade.LetraMaxLength);
        builder.Property(x => x.CorBotao).HasMaxLength(32);

        builder.HasIndex(x => new { x.FilaId, x.PrioridadeId }).IsUnique().HasDatabaseName("UX_FilaPrioridades_FilaPrioridade");
        // Unique per unidade, not per fila: one painel calls several filas and announces only the senha (seção 4.2).
        builder.HasIndex(x => new { x.UnidadeId, x.Letra }).IsUnique().HasDatabaseName("UX_FilaPrioridades_Letra");

        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Fila>().WithMany().HasForeignKey(x => x.FilaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Prioridade>().WithMany().HasForeignKey(x => x.PrioridadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class ParametroConfiguration : IEntityTypeConfiguration<Parametro>
{
    public void Configure(EntityTypeBuilder<Parametro> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Parametros", t => t.HasCheckConstraint(
            "CK_Parametros_NivelUnidade",
            "(\"Nivel\" = 'Empresa') = (\"UnidadeId\" IS NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Nivel).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Chave).IsRequired().HasMaxLength(120);
        builder.Property(x => x.Valor).IsRequired().HasMaxLength(4096);
        // NULLS NOT DISTINCT: without it every empresa-level row (UnidadeId NULL) would dodge uniqueness.
        builder.HasIndex(x => new { x.Chave, x.UnidadeId }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName("UX_Parametros_Chave");
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class ContratoUnidadeConfiguration : IEntityTypeConfiguration<ContratoUnidade>
{
    public void Configure(EntityTypeBuilder<ContratoUnidade> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("ContratosUnidade");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Plano).IsRequired().HasMaxLength(120);
        builder.Property(x => x.Politica).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(x => new { x.UnidadeId, x.InicioVigencia }).HasDatabaseName("IX_ContratosUnidade_Vigencia");
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class UsuarioUnidadeConfiguration : IEntityTypeConfiguration<UsuarioUnidade>
{
    public void Configure(EntityTypeBuilder<UsuarioUnidade> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("UsuariosUnidade");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => new { x.UsuarioId, x.UnidadeId }).IsUnique().HasDatabaseName("UX_UsuariosUnidade_UsuarioUnidade");

        // UX_UsuariosUnidade_Ordem (UsuarioId, Ordem, TenantId) lives only in the migration, as a DEFERRABLE INITIALLY
        // DEFERRED constraint so a reorder swapping two positions is checked at commit. Not declared here: Npgsql has
        // no fluent API for it, and as a model index EF refuses the swap as a circular dependency before PostgreSQL.
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}
