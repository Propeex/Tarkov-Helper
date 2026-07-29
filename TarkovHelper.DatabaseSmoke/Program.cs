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

    var mapQuestLoaderSucceeded = await QuestObjectiveDbService.Instance.LoadObjectivesAsync();
    var loadedMapQuestObjectives = QuestObjectiveDbService.Instance.AllObjectives.Count;
    if (!mapQuestLoaderSucceeded || loadedMapQuestObjectives != 0)
    {
        throw new InvalidDataException(
            $"Map quest data must remain disabled: " +
            $"success={mapQuestLoaderSucceeded}, objectives={loadedMapQuestObjectives}.");
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
    var acquisitionRequirementRows = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequiredItems
        WHERE LOWER(REPLACE(REPLACE(REPLACE(COALESCE(RequirementType, ''), '_', ''), '-', ''), ' ', ''))
              IN ('finditem', 'collect', 'item', 'genericitem');
        """);
    var pairedBoltRequirementRows = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequiredItems r
        JOIN Quests q ON q.Id = r.QuestId
        JOIN Items i ON i.Id = r.ItemId
        WHERE q.BsgId = 'fixture-quest-first'
          AND i.BsgId = 'fixture-item-bolts';
        """);
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
    if (questItemLinks != 3 || hideoutItemLinks < 1)
    {
        throw new InvalidDataException(
            $"Quest or hideout item links were not persisted exactly: " +
            $"quest={questItemLinks}, hideout={hideoutItemLinks}.");
    }
    if (acquisitionRequirementRows != 0 || pairedBoltRequirementRows != 1)
    {
        throw new InvalidDataException(
            $"Acquisition objectives leaked into consumable requirements: " +
            $"acquisition={acquisitionRequirementRows}, pairedBolts={pairedBoltRequirementRows}.");
    }
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

    await RunUserProgressResetSmokeAsync();
    RunApplicationBehaviorSmoke();

    Console.WriteLine(
        $"Deterministic database smoke passed: profile=PVP, transport=static-json, " +
        $"requests={fixtureHandler.StaticRequestCount}, items={result.ItemCount}, quests={result.QuestCount}, " +
        $"hideout={result.HideoutStationCount}, questLinks={questItemLinks}, hideoutLinks={hideoutItemLinks}, " +
        $"acquisitionRows={acquisitionRequirementRows}, pairedBolts={pairedBoltRequirementRows}, " +
        $"iconLinks={iconLinks}, neutralRestrictions={restrictedNeutralQuests}, sellItemRows={sellItemRequirements}, " +
        $"dogtagRows={dogtagAlternativeRows}, canonicalDogtags={canonicalDogtagRows}, " +
        $"mapQuestObjectives={loadedMapQuestObjectives}, " +
        $"duplicateDisplayNames={duplicateDisplayNames}, questLoader={loadedQuests.Count}, " +
        $"uniqueQuestKeys={uniqueQuestKeys}, disambiguatedQuestKeys={disambiguatedQuestKeys}, " +
        $"objectiveIds={protectedObjectiveIds}, scopedObjectives={correctlyScopedObjectives}, " +
        $"duplicateObjectiveIds={duplicateObjectiveIds}, " +
        $"duplicateLocalizedObjectives={duplicateLocalizedDescriptions}, " +
        $"missingIds={missingChildIds}, invalidMaxLevels={invalidMaxLevels}");
    return 0;
}

static async Task RunUserProgressResetSmokeAsync()
{
    var database = UserDataDbService.Instance;
    var profile = ProfileType.Pvp;

    await database.SaveQuestProgressAsync(
        "reset-smoke-quest",
        "reset-smoke-quest",
        QuestStatus.Done,
        profile);
    await database.SaveObjectiveProgressAsync(
        "reset-smoke-quest:0",
        "reset-smoke-quest",
        true);
    await database.SaveHideoutProgressAsync("reset-smoke-hideout", 2, profile);
    await database.SaveItemInventoryAsync("reset-smoke-item", 3, 4, profile);

    await UserProgressResetService.Instance.ResetCurrentProfileAsync();

    var counts = (
        Quests: (await database.LoadQuestProgressAsync(profile)).Count,
        Objectives: (await database.LoadObjectiveProgressAsync()).Count,
        Hideout: (await database.LoadHideoutProgressAsync(profile)).Count,
        Inventory: (await database.LoadItemInventoryAsync(profile)).Count);
    if (counts != (0, 0, 0, 0))
    {
        throw new InvalidDataException(
            $"Integrated user progress reset smoke failed: quests={counts.Quests}, " +
            $"objectives={counts.Objectives}, hideout={counts.Hideout}, " +
            $"inventory={counts.Inventory}.");
    }
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
    var originalBytes = await File.ReadAllBytesAsync(databasePath);
    using var httpClient = new HttpClient(new AlwaysUnavailableTarkovApiHandler())
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    var builder = new TarkovDataDatabaseBuilder(httpClient);
    var failure = await builder.BuildPreferredAsync(databasePath);

    if (failure.Success)
        throw new InvalidDataException("The rebuild unexpectedly succeeded while every tarkov.dev endpoint was unavailable.");
    if (!failure.PreservedExistingDatabase)
        throw new InvalidDataException("A failed rebuild did not report that the existing database was preserved.");
    if (!File.Exists(databasePath))
        throw new InvalidDataException("A failed rebuild removed the existing database.");

    var currentBytes = await File.ReadAllBytesAsync(databasePath);
    if (!originalBytes.AsSpan().SequenceEqual(currentBytes))
        throw new InvalidDataException("A failed rebuild modified the existing database.");
}

static void RunApplicationBehaviorSmoke()
{
    if (!QuestTextKoreanSourceSmoke.Run())
        throw new InvalidDataException("Quest Korean source policy smoke failed.");

    var localizedMapName = MapFloorConfig.GetLocalizedDisplayName("Basement 2", "basement2", -2);
    if (!string.Equals(localizedMapName, "지하 2층", StringComparison.Ordinal))
        throw new InvalidDataException($"Map floor localization failed: {localizedMapName}");

    if (OverlayClickThroughPolicy.ShouldToggle(
            isInitializing: true,
            currentState: false,
            requestedState: true))
    {
        throw new InvalidDataException("Overlay click-through must not toggle while settings initialize.");
    }

    if (!OverlayClickThroughPolicy.ShouldToggle(
            isInitializing: false,
            currentState: true,
            requestedState: false))
    {
        throw new InvalidDataException("Overlay click-through must toggle when the requested state changes.");
    }

    if (OverlayClickThroughPolicy.ShouldToggle(
            isInitializing: false,
            currentState: false,
            requestedState: false))
    {
        throw new InvalidDataException("Overlay click-through must not toggle when state is unchanged.");
    }

    var selector = new QuestStatusSelector();
    selector.ApplyDefault();
    if (!string.Equals(selector.SelectedStatus, "All", StringComparison.Ordinal))
        throw new InvalidDataException($"Quest status default filter must be All, actual={selector.SelectedStatus}.");
}

static async Task<int> RunExternalApiSmokeAsync()
{
    var databasePath = Path.Combine(AppContext.BaseDirectory, "Assets", "tarkov_data.db");
    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    var builder = new TarkovDataDatabaseBuilder(
        httpClient,
        progress => Console.WriteLine($"[{progress.Percent,6:F1}%] {progress.Message}"));
    var result = await builder.BuildPreferredAsync(databasePath);

    if (!result.Success)
    {
        Console.Error.WriteLine(
            $"Live tarkov.dev rebuild failed. Existing DB preserved={result.PreservedExistingDatabase}. " +
            $"Reason={result.ErrorMessage}");
        return 1;
    }

    if (result.ItemCount < 1000 || result.QuestCount < 300 || result.HideoutStationCount < 10)
    {
        Console.Error.WriteLine(
            $"Live tarkov.dev rebuild returned suspiciously small data: " +
            $"items={result.ItemCount}, quests={result.QuestCount}, hideout={result.HideoutStationCount}.");
        return 1;
    }

    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ConnectionString);
    await connection.OpenAsync();

    var integrity = Convert.ToString(await ExecuteScalarAsync(connection, "PRAGMA integrity_check;"));
    var foreignKeyViolations = await CountRowsAsync(connection, "PRAGMA foreign_key_check;");
    var acquisitionRequirements = Convert.ToInt32(await ExecuteScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequiredItems
        WHERE LOWER(REPLACE(REPLACE(REPLACE(COALESCE(RequirementType, ''), '_', ''), '-', ''), ' ', ''))
              IN ('finditem', 'collect', 'item', 'genericitem', 'sellitem');
        """));
    var requirementTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT DISTINCT COALESCE(RequirementType, '') FROM QuestRequiredItems;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            requirementTypes.Add(reader.GetString(0));
    }

    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Live database integrity check failed: {integrity}");
    if (foreignKeyViolations != 0)
        throw new InvalidDataException($"Live database contains {foreignKeyViolations} foreign-key violations.");
    if (acquisitionRequirements != 0)
        throw new InvalidDataException($"Live database contains {acquisitionRequirements} non-consumable requirement rows.");
    if (requirementTypes.Count != 1 || !requirementTypes.Contains("giveItem"))
        throw new InvalidDataException(
            $"Live database contains unexpected requirement types: {string.Join(", ", requirementTypes.OrderBy(x => x))}");

    Console.WriteLine(
        $"Live tarkov.dev rebuild passed: items={result.ItemCount}, quests={result.QuestCount}, " +
        $"hideout={result.HideoutStationCount}, integrity={integrity}, foreignKeys={foreignKeyViolations}, " +
        $"requirementTypes={string.Join(",", requirementTypes)}");
    return 0;
}

static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return await command.ExecuteScalarAsync();
}

static async Task<int> CountRowsAsync(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await using var reader = await command.ExecuteReaderAsync();
    var count = 0;
    while (await reader.ReadAsync())
        count++;
    return count;
}
