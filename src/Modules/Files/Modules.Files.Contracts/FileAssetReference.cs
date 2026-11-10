namespace FSH.Modules.Files.Contracts;

/// <summary>Owning-feature handle to a FileAsset. Stored on the owning module's join tables.</summary>
public sealed record FileAssetReference(Guid Id, string OwnerType, Guid? OwnerId);
