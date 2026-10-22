using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.PAF.Data.Configurations;

public sealed class FichaConfiguration : IEntityTypeConfiguration<Ficha>
{
    public void Configure(EntityTypeBuilder<Ficha> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Fichas");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Letra).IsRequired().HasMaxLength(FilaPrioridade.LetraMaxLength);
        builder.Property(x => x.Canal).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.ChaveIdempotencia).IsRequired().HasMaxLength(Ficha.ChaveIdempotenciaMaxLength);
        builder.Property(x => x.CriadoEm).HasDefaultValueSql(PafSql.Agora);
        builder.Property(x => x.DataOperacional).HasComputedColumnSql(PafSql.DataEmSaoPaulo(nameof(Ficha.CriadoEm)), stored: true);

        // Two totens computing the same next number: the second insert fails here and retries in a new transaction (ADR 0005).
        builder.HasIndex(x => new { x.UnidadeId, x.FilaId, x.PrioridadeId, x.DataOperacional, x.Numero }).IsUnique()
            .HasDatabaseName("UX_Fichas_Numeracao");
        // A resend after a lost response fails here and returns the stored ficha, with or without Valkey (seção 4.2).
        builder.HasIndex(x => new { x.EstacaoId, x.ChaveIdempotencia }).IsUnique()
            .HasDatabaseName("UX_Fichas_ChaveIdempotencia");
        builder.HasIndex(x => x.IdentificadorPublico).IsUnique().HasDatabaseName("UX_Fichas_IdentificadorPublico");
        builder.HasIndex(x => new { x.UnidadeId, x.DataOperacional }).HasDatabaseName("IX_Fichas_UnidadeData");

        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Fila>().WithMany().HasForeignKey(x => x.FilaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Prioridade>().WithMany().HasForeignKey(x => x.PrioridadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Estacao>().WithMany().HasForeignKey(x => x.EstacaoId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class AtendimentoConfiguration : IEntityTypeConfiguration<Atendimento>
{
    public void Configure(EntityTypeBuilder<Atendimento> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Atendimentos");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Estado).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.CriadoEm).HasDefaultValueSql(PafSql.Agora);
        builder.Property(x => x.ElegivelDesde).HasDefaultValueSql(PafSql.Agora);
        builder.Property(x => x.DataOperacional).HasComputedColumnSql(PafSql.DataEmSaoPaulo(nameof(Atendimento.CriadoEm)), stored: true);
        builder.Property(x => x.DataElegibilidade).HasComputedColumnSql(PafSql.DataEmSaoPaulo(nameof(Atendimento.ElegivelDesde)), stored: true);
        builder.Property(x => x.Versao).HasDefaultValue(0);

        // At most one atendimento in progress per ficha and per guichê. Cancel, recall and rehabilitate
        // never enter these states, so they are serialized by FOR UPDATE, not by these indexes (seção 8).
        builder.HasIndex(x => x.FichaId).IsUnique().HasFilter(PafSql.EstadoEmCurso)
            .HasDatabaseName("UX_Atendimentos_FichaEmCurso");
        builder.HasIndex(x => x.EstacaoId).IsUnique().HasFilter(PafSql.EstadoEmCurso)
            .HasDatabaseName("UX_Atendimentos_EstacaoEmCurso");
        builder.HasIndex(x => new { x.UnidadeId, x.DataOperacional }).HasDatabaseName("IX_Atendimentos_UnidadeData");
        builder.HasIndex(x => new { x.UnidadeId, x.FilaId, x.DataOperacional }).HasDatabaseName("IX_Atendimentos_UnidadeFilaData");
        // Candidate query of the call (hottest path): only waiting rows, so the index stays the size of the live queue.
        builder.HasIndex(x => new { x.UnidadeId, x.FilaId, x.DataElegibilidade }).HasFilter(PafSql.EstadoAguardando)
            .HasDatabaseName("IX_Atendimentos_Candidatos");

        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Ficha>().WithMany().HasForeignKey(x => x.FichaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Fila>().WithMany().HasForeignKey(x => x.FilaId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Prioridade>().WithMany().HasForeignKey(x => x.PrioridadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Estacao>().WithMany().HasForeignKey(x => x.EstacaoId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Atendimento>().WithMany().HasForeignKey(x => x.AtendimentoOrigemId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class RechamadaConfiguration : IEntityTypeConfiguration<Rechamada>
{
    public void Configure(EntityTypeBuilder<Rechamada> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("Rechamadas");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.RechamadoEm).HasDefaultValueSql(PafSql.Agora);
        builder.HasIndex(x => x.AtendimentoId);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Atendimento>().WithMany().HasForeignKey(x => x.AtendimentoId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Estacao>().WithMany().HasForeignKey(x => x.EstacaoId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}

public sealed class AtoImpersonacaoConfiguration : IEntityTypeConfiguration<AtoImpersonacao>
{
    public void Configure(EntityTypeBuilder<AtoImpersonacao> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("AtosImpersonacao");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.RegistradoEm).HasDefaultValueSql(PafSql.Agora);
        builder.HasIndex(x => x.ConcessaoId);
        builder.HasIndex(x => x.AtendimentoId);
        builder.HasOne<Unidade>().WithMany().HasForeignKey(x => x.UnidadeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Atendimento>().WithMany().HasForeignKey(x => x.AtendimentoId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.DomainEvents);
    }
}
