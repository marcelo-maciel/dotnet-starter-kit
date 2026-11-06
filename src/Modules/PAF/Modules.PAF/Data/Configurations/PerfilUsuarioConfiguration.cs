using FSH.Modules.PAF.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.PAF.Data.Configurations;

public sealed class PerfilUsuarioConfiguration : IEntityTypeConfiguration<PerfilUsuario>
{
    public void Configure(EntityTypeBuilder<PerfilUsuario> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("PerfisUsuario");
        builder.HasKey(x => x.UsuarioId);
        builder.Property(x => x.UsuarioId).ValueGeneratedNever();
        builder.Property(x => x.Funcao).HasMaxLength(PerfilUsuario.FuncaoMaxLength);
        builder.Property(x => x.Ramal).HasMaxLength(PerfilUsuario.RamalMaxLength);
    }
}
