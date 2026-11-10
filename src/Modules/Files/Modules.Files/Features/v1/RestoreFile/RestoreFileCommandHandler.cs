using FSH.Framework.Core.Exceptions;
using FSH.Framework.Persistence;
using FSH.Modules.Files.Contracts.v1.Commands;
using FSH.Modules.Files.Data;
using FSH.Modules.Files.Localization;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Files.Features.v1.RestoreFile;

public sealed class RestoreFileCommandHandler(FilesDbContext db)
    : ICommandHandler<RestoreFileCommand, Unit>
{
    public async ValueTask<Unit> Handle(RestoreFileCommand cmd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // Bypasses ONLY the soft-delete filter, which would otherwise hide the row; the tenant filter stays, so
        // another tenant's file is not found.
        var f = await db.FileAssets
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .FirstOrDefaultAsync(x => x.Id == cmd.FileAssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("file not found").Localized<FilesResources>("Files.FileNotFound");

        if (!f.IsDeleted)
        {
            return Unit.Value; // idempotent — already live
        }

        f.Restore();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
