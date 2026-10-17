using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.PAF.Data.Configurations;

public sealed class EstacaoConfiguration : IEntityTypeConfiguration<Estacao>
{
    public void Configure(EntityTypeBuilder<Estacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Estacoes");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Nome).IsRequired().HasMaxLength(120);
        builder.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Configuracao).HasColumnType("jsonb");
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class EstacaoFilaConfiguration : IEntityTypeConfiguration<EstacaoFila>
{
    public void Configure(EntityTypeBuilder<EstacaoFila> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("EstacaoFilas");
        builder.HasKey(x => new { x.EstacaoId, x.Papel, x.FilaId });
        builder.Property(x => x.Papel).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(x => x.FilaId);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Estacao>().WithMany().HasForeignKey(x => x.EstacaoId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Fila>().WithMany().HasForeignKey(x => x.FilaId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EstacaoPrioridadeConfiguration : IEntityTypeConfiguration<EstacaoPrioridade>
{
    public void Configure(EntityTypeBuilder<EstacaoPrioridade> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("EstacaoPrioridades");
        builder.HasKey(x => new { x.EstacaoId, x.PrioridadeId });
        builder.HasIndex(x => x.PrioridadeId);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Estacao>().WithMany().HasForeignKey(x => x.EstacaoId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Prioridade>().WithMany().HasForeignKey(x => x.PrioridadeId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class VinculoEstacaoConfiguration : IEntityTypeConfiguration<VinculoEstacao>
{
    public void Configure(EntityTypeBuilder<VinculoEstacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("VinculosEstacao");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.LinkHash).IsRequired().HasMaxLength(VinculoEstacao.HashMaxLength);
        builder.Property(x => x.CredencialEmissaoHash).HasMaxLength(VinculoEstacao.HashMaxLength);
        builder.Property(x => x.CredencialPainelHash).HasMaxLength(VinculoEstacao.HashMaxLength);
        builder.Property(x => x.CriadoEm).HasDefaultValueSql(PafSql.Agora);
        builder.HasIndex(x => x.LinkHash).IsUnique().HasDatabaseName("UX_VinculosEstacao_LinkHash");
        builder.HasIndex(x => x.EstacaoId);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Estacao>().WithMany().HasForeignKey(x => x.EstacaoId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class SessaoEstacaoConfiguration : IEntityTypeConfiguration<SessaoEstacao>
{
    public void Configure(EntityTypeBuilder<SessaoEstacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("SessoesEstacao", t => t.HasCheckConstraint(
            "CK_SessoesEstacao_DonoUnico",
            "(\"SessaoId\" IS NULL) <> (\"ConcessaoId\" IS NULL)"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SelecionadoEm).HasDefaultValueSql(PafSql.Agora);

        // Active link = LiberadoEm IS NULL. One guichê per owner and one owner per guichê; session and
        // grant share the station index, so support selecting an occupied guichê fails like a colleague would.
        builder.HasIndex(x => x.EstacaoId).IsUnique().HasFilter(PafSql.VinculoAtivo)
            .HasDatabaseName("UX_SessoesEstacao_EstacaoAtiva");
        builder.HasIndex(x => x.SessaoId).IsUnique().HasFilter(PafSql.VinculoAtivo)
            .HasDatabaseName("UX_SessoesEstacao_SessaoAtiva");
        builder.HasIndex(x => x.ConcessaoId).IsUnique().HasFilter(PafSql.VinculoAtivo)
            .HasDatabaseName("UX_SessoesEstacao_ConcessaoAtiva");

        builder.HasOne<Estacao>().WithMany().HasForeignKey(x => x.EstacaoId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class AtividadeSessaoConfiguration : IEntityTypeConfiguration<AtividadeSessao>
{
    public void Configure(EntityTypeBuilder<AtividadeSessao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("AtividadesSessao");
        builder.HasKey(x => x.SessaoId);
        builder.Property(x => x.SessaoId).ValueGeneratedNever();
        builder.Property(x => x.UltimaAtividadeEm).HasDefaultValueSql(PafSql.Agora);
        builder.HasIndex(x => new { x.UnidadeId, x.UltimaAtividadeEm }).HasDatabaseName("IX_AtividadesSessao_UnidadeAtividade");
        builder.HasIndex(x => x.UsuarioId);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
    }
}
