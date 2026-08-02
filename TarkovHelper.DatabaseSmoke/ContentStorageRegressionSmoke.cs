using TarkovHelper.Services;

internal static class ContentStorageRegressionSmoke
{
    public static async Task RunAsync(string rebuiltDatabasePath)
    {
        var storage = ContentStorageService.Instance;
        if (storage.IsUsingBundledFallback)
        {
            throw new InvalidDataException(
                $"Mutable content storage unexpectedly fell back to bundled assets: {storage.InitializationError}");
        }

        if (string.Equals(
                Path.GetFullPath(storage.DatabasePath),
                Path.GetFullPath(storage.BundledDatabasePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mutable content database still points at the application Assets folder.");
        }

        var rebuiltHash = ContentStorageService.ComputeSha256(rebuiltDatabasePath);
        var activeHash = ContentStorageService.ComputeSha256(storage.DatabasePath);
        if (!string.Equals(rebuiltHash, activeHash, StringComparison.Ordinal))
            throw new InvalidDataException("Initial mutable content seed did not copy the rebuilt database.");

        var initialManifest = storage.LoadCurrentManifest()
            ?? throw new InvalidDataException("Initial content manifest was not created.");
        if (initialManifest.SchemaVersion != ContentStorageService.CurrentManifestSchemaVersion ||
            !string.Equals(initialManifest.GameMode, "PVP", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Initial content manifest is not PVP-compatible.");
        }

        var staging = storage.PrepareStaging();
        var stagedManifest = storage.LoadCurrentManifest()
            ?? throw new InvalidDataException("Current content manifest disappeared before staging.");
        stagedManifest.Source = "content-storage-smoke";
        stagedManifest.UpdatedAt = DateTimeOffset.UtcNow;
        await storage.SaveManifestAsync(staging.ManifestPath, stagedManifest);
        storage.CommitStaging();

        if (!storage.HasPreviousContent)
            throw new InvalidDataException("Committed content did not retain a rollback set.");
        if (storage.LoadCurrentManifest()?.Source != "content-storage-smoke")
            throw new InvalidDataException("Staged manifest was not activated with the content set.");

        storage.RestorePrevious();
        if (storage.LoadCurrentManifest()?.Source == "content-storage-smoke")
            throw new InvalidDataException("Previous content restore did not reactivate the original set.");
        if (!string.Equals(
                ContentStorageService.ComputeSha256(storage.DatabasePath),
                rebuiltHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Previous content restore changed the active database bytes.");
        }

        var invalidStaging = storage.PrepareStaging();
        var invalidManifest = storage.LoadCurrentManifest()
            ?? throw new InvalidDataException("Current manifest disappeared before compatibility smoke.");
        invalidManifest.SchemaVersion = ContentStorageService.CurrentManifestSchemaVersion + 1;
        await storage.SaveManifestAsync(invalidStaging.ManifestPath, invalidManifest);

        var rejected = false;
        try
        {
            storage.CommitStaging();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        finally
        {
            storage.DiscardStaging();
        }

        if (!rejected)
            throw new InvalidDataException("An incompatible content manifest was allowed to become active.");
        if (!string.Equals(
                ContentStorageService.ComputeSha256(storage.DatabasePath),
                rebuiltHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Rejected staging content modified the active database.");
        }
    }
}
