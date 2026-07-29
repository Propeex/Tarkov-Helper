using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (ProfileService.Instance.CurrentProfile != ProfileType.Pvp)
    throw new InvalidOperationException("The application profile is not locked to PVP.");

if (args.Contains("--external", StringComparer.OrdinalIgnoreCase))
{
    return await RunExternalApiSmokeAsync();
}

return await RunDeterministicDatabaseSmokeAsync();

static async Task<int> RunDeterministicDatabaseSmokeAsync()
{
    var databasePath = Path.Combine(AppContext.BaseDirectory, "Assets", "tarkov_data.db");

    await RunOutageHandlingSmokeAsync(databasePath);

    // The deterministic fixture intentionally contains only two quests. Remove
    // legacy alternative-quest rows from the copied production DB so this test
    // validates the rebuilt API-managed tables rather than unrelated fixture gaps.
    await using (var cleanupConnection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ConnectionString))
    {
        await cleanupConnection.OpenAsync();
        await using var cleanupCommand = cleanupConnection.CreateCommand();
        cleanupCommand.CommandText = "DELETE FROM OptionalQuests;";
        await cleanupCommand.ExecuteNonQueryAsync();
    }

    await PrepareLegacyQuestCoordinateFixtureAsync(databasePath);

    var fixtureHandler = new FixtureTarkovApiHandler();
    using var httpClient = new HttpClient(
        new TarkovJsonObjectiveIdProtectionHandler(
            new ObjectiveIdCollisionFixtureHandler(fixtureHandler)))
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    var builder = new TarkovDataDatabaseBuilder(
        httpClient,
        progress => Console.WriteLine($"[{progress.Percent,6:F1}%] {progress.Message}"));

    var result = await builder.BuildPreferredAsync(databasePath);

    if (fixtureHandler.StaticRequestCount == 0)
        throw new InvalidDataException("The static JSON API path was not exercised.");
    if (fixtureHandler.GraphQlRequestCount != 0)
        throw new InvalidDataException("The deterministic static JSON test unexpectedly used GraphQL fallback.");

    var objectiveLoaderSucceeded = await QuestObjectiveDbService.Instance.LoadObjectivesAsync();
    var loadedCoordinateObjectives = QuestObjectiveDbService.Instance.AllObjectives.Count;
    if (!objectiveLoaderSucceeded || loadedCoordinateObjectives == 0)
    {
        throw new InvalidDataException(
            $"Quest marker coordinates were not loadable after rebuild: " +
            $"success={objectiveLoaderSucceeded}, coordinates={loadedCoordinateObjectives}.");
    }

    // Real tarkov.dev data contains distinct quests with the same English title
    // (for example Battery Change). Reproduce that condition after the builder
    // succeeds and verify that the application DB loader keeps both quests.
    await ForceDuplicateQuestNamesAsync(databasePath);
    var questLoaderSucceeded = await QuestDbService.Instance.LoadQuestsAsync();
    var loadedQuests = QuestDbService.Instance.AllQuests.ToList();
    var uniqueQuestKeys = loadedQuests
        .Select(quest => quest.NormalizedName)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
    var disambiguatedQuestKeys = loadedQuests.Count(quest =>
        quest.NormalizedName?.StartsWith("duplicate-fixture-quest--", StringComparison.OrdinalIgnoreCase) == true);

    if (!questLoaderSucceeded || loadedQuests.Count != result.QuestCount)
    {
        throw new InvalidDataException(
            $"QuestDbService failed to load duplicate-name quests: success={questLoaderSucceeded}, " +
            $"loaded={loadedQuests.Count}, expected={result.QuestCount}.");
    }
    if (uniqueQuestKeys != result.QuestCount || disambiguatedQuestKeys != 1)
    {
        throw new InvalidDataException(
            $"Duplicate quest names were not assigned stable unique keys: " +
            $"unique={uniqueQuestKeys}, disambiguated={disambiguatedQuestKeys}.");
    }
    if (QuestDbService.Instance.GetQuestById("fixture-quest-first") == null ||
        QuestDbService.Instance.GetQuestById("fixture-quest-second") == null)
    {
        throw new InvalidDataException("Quest ID lookup lost one of the duplicate-name quests.");
    }

    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ConnectionString;

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    var koreanItems = await ScalarAsync(connection,
        "SELECT COUNT(*) FROM Items WHERE NameKO IS NOT NULL AND NameKO != '' AND NameKO != NameEN;");
    var koreanQuests = await ScalarAsync(connection,
        "SELECT COUNT(*) FROM Quests WHERE NameKO IS NOT NULL AND NameKO != '' AND NameKO != NameEN;");
    var questItemLinks = await ScalarAsync(connection,
        "SELECT COUNT(*) FROM QuestRequiredItems q JOIN Items i ON q.ItemId = i.Id;");
    var hideoutItemLinks = await ScalarAsync(connection,
        "SELECT COUNT(*) FROM HideoutItemRequirements h JOIN Items i ON h.ItemId = i.BsgId;");
    var iconLinks = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM Items
        WHERE IconUrl LIKE 'http://%' OR IconUrl LIKE 'https://%';
        """);
    var restrictedNeutralQuests = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM Quests
        WHERE LOWER(TRIM(COALESCE(Faction, ''))) IN ('any', 'any target', 'all', 'both', 'pmc');
        """);
    var sellItemRequirements = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequiredItems
        WHERE LOWER(COALESCE(RequirementType, '')) = 'sellitem';
        """);
    var dogtagAlternativeRows = await ScalarAsync(connection, $"""
        SELECT COUNT(*)
        FROM QuestRequiredItems
        WHERE ObjectiveId = '{ObjectiveIdCollisionFixtureHandler.DogtagObjectiveId}';
        """);
    var canonicalDogtagRows = await ScalarAsync(connection, $"""
        SELECT COUNT(*)
        FROM QuestRequiredItems r
        JOIN Items i ON i.Id = r.ItemId
        WHERE r.ObjectiveId = '{ObjectiveIdCollisionFixtureHandler.DogtagObjectiveId}'
          AND i.NormalizedName = 'dogtag-usec'
          AND r.Count = 7
          AND r.DogtagMinLevel = 50;
        """);
    var preservedCoordinateRows = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestObjectives o
        JOIN Quests q ON q.Id = o.QuestId
        WHERE q.BsgId = 'fixture-quest-first'
          AND (
              (o.LocationPoints IS NOT NULL AND o.LocationPoints != '')
              OR (o.OptionalPoints IS NOT NULL AND o.OptionalPoints != '')
          );
        """);
    var duplicateDisplayNames = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM (
            SELECT LOWER(Name)
            FROM Quests
            GROUP BY LOWER(Name)
            HAVING COUNT(*) > 1
        );
        """);
    var protectedObjectiveIds = await ScalarAsync(connection, $"""
        SELECT COUNT(*)
        FROM QuestObjectives
        WHERE Id IN (
            '{ObjectiveIdCollisionFixtureHandler.SharedObjectiveId}',
            '{ObjectiveIdCollisionFixtureHandler.ScopedSecondObjectiveId}'
        );
        """);
    var correctlyScopedObjectives = await ScalarAsync(connection, $"""
        SELECT COUNT(*)
        FROM QuestObjectives o
        JOIN Quests q ON q.Id = o.QuestId
        WHERE (o.Id = '{ObjectiveIdCollisionFixtureHandler.SharedObjectiveId}'
               AND q.BsgId = 'fixture-quest-first')
           OR (o.Id = '{ObjectiveIdCollisionFixtureHandler.ScopedSecondObjectiveId}'
               AND q.BsgId = 'fixture-quest-second');
        """);
    var duplicateObjectiveIds = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM (
            SELECT Id
            FROM QuestObjectives
            GROUP BY Id
            HAVING COUNT(*) > 1
        );
        """);
    var duplicateLocalizedDescriptions = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestObjectives
        WHERE Description = '주사기 건네주기';
        """);
    var missingChildIds = await ScalarAsync(connection, """
        SELECT
          (SELECT COUNT(*) FROM QuestRequirements WHERE Id IS NULL OR Id = '') +
          (SELECT COUNT(*) FROM QuestRequiredItems WHERE Id IS NULL OR Id = '') +
          (SELECT COUNT(*) FROM HideoutItemRequirements WHERE Id IS NULL OR Id = '') +
          (SELECT COUNT(*) FROM HideoutStationRequirements WHERE Id IS NULL OR Id = '') +
          (SELECT COUNT(*) FROM HideoutTraderRequirements WHERE Id IS NULL OR Id = '') +
          (SELECT COUNT(*) FROM HideoutSkillRequirements WHERE Id IS NULL OR Id = '');
        """);
    var invalidMaxLevels = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM HideoutStations s
        WHERE s.MaxLevel != COALESCE((
            SELECT MAX(l.Level) FROM HideoutLevels l WHERE l.StationId = s.Id
        ), 0);
        """);

    if (result.ItemCount != 4 || result.QuestCount != 2 || result.HideoutStationCount != 1)
        throw new InvalidDataException("Fixture row counts do not match the generated database.");
    if (koreanItems < 4 || koreanQuests < 2)
        throw new InvalidDataException("Korean localized names were not written correctly.");
    if (questItemLinks < 3 || hideoutItemLinks < 1)
        throw new InvalidDataException("Quest or hideout item links were not persisted correctly.");
    if (iconLinks != result.ItemCount)
        throw new InvalidDataException($"Item icon URLs were not persisted: {iconLinks}/{result.ItemCount}.");
    if (restrictedNeutralQuests != 0)
        throw new InvalidDataException($"Neutral quests still contain a faction restriction: {restrictedNeutralQuests}.");
    if (sellItemRequirements != 0)
        throw new InvalidDataException($"Sell catalogues leaked into quest item requirements: {sellItemRequirements}.");
    if (dogtagAlternativeRows != 1 || canonicalDogtagRows != 1)
    {
        throw new InvalidDataException(
            $"Dogtag alternatives were not collapsed into one faction requirement: " +
            $"rows={dogtagAlternativeRows}, canonical={canonicalDogtagRows}.");
    }
    if (preservedCoordinateRows < 1 || loadedCoordinateObjectives < 1)
    {
        throw new InvalidDataException(
            $"Legacy quest marker coordinates were not preserved: " +
            $"database={preservedCoordinateRows}, loader={loadedCoordinateObjectives}.");
    }
    if (duplicateDisplayNames != 1)
        throw new InvalidDataException($"Duplicate-name loader fixture was not created: groups={duplicateDisplayNames}.");
    if (protectedObjectiveIds != 2 || correctlyScopedObjectives != 2 || duplicateObjectiveIds != 0)
    {
        throw new InvalidDataException(
            $"Globally duplicate objective IDs were not scoped correctly: " +
            $"ids={protectedObjectiveIds}, scoped={correctlyScopedObjectives}, duplicates={duplicateObjectiveIds}.");
    }
    if (duplicateLocalizedDescriptions != 2)
    {
        throw new InvalidDataException(
            $"Localized objective descriptions were corrupted: localized={duplicateLocalizedDescriptions}.");
    }
    if (missingChildIds != 0)
        throw new InvalidDataException($"Rebuilt child rows contain {missingChildIds} missing primary keys.");
    if (invalidMaxLevels != 0)
        throw new InvalidDataException($"Rebuilt hideout stations contain {invalidMaxLevels} invalid maximum levels.");

    RunApplicationBehaviorSmoke();

    Console.WriteLine(
        $"Deterministic database smoke passed: profile=PVP, transport=static-json, " +
        $"requests={fixtureHandler.StaticRequestCount}, items={result.ItemCount}, quests={result.QuestCount}, " +
        $"hideout={result.HideoutStationCount}, questLinks={questItemLinks}, hideoutLinks={hideoutItemLinks}, " +
        $"iconLinks={iconLinks}, neutralRestrictions={restrictedNeutralQuests}, sellItemRows={sellItemRequirements}, " +
        $"dogtagRows={dogtagAlternativeRows}, canonicalDogtags={canonicalDogtagRows}, " +
        $"markerRows={preservedCoordinateRows}, markerLoader={loadedCoordinateObjectives}, " +
        $"duplicateDisplayNames={duplicateDisplayNames}, questLoader={loadedQuests.Count}, " +
        $"uniqueQuestKeys={uniqueQuestKeys}, disambiguatedQuestKeys={disambiguatedQuestKeys}, " +
        $"objectiveIds={protectedObjectiveIds}, scopedObjectives={correctlyScopedObjectives}, " +
        $"duplicateObjectiveIds={duplicateObjectiveIds}, " +
        $"duplicateLocalizedObjectives={duplicateLocalizedDescriptions}, " +
        $"missingIds={missingChildIds}, invalidMaxLevels={invalidMaxLevels}");
    return 0;
}

static async Task PrepareLegacyQuestCoordinateFixtureAsync(string databasePath)
{
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ConnectionString;

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    string? questId;
    await using (var questCommand = connection.CreateCommand())
    {
        questCommand.CommandText = "SELECT Id FROM Quests ORDER BY Id LIMIT 1;";
        questId = (await questCommand.ExecuteScalarAsync())?.ToString();
    }

    if (string.IsNullOrWhiteSpace(questId))
        throw new InvalidDataException("Could not locate a quest row for the marker preservation fixture.");

    long coordinateRowId;
    await using (var objectiveCommand = connection.CreateCommand())
    {
        objectiveCommand.CommandText = """
            SELECT rowid
            FROM QuestObjectives
            WHERE (LocationPoints IS NOT NULL AND LocationPoints != '')
               OR (OptionalPoints IS NOT NULL AND OptionalPoints != '')
            ORDER BY rowid
            LIMIT 1;
            """;
        coordinateRowId = Convert.ToInt64(await objectiveCommand.ExecuteScalarAsync() ?? 0);
    }

    if (coordinateRowId <= 0)
        throw new InvalidDataException("The bundled database has no quest marker coordinate row for the fixture.");

    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

    await using (var questUpdate = connection.CreateCommand())
    {
        questUpdate.Transaction = transaction;
        questUpdate.CommandText = """
            UPDATE Quests
            SET BsgId = 'fixture-quest-first'
            WHERE Id = @questId;
            """;
        questUpdate.Parameters.AddWithValue("@questId", questId);
        if (await questUpdate.ExecuteNonQueryAsync() != 1)
            throw new InvalidDataException("Could not prepare the fixture quest ID mapping.");
    }

    await using (var objectiveUpdate = connection.CreateCommand())
    {
        objectiveUpdate.Transaction = transaction;
        objectiveUpdate.CommandText = """
            UPDATE QuestObjectives
            SET Id = 'legacy-fixture-coordinate',
                QuestId = @questId,
                Description = 'Hand over syringe',
                DescriptionEN = 'Hand over syringe',
                DescriptionKO = '주사기 건네주기',
                ObjectiveType = 'findItem'
            WHERE rowid = @rowId;
            """;
        objectiveUpdate.Parameters.AddWithValue("@questId", questId);
        objectiveUpdate.Parameters.AddWithValue("@rowId", coordinateRowId);
        if (await objectiveUpdate.ExecuteNonQueryAsync() != 1)
            throw new InvalidDataException("Could not prepare the marker coordinate fixture.");
    }

    await transaction.CommitAsync();
}

static async Task ForceDuplicateQuestNamesAsync(string databasePath)
{
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ConnectionString;

    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE Quests
        SET Name = 'Duplicate Fixture Quest',
            NameEN = 'Duplicate Fixture Quest'
        WHERE BsgId IN ('fixture-quest-first', 'fixture-quest-second');
        """;
    var changed = await command.ExecuteNonQueryAsync();
    if (changed != 2)
        throw new InvalidDataException($"Could not create duplicate quest-name fixture: changed={changed}.");
}

static async Task RunOutageHandlingSmokeAsync(string databasePath)
{
    var outageHandler = new TarkovApiOutageFixtureHandler();
    using var httpClient = new HttpClient(
        new TarkovJsonObjectiveIdProtectionHandler(outageHandler))
    {
        Timeout = TimeSpan.FromMinutes(1)
    };

    var progressMessages = new List<string>();
    var builder = new TarkovDataDatabaseBuilder(
        httpClient,
        progress => progressMessages.Add(progress.ToDisplayText()));

    var stopwatch = Stopwatch.StartNew();
    string? failureMessage = null;

    try
    {
        await builder.BuildPreferredAsync(databasePath);
    }
    catch (InvalidOperationException exception)
    {
        failureMessage = exception.Message;
    }

    stopwatch.Stop();

    if (failureMessage == null ||
        !failureMessage.Contains("정적 JSON API와 GraphQL API가 모두 응답하지 않았습니다", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Outage fixture did not return the expected combined API error: {failureMessage ?? "no error"}");
    }

    if (outageHandler.StaticRequestCount != 1 || outageHandler.GraphQlRequestCount != 1)
    {
        throw new InvalidDataException(
            $"Outage handling retried unavailable endpoints: static={outageHandler.StaticRequestCount}, " +
            $"graphql={outageHandler.GraphQlRequestCount}.");
    }

    if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
        throw new InvalidDataException($"Outage handling was too slow: {stopwatch.Elapsed}.");

    var misleadingEta = new DatabaseBuildProgress(
        "API",
        "서버 응답 확인 중",
        1,
        0,
        null,
        TimeSpan.FromMinutes(2),
        TimeSpan.FromHours(3)).ToDisplayText();
    if (misleadingEta.Contains("예상", StringComparison.Ordinal))
        throw new InvalidDataException($"API progress still exposes a misleading ETA: {misleadingEta}");

    if (File.Exists(databasePath + ".rebuild.tmp"))
        throw new InvalidDataException("Outage handling left a temporary database behind.");

    Console.WriteLine(
        $"Outage handling smoke passed: elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms, " +
        $"static={outageHandler.StaticRequestCount}, graphql={outageHandler.GraphQlRequestCount}, etaHidden=true");
}

static void RunApplicationBehaviorSmoke()
{
    const string inventoryKey = "__maintenance-consumption-smoke__";
    var inventory = ItemInventoryService.Instance;
    inventory.SetFirQuantity(inventoryKey, 3);
    inventory.SetNonFirQuantity(inventoryKey, 5);

    var generalResult = inventory.ConsumeBatch([
        new InventoryConsumptionRequirement(inventoryKey, 4, FirOnly: false)
    ]);
    if (generalResult.Consumed != 4 ||
        inventory.GetNonFirQuantity(inventoryKey) != 1 ||
        inventory.GetFirQuantity(inventoryKey) != 3)
    {
        throw new InvalidDataException(
            "General inventory consumption did not preserve FIR stock or subtract the expected quantity.");
    }

    var firResult = inventory.ConsumeBatch([
        new InventoryConsumptionRequirement(inventoryKey, 5, FirOnly: true)
    ]);
    if (firResult.Consumed != 3 || firResult.Missing != 2 ||
        inventory.GetFirQuantity(inventoryKey) != 0 ||
        inventory.GetNonFirQuantity(inventoryKey) != 1)
    {
        throw new InvalidDataException(
            "FIR-only inventory consumption did not clamp at the available FIR quantity.");
    }

    inventory.SetNonFirQuantity(inventoryKey, 0);

    var statusTask = new TarkovTask
    {
        Ids = ["actual-status-smoke"],
        NormalizedName = "actual-status-smoke",
        Name = "Actual Status Smoke"
    };
    var availableStatus = new ActualQuestStatusEvaluator(
        QuestProgressService.Instance,
        []).Evaluate(statusTask);
    var activeStatus = new ActualQuestStatusEvaluator(
        QuestProgressService.Instance,
        ["actual-status-smoke"]).Evaluate(statusTask);
    if (availableStatus != QuestStatus.Available || activeStatus != QuestStatus.Active)
    {
        throw new InvalidDataException(
            $"Actual quest status separation failed: available={availableStatus}, active={activeStatus}.");
    }

    var categories = ItemsDataService.Instance;
    if (categories.GetParentCategory("Scopes") != "WeaponParts" ||
        categories.GetParentCategory("Magazines") != "Ammunition" ||
        categories.GetParentCategory("unrecognized-category") != "Other")
    {
        throw new InvalidDataException("Practical item category grouping failed.");
    }
}

static async Task<int> RunExternalApiSmokeAsync()
{
    var service = DatabaseUpdateService.Instance;
    service.ProgressChanged += (_, progress) =>
    {
        Console.WriteLine($"[{progress.Percent,6:F1}%] {progress.Message}");
    };

    try
    {
        var result = await service.CheckAndUpdateAsync();
        Console.WriteLine(result.Message);
        return result.Success && result.WasUpdated ? 0 : 1;
    }
    finally
    {
        service.Dispose();
    }
}

static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}