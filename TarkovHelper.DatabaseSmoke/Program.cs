using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;
using TarkovHelper.Services.Map;
using TarkovHelper.Services.Settings;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var contentSmokeRoot = Path.Combine(AppContext.BaseDirectory, "ContentSmoke");
if (Directory.Exists(contentSmokeRoot))
    Directory.Delete(contentSmokeRoot, recursive: true);
Environment.SetEnvironmentVariable("TARKOV_CONTENT_ROOT", contentSmokeRoot);

ItemFulfillmentRegressionSmoke.Run();
PvpOnlyRegressionSmoke.Run();

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
        progress => Console.WriteLine($"[{progress.Percent,6:F1}%] {progress.Message}"),
        enrichAmmoSources: false);

    var result = await builder.BuildPreferredAsync(databasePath);

    if (fixtureHandler.StaticRequestCount == 0)
        throw new InvalidDataException("The static JSON API path was not exercised.");
    if (fixtureHandler.GraphQlRequestCount != 0)
        throw new InvalidDataException("The deterministic static JSON test unexpectedly used GraphQL fallback.");

    await ContentStorageRegressionSmoke.RunAsync(databasePath);

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
    var activeDatabasePath = DatabaseUpdateService.Instance.DatabasePath;
    if (!string.Equals(
            Path.GetFullPath(activeDatabasePath),
            Path.GetFullPath(databasePath),
            StringComparison.OrdinalIgnoreCase))
    {
        await ForceDuplicateQuestNamesAsync(activeDatabasePath);
    }

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
    var validFixtureAmmoSource = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM Ammo
        WHERE ItemId = 'fixture-item-bolts'
          AND AcquisitionSource = 'trader:Prapor:level:1 · trader:Jaeger:level:2 · craft:Workbench:level:3';
        """);
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
    var validDogtagAlternativeGroups = await ScalarAsync(connection, $"""
        SELECT COUNT(*)
        FROM QuestRequiredItems
        WHERE ObjectiveId = '{ObjectiveIdCollisionFixtureHandler.DogtagObjectiveId}'
          AND IsAlternativeGroup = 1
          AND ItemId IS NULL
          AND Count = 7
          AND DogtagMinLevel = 50
          AND json_valid(AlternativeItemIds) = 1
          AND json_array_length(AlternativeItemIds) = 2
          AND json_valid(AlternativeItemNames) = 1
          AND json_array_length(AlternativeItemNames) = 2;
        """);
    var unresolvedAlternativeItems = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequiredItems r, json_each(r.AlternativeItemIds) alternative
        LEFT JOIN Items i ON i.Id = alternative.value
        WHERE r.IsAlternativeGroup = 1
          AND i.Id IS NULL;
        """);
    var malformedAlternativeGroups = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequiredItems
        WHERE IsAlternativeGroup = 1
          AND (
              ItemId IS NOT NULL
              OR COALESCE(RequirementGroupId, '') = ''
              OR json_valid(AlternativeItemIds) != 1
              OR json_array_length(AlternativeItemIds) < 2
              OR json_valid(AlternativeItemNames) != 1
              OR json_array_length(AlternativeItemNames) != json_array_length(AlternativeItemIds)
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
    var missingPrerequisiteLinks = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequirements r
        LEFT JOIN Quests required ON required.Id = r.RequiredQuestId
        LEFT JOIN Quests source ON source.Id = r.QuestId
        WHERE required.Id IS NULL OR source.Id IS NULL;
        """);
    var invalidConcreteRequirementLinks = await ScalarAsync(connection, """
        SELECT COUNT(*)
        FROM QuestRequiredItems r
        LEFT JOIN Items i ON i.Id = r.ItemId
        WHERE r.IsAlternativeGroup = 0 AND (r.ItemId IS NULL OR i.Id IS NULL);
        """);

    if (result.ItemCount != 4 || result.AmmoCount != 1 || result.QuestCount != 2 || result.HideoutStationCount != 1)
        throw new InvalidDataException("Fixture row counts do not match the generated database.");
    if (koreanItems < 4 || koreanQuests < 2)
        throw new InvalidDataException("Korean localized names were not written correctly.");
    if (questItemLinks != 2 || hideoutItemLinks < 1)
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
    if (dogtagAlternativeRows != 1 || validDogtagAlternativeGroups != 1 ||
        unresolvedAlternativeItems != 0 || malformedAlternativeGroups != 0)
    {
        throw new InvalidDataException(
            $"Alternative requirements were not stored as one valid collective group: " +
            $"rows={dogtagAlternativeRows}, valid={validDogtagAlternativeGroups}, " +
            $"unresolved={unresolvedAlternativeItems}, malformed={malformedAlternativeGroups}.");
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
    if (validFixtureAmmoSource != 1)
        throw new InvalidDataException("Deterministic ammo acquisition source did not preserve trader and craft levels.");

    if (missingPrerequisiteLinks != 0 || invalidConcreteRequirementLinks != 0)
    {
        throw new InvalidDataException(
            $"Rebuilt quest links are incomplete: prerequisites={missingPrerequisiteLinks}, " +
            $"concreteItems={invalidConcreteRequirementLinks}.");
    }

    await RunPersistenceWriteQueueSmokeAsync();
    await RunObjectiveProfileIsolationSmokeAsync();
    await RunUserProgressResetSmokeAsync();
    RunMiniMapVisibilitySourceSmoke();
    RunOverlayMiniMapControlsSmoke();
    RunApplicationBehaviorSmoke();

    Console.WriteLine(
        $"Deterministic database smoke passed: profile=PVP, transport=static-json, " +
        $"requests={fixtureHandler.StaticRequestCount}, items={result.ItemCount}, ammo={result.AmmoCount}, quests={result.QuestCount}, " +
        $"hideout={result.HideoutStationCount}, questLinks={questItemLinks}, hideoutLinks={hideoutItemLinks}, " +
        $"acquisitionRows={acquisitionRequirementRows}, pairedBolts={pairedBoltRequirementRows}, " +
        $"iconLinks={iconLinks}, ammoSources={validFixtureAmmoSource}, neutralRestrictions={restrictedNeutralQuests}, sellItemRows={sellItemRequirements}, " +
        $"dogtagRows={dogtagAlternativeRows}, validDogtagGroups={validDogtagAlternativeGroups}, " +
        $"unresolvedAlternativeItems={unresolvedAlternativeItems}, malformedAlternativeGroups={malformedAlternativeGroups}, " +
        $"mapQuestObjectives={loadedMapQuestObjectives}, " +
        $"duplicateDisplayNames={duplicateDisplayNames}, questLoader={loadedQuests.Count}, " +
        $"uniqueQuestKeys={uniqueQuestKeys}, disambiguatedQuestKeys={disambiguatedQuestKeys}, " +
        $"objectiveIds={protectedObjectiveIds}, scopedObjectives={correctlyScopedObjectives}, " +
        $"duplicateObjectiveIds={duplicateObjectiveIds}, " +
        $"duplicateLocalizedObjectives={duplicateLocalizedDescriptions}, " +
        $"missingIds={missingChildIds}, invalidMaxLevels={invalidMaxLevels}, " +
        $"missingPrerequisites={missingPrerequisiteLinks}, invalidConcreteItems={invalidConcreteRequirementLinks}");
    return 0;
}

static async Task RunPersistenceWriteQueueSmokeAsync()
{
    var queue = new PersistenceWriteQueue();
    var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var state = 0;

    _ = queue.Enqueue(async () =>
    {
        writeStarted.TrySetResult();
        await releaseWrite.Task;
        state = 1;
    });

    await writeStarted.Task;
    var resetBarrier = queue.BeginResetAsync();
    var queuedDuringDrain = queue.Enqueue(() =>
    {
        state = 2;
        return Task.CompletedTask;
    });

    releaseWrite.TrySetResult();
    await Task.WhenAll(resetBarrier, queuedDuringDrain);

    // Simulate the database clear while the reset barrier remains held.
    state = 0;
    await queue.Enqueue(() =>
    {
        state = 4;
        return Task.CompletedTask;
    });
    if (state != 0)
        throw new InvalidDataException($"Persistence reset hold failed: state={state}.");

    queue.EndReset();
    await queue.Enqueue(() =>
    {
        state = 3;
        return Task.CompletedTask;
    });
    if (state != 3)
        throw new InvalidDataException("Persistence queue did not resume after reset.");
}

static async Task RunObjectiveProfileIsolationSmokeAsync()
{
    const string key = "objective-profile-isolation-smoke";
    var store = ProfileScopedObjectiveProgressStore.Instance;

    await store.ClearAllAsync(ProfileType.Pvp);
    await store.ClearAllAsync(ProfileType.Pve);
    await store.SaveAsync(key, "objective-profile-isolation-quest", true, ProfileType.Pvp);

    var pvp = await store.LoadAsync(ProfileType.Pvp);
    var pve = await store.LoadAsync(ProfileType.Pve);
    if (!pvp.TryGetValue(key, out var completed) || !completed || pve.ContainsKey(key))
    {
        throw new InvalidDataException(
            $"Objective profile isolation failed: pvp={pvp.ContainsKey(key)}, pve={pve.ContainsKey(key)}.");
    }

    await store.ClearAllAsync(ProfileType.Pvp);
    await store.ClearAllAsync(ProfileType.Pve);
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

    // Exercise the real debounced inventory persistence path immediately before reset.
    // The timer would recreate a row after 500 ms if the coordinated barrier failed.
    ItemInventoryService.Instance.SetFirQuantity("reset-smoke-pending-item", 7);

    await UserProgressResetService.Instance.ResetCurrentProfileAsync();
    await Task.Delay(650);

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

static void RunMiniMapVisibilitySourceSmoke()
{
    var markerSettings = MapSettings.Instance;
    var applicationSettings = SettingsService.Instance;
    var original = (
        markerSettings.ShowPmcSpawns,
        markerSettings.ShowSniperScavs,
        markerSettings.ShowRogues,
        markerSettings.ShowCultists,
        markerSettings.ShowLevers,
        markerSettings.ShowBosses,
        applicationSettings.MapShowExtracts,
        applicationSettings.MapShowPmcExtracts,
        applicationSettings.MapShowScavExtracts,
        applicationSettings.MapShowTransits);

    try
    {
        markerSettings.ShowPmcSpawns = true;
        markerSettings.ShowSniperScavs = false;
        markerSettings.ShowRogues = true;
        markerSettings.ShowCultists = false;
        markerSettings.ShowLevers = true;
        markerSettings.ShowBosses = false;
        applicationSettings.MapShowExtracts = true;
        applicationSettings.MapShowPmcExtracts = false;
        applicationSettings.MapShowScavExtracts = true;
        applicationSettings.MapShowTransits = false;

        var captured = MiniMapMarkerVisibilityState.Capture(markerSettings);
        if (!captured.ShowPmcSpawns || captured.ShowSniperScavs ||
            !captured.ShowRogues || captured.ShowCultists ||
            !captured.ShowLevers || captured.ShowBosses ||
            !captured.ShowExtracts || captured.ShowPmcExtracts ||
            !captured.ShowScavExtracts || captured.ShowTransits)
        {
            throw new InvalidDataException(
                "Minimap visibility snapshot did not read the live map-tab setting sources.");
        }
    }
    finally
    {
        markerSettings.ShowPmcSpawns = original.ShowPmcSpawns;
        markerSettings.ShowSniperScavs = original.ShowSniperScavs;
        markerSettings.ShowRogues = original.ShowRogues;
        markerSettings.ShowCultists = original.ShowCultists;
        markerSettings.ShowLevers = original.ShowLevers;
        markerSettings.ShowBosses = original.ShowBosses;
        applicationSettings.MapShowExtracts = original.MapShowExtracts;
        applicationSettings.MapShowPmcExtracts = original.MapShowPmcExtracts;
        applicationSettings.MapShowScavExtracts = original.MapShowScavExtracts;
        applicationSettings.MapShowTransits = original.MapShowTransits;
    }
}

static void RunOverlayMiniMapControlsSmoke()
{
    var settings = new OverlayMiniMapSettings();
    if (settings.FloorUpKey != 0x21 || settings.FloorDownKey != 0x22 ||
        Math.Abs(settings.OtherFloorOpacity - 0.3) > 0.0001 ||
        !settings.AutoFloorSelection)
        throw new InvalidDataException("Minimap floor-control defaults are not stable.");

    settings.SetHotkey(OverlayMiniMapHotkeyAction.OpacityIncrease, settings.FloorUpKey);
    if (settings.OpacityIncreaseKey != 0x21 || settings.FloorUpKey != 0 ||
        settings.GetActionForHotkey(0x21) != OverlayMiniMapHotkeyAction.OpacityIncrease)
        throw new InvalidDataException("Duplicate minimap hotkeys were not transferred atomically.");

    settings.ToggleViewModeKey = 0x76;
    settings.ToggleClickThroughKey = 0x77;
    settings.ResetViewKey = 0x78;
    settings.ResumeAutoFloorKey = 0x79;
    var cloned = settings.Clone();
    if (cloned.ToggleViewModeKey != settings.ToggleViewModeKey ||
        cloned.ToggleClickThroughKey != settings.ToggleClickThroughKey ||
        cloned.ResetViewKey != settings.ResetViewKey ||
        cloned.ResumeAutoFloorKey != settings.ResumeAutoFloorKey)
        throw new InvalidDataException("Minimap hotkeys were not preserved by cloning.");

    var floors = new[]
    {
        new MapFloorConfig { LayerId = "level3", Order = 2 },
        new MapFloorConfig { LayerId = "basement", Order = -1 },
        new MapFloorConfig { LayerId = "main", Order = 0, IsDefault = true }
    };
    if (MiniMapFloorSelection.SelectAutomatic(floors, null) != "main" ||
        MiniMapFloorSelection.SelectAutomatic(floors, "unknown") != "main" ||
        MiniMapFloorSelection.SelectAutomatic(floors, "level3") != "level3" ||
        MiniMapFloorSelection.SelectInitial(floors, null) != "main" ||
        MiniMapFloorSelection.SelectInitial(floors, "level3") != "level3" ||
        MiniMapFloorSelection.Move(floors, "main", 1) != "level3" ||
        MiniMapFloorSelection.Move(floors, "main", -1) != "basement" ||
        MiniMapFloorSelection.Move(floors, "level3", 1) != "level3" ||
        MiniMapFloorSelection.Move(floors, "basement", -1) != "basement")
        throw new InvalidDataException("Minimap floor ordering or navigation is incorrect.");

    if (!MiniMapMarkerVisibilityState.IsCurrentFloor(null, "main") ||
        MiniMapMarkerVisibilityState.IsCurrentFloor("basement", "main") ||
        !MiniMapMarkerVisibilityState.IsCurrentFloor("basement", "basement"))
        throw new InvalidDataException("Minimap floor marker filtering is incorrect.");

    const string svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\">" +
        "<g id=\"basement\"><rect width=\"10\" height=\"10\" /></g>" +
        "<g id=\"main\"><rect width=\"10\" height=\"10\" /></g>" +
        "<g id=\"level3\"><rect width=\"10\" height=\"10\" /></g>" +
        "</svg>";
    var processed = new TarkovHelper.Services.Map.SvgStylePreprocessor().ProcessSvgContent(
        svg,
        new[] { "main" },
        new[] { "basement", "main", "level3" },
        backgroundFloorId: null,
        backgroundOpacity: 0.42,
        dimAllOtherFloors: true);
    var document = new System.Xml.XmlDocument();
    document.LoadXml(processed);
    var styles = document.GetElementsByTagName("g")
        .Cast<System.Xml.XmlElement>()
        .ToDictionary(
            element => element.GetAttribute("id"),
            element => element.GetAttribute("style"),
            StringComparer.OrdinalIgnoreCase);
    if (!styles["main"].Contains("display:block", StringComparison.OrdinalIgnoreCase) ||
        !styles["main"].Contains("opacity:1", StringComparison.OrdinalIgnoreCase) ||
        !styles["basement"].Contains("opacity:0.42", StringComparison.OrdinalIgnoreCase) ||
        !styles["level3"].Contains("opacity:0.42", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Minimap floor opacity processing is incorrect.");
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

    var settingsService = SettingsService.Instance;
    var originalPlayerLevel = settingsService.PlayerLevel;
    settingsService.PlayerLevel = 16;

    var statusTask = new TarkovTask
    {
        Ids = ["actual-status-smoke"],
        NormalizedName = "actual-status-smoke",
        Name = "Actual Status Smoke"
    };
    var progressService = QuestProgressService.Instance;
    var eligibleStatus = new ActualQuestStatusEvaluator(progressService).Evaluate(statusTask);
    if (eligibleStatus != QuestStatus.Active)
        throw new InvalidDataException($"Eligible quest must be Active without a separate accept state: actual={eligibleStatus}.");

    var levelLockedTask = new TarkovTask
    {
        Ids = ["level-locked-status-smoke"],
        NormalizedName = "level-locked-status-smoke",
        Name = "Level Locked Status Smoke",
        RequiredLevel = 20
    };
    var levelLockedStatus = new ActualQuestStatusEvaluator(progressService).Evaluate(levelLockedTask);
    if (levelLockedStatus != QuestStatus.LevelLocked)
        throw new InvalidDataException($"Quest below required level must be LevelLocked: actual={levelLockedStatus}.");

    settingsService.PlayerLevel = originalPlayerLevel;

    const string alternativeA = "__alternative-consumption-a__";
    const string alternativeB = "__alternative-consumption-b__";
    inventory.SetNonFirQuantity(alternativeA, 2);
    inventory.SetNonFirQuantity(alternativeB, 4);
    var alternativeResult = inventory.ConsumeBatch([
        new InventoryConsumptionRequirement(
            "group:alternative-consumption",
            5,
            FirOnly: false,
            AlternativeItemKeys: [alternativeA, alternativeB])
    ]);
    if (alternativeResult.Consumed != 5 || alternativeResult.Missing != 0 ||
        inventory.GetTotalQuantity(alternativeA) + inventory.GetTotalQuantity(alternativeB) != 1)
    {
        throw new InvalidDataException("Collective alternative requirement multiplied or consumed the shared count incorrectly.");
    }
    inventory.SetNonFirQuantity(alternativeA, 0);
    inventory.SetNonFirQuantity(alternativeB, 0);

    var markerVisibility = new MiniMapMarkerVisibilityState(
        ShowPmcSpawns: true,
        ShowSniperScavs: false,
        ShowRogues: true,
        ShowCultists: false,
        ShowLevers: true,
        ShowBosses: false,
        ShowExtracts: true,
        ShowPmcExtracts: true,
        ShowScavExtracts: false,
        ShowTransits: true);
    if (!markerVisibility.IsMapMarkerVisible(MarkerType.PmcSpawn) ||
        markerVisibility.IsMapMarkerVisible(MarkerType.SniperScavSpawn) ||
        !markerVisibility.IsMapMarkerVisible(MarkerType.RogueSpawn) ||
        markerVisibility.IsMapMarkerVisible(MarkerType.CultistSpawn) ||
        !markerVisibility.IsMapMarkerVisible(MarkerType.Lever) ||
        markerVisibility.IsMapMarkerVisible(MarkerType.BossSpawn) ||
        markerVisibility.IsMapMarkerVisible(MarkerType.ScavSpawn) ||
        !markerVisibility.IsExtractVisible(ExtractFaction.Pmc) ||
        markerVisibility.IsExtractVisible(ExtractFaction.Scav) ||
        !markerVisibility.IsExtractVisible(ExtractFaction.Transit) ||
        !markerVisibility.IsExtractVisible(ExtractFaction.Shared))
    {
        throw new InvalidDataException(
            "Minimap marker visibility did not mirror the map-tab category settings.");
    }

    var scavOnlyExtracts = markerVisibility with
{
    ShowPmcExtracts = false,
    ShowScavExtracts = true
};
if (scavOnlyExtracts.IsExtractVisible(ExtractFaction.Shared))
{
    throw new InvalidDataException(
        "Shared extracts must follow the PMC filter exactly like the map tab.");
}

    var pairedExtracts = MapExtractDisplayGrouping.GroupForDisplay(new[]
    {
        new MapExtract
        {
            Id = "paired-pmc",
            Name = "Crossroads",
            Faction = ExtractFaction.Pmc,
            X = 100,
            Z = 200
        },
        new MapExtract
        {
            Id = "paired-scav",
            Name = "Crossroads",
            Faction = ExtractFaction.Scav,
            X = 104,
            Z = 204
        }
    });
    if (pairedExtracts.Count != 1 ||
        pairedExtracts[0].Faction != ExtractFaction.Pmc ||
        pairedExtracts[0].SourceCount != 2 ||
        scavOnlyExtracts.IsExtractVisible(pairedExtracts[0].Faction))
    {
        throw new InvalidDataException(
            "Paired PMC/Scav extracts were not grouped and classified as PMC.");
    }

        var extractsDisabled = markerVisibility with { ShowExtracts = false };
    if (extractsDisabled.IsExtractVisible(ExtractFaction.Pmc) ||
        extractsDisabled.IsExtractVisible(ExtractFaction.Transit))
    {
        throw new InvalidDataException(
            "Minimap extract master visibility did not override faction filters.");
    }

    var categories = ItemsDataService.Instance;
    var categoryCases = new (string Primary, string[] Hierarchy, string Expected)[]
    {
        ("Assault rifle", ["Assault rifle", "Weapon", "Item"], "Weapons"),
        ("Magazine", ["Magazine", "Weapon mod", "Item"], "Magazines"),
        ("Ammo container", ["Ammo container", "Item"], "Ammunition"),
        ("Medical supplies", ["Medical supplies", "Barter item", "Item"], "Medical"),
        ("Drink", ["Drink", "Food and drink", "Item"], "Food"),
        ("Knife", ["Knife", "Item"], "Melee"),
        ("Stock", ["Stock", "Weapon mod", "Item"], "Parts"),
        ("Throwable weapon", ["Throwable weapon", "Item"], "Grenades"),
        ("Electronics", ["Electronics", "Barter item", "Item"], "Barter"),
        ("Chest rig", ["Chest rig", "Armored equipment", "Item"], "Rigs"),
        ("Thermal Vision", ["Thermal Vision", "Special scope", "Weapon mod", "Item"], "Eyewear"),
        ("Common container", ["Common container", "Item"], "Containers"),
        ("Headphones", ["Headphones", "Equipment", "Item"], "Armor"),
        ("Notes", ["Notes", "Item"], "Info"),
        ("Mechanical Key", ["Mechanical Key", "Key", "Item"], "Keys"),
        ("Compass", ["Compass", "Special item", "Item"], "Special")
    };
    if (categoryCases.Any(test =>
            categories.GetParentCategory(test.Primary, test.Hierarchy) != test.Expected) ||
        categories.GetParentCategory("Ammo", ["Ammo", "Item"], isRangeSubmission: true) !=
            ItemCategoryClassifier.RangeSubmission)
    {
        throw new InvalidDataException("Canonical item category hierarchy classification failed.");
    }

    var categoryOrder = new[]
    {
        "Weapons", "Magazines", "Ammunition", "Medical", "Food", "Melee",
        "Parts", "Grenades", "Barter", "Rigs", "Eyewear", "Containers",
        "Armor", "Info", "Keys", "Special", ItemCategoryClassifier.RangeSubmission
    };
    if (!categoryOrder.SequenceEqual(UiSortOrder.ItemCategories) ||
        !categoryOrder.Select(UiSortOrder.GetItemCategoryRank).SequenceEqual(Enumerable.Range(0, 17)))
    {
        throw new InvalidDataException("Item category dropdown order is not canonical.");
    }

    var rangeTask = new TarkovTask
    {
        Ids = ["range-inventory-smoke"],
        NormalizedName = "range-inventory-smoke",
        Name = "Range Inventory Smoke"
    };
    var rangeRequirement = new QuestItem
    {
        ItemNormalizedName = "group:range-inventory",
        RequirementGroupId = "range-inventory",
        IsAlternativeGroup = true,
        AlternativeItemIds = ["item-a", "item-b", "item-c"],
        Amount = 3
    };
    var rangeKeys = QuestRequirementInventoryKey.BuildAlternativeItemKeys(rangeTask, rangeRequirement);
    if (rangeKeys.Count != 3 || rangeKeys.Any(key => key is "item-a" or "item-b" or "item-c"))
        throw new InvalidDataException("Range requirement keys were not isolated from concrete item inventory.");
    inventory.SetNonFirQuantity(rangeKeys[1], 3);
    inventory.SetNonFirQuantity("item-b", 0);
    if (rangeKeys.Sum(inventory.GetTotalQuantity) != 3 || inventory.GetTotalQuantity("item-b") != 0)
        throw new InvalidDataException("Range and concrete item inventories were not calculated independently.");
    foreach (var key in rangeKeys) inventory.SetNonFirQuantity(key, 0);

    var selector = new QuestStatusSelector();
    selector.ApplyDefault();
    if (!string.Equals(selector.SelectedStatus, QuestStatusSelector.DefaultStatus, StringComparison.Ordinal))
        throw new InvalidDataException($"Quest status default filter must be All, actual={selector.SelectedStatus}.");
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
        if (!result.Success || !result.WasUpdated)
            return 1;

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = service.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync();
        var ammoRows = await ScalarAsync(connection, "SELECT COUNT(*) FROM Ammo;");
        var linkedAmmoRows = await ScalarAsync(connection, "SELECT COUNT(*) FROM Ammo a JOIN Items i ON i.BsgId = a.ItemId OR i.Id = a.ItemId;");
        var koreanAmmoRows = await ScalarAsync(connection, "SELECT COUNT(*) FROM Ammo a JOIN Items i ON i.BsgId = a.ItemId OR i.Id = a.ItemId WHERE COALESCE(NULLIF(i.NameKO, ''), '') != ''; ");
        var caliberRows = await ScalarAsync(connection, "SELECT COUNT(DISTINCT Caliber) FROM Ammo;");
        var sourceSummary = await ValidateAmmoAcquisitionSourcesAsync(connection);
        var specificSourceRows = sourceSummary.PermanentRows;
        var invalidConcreteRequirements = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems r
            LEFT JOIN Items i ON i.Id = r.ItemId
            WHERE r.IsAlternativeGroup = 0 AND (r.ItemId IS NULL OR i.Id IS NULL);
            """);
        var invalidAlternativeRequirements = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems r
            WHERE r.IsAlternativeGroup = 1 AND (
                r.ItemId IS NOT NULL
                OR COALESCE(r.RequirementGroupId, '') = ''
                OR json_valid(r.AlternativeItemIds) != 1
                OR json_array_length(r.AlternativeItemIds) < 2
                OR json_valid(r.AlternativeItemNames) != 1
                OR json_array_length(r.AlternativeItemNames) != json_array_length(r.AlternativeItemIds)
            );
            """);
        var unresolvedAlternativeRequirements = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequiredItems r, json_each(r.AlternativeItemIds) alternative
            LEFT JOIN Items i ON i.Id = alternative.value
            WHERE r.IsAlternativeGroup = 1 AND i.Id IS NULL;
            """);
        var missingPrerequisiteLinks = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequirements r
            LEFT JOIN Quests q ON q.Id = r.RequiredQuestId
            WHERE q.Id IS NULL;
            """);
        var helpingHandLevel = await ScalarAsync(connection, "SELECT COALESCE(MAX(MinLevel), -1) FROM Quests WHERE Name = 'A Helping Hand';");
        var helpingHandPrerequisites = await ScalarAsync(connection, """
            SELECT COUNT(*)
            FROM QuestRequirements r
            JOIN Quests source ON source.Id = r.QuestId
            JOIN Quests required ON required.Id = r.RequiredQuestId
            WHERE source.Name = 'A Helping Hand' AND required.Name = 'Saving the Mole';
            """);
        if (ammoRows < 150 || linkedAmmoRows != ammoRows || koreanAmmoRows < 150 || caliberRows < 20 ||
            sourceSummary.TotalRows != ammoRows || specificSourceRows < 50)
        {
            throw new InvalidDataException(
                $"Live ammo data validation failed: rows={ammoRows}, linked={linkedAmmoRows}, korean={koreanAmmoRows}, " +
                $"calibers={caliberRows}, permanentSources={specificSourceRows}, raidOnly={sourceSummary.RaidOnlyRows}.");
        }
        if (invalidConcreteRequirements != 0 || invalidAlternativeRequirements != 0 ||
            unresolvedAlternativeRequirements != 0 || missingPrerequisiteLinks != 0)
        {
            throw new InvalidDataException(
                $"Live quest item/prerequisite integrity failed: concrete={invalidConcreteRequirements}, " +
                $"groups={invalidAlternativeRequirements}, unresolved={unresolvedAlternativeRequirements}, " +
                $"prerequisites={missingPrerequisiteLinks}.");
        }
        if (helpingHandLevel != 20 || helpingHandPrerequisites != 1)
        {
            throw new InvalidDataException(
                $"A Helping Hand start conditions were lost during API refresh: " +
                $"level={helpingHandLevel}, prerequisites={helpingHandPrerequisites}.");
        }

        Console.WriteLine(
            $"Live data validated: ammo={ammoRows}, calibers={caliberRows}, permanentSources={specificSourceRows}, " +
            $"traderRows={sourceSummary.TraderRows}, craftRows={sourceSummary.CraftRows}, raidOnly={sourceSummary.RaidOnlyRows}, " +
            $"A Helping Hand level={helpingHandLevel}, prerequisiteLinks={helpingHandPrerequisites}.");

        // The application updater writes to its isolated mutable-content path.
        // Existing release workflows intentionally publish the verified database
        // from the smoke output Assets folder, so copy the validated result back
        // only after every live-data assertion has succeeded.
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
        var releaseDatabasePath = Path.Combine(AppContext.BaseDirectory, "Assets", "tarkov_data.db");
        File.Copy(service.DatabasePath, releaseDatabasePath, overwrite: true);
        return 0;
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


static async Task<AmmoSourceSummary> ValidateAmmoAcquisitionSourcesAsync(SqliteConnection connection)
{
    long totalRows = 0;
    long permanentRows = 0;
    long traderRows = 0;
    long craftRows = 0;
    long raidOnlyRows = 0;

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT ItemId, AcquisitionSource FROM Ammo ORDER BY ItemId;";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        totalRows++;
        var itemId = reader.IsDBNull(0) ? "?" : reader.GetString(0);
        var source = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidDataException($"Ammo acquisition source is empty: {itemId}.");

        var tokens = source.Split(
            '·',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            throw new InvalidDataException($"Ammo acquisition source has no tokens: {itemId}.");

        var hasRaid = tokens.Any(token => token.Equals("raid-found", StringComparison.OrdinalIgnoreCase));
        var hasTrader = false;
        var hasCraft = false;
        foreach (var token in tokens)
        {
            if (token.Equals("raid-found", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 ||
                !parts[2].Equals("level", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[3], out var level) || level < 1)
            {
                throw new InvalidDataException($"Malformed ammo acquisition source: item={itemId}, source={source}.");
            }

            if (parts[0].Equals("trader", StringComparison.OrdinalIgnoreCase))
                hasTrader = true;
            else if (parts[0].Equals("craft", StringComparison.OrdinalIgnoreCase))
                hasCraft = true;
            else
                throw new InvalidDataException($"Unsupported ammo acquisition source: item={itemId}, source={source}.");
        }

        if (hasRaid && (hasTrader || hasCraft || tokens.Length != 1))
        {
            throw new InvalidDataException(
                $"Raid-only source coexists with a permanent ammo source: item={itemId}, source={source}.");
        }

        if (hasTrader || hasCraft)
        {
            permanentRows++;
            if (hasTrader) traderRows++;
            if (hasCraft) craftRows++;
        }
        else if (hasRaid)
        {
            raidOnlyRows++;
        }
        else
        {
            throw new InvalidDataException($"Ammo has neither a permanent source nor raid-only status: {itemId}.");
        }
    }

    return new AmmoSourceSummary(totalRows, permanentRows, traderRows, craftRows, raidOnlyRows);
}

internal sealed record AmmoSourceSummary(
    long TotalRows,
    long PermanentRows,
    long TraderRows,
    long CraftRows,
    long RaidOnlyRows);
