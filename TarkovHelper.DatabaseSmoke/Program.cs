using System.Diagnostics;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Models.Map;
using TarkovHelper.Services;
using TarkovHelper.Services.Map;
using TarkovHelper.Services.Settings;

Console.OutputEncoding = System.Text.Encoding.UTF8;

ItemFulfillmentRegressionSmoke.Run();

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

    if (result.ItemCount != 4 || result.AmmoCount != 1 || result.QuestCount != 2 || result.HideoutStationCount != 1)
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
    if (MiniMapFloorSelection.SelectAutomatic(floors, null) != null ||
        MiniMapFloorSelection.SelectAutomatic(floors, "unknown") != null ||
        MiniMapFloorSelection.SelectAutomatic(floors, "level3") != "level3" ||
        MiniMapFloorSelection.SelectInitial(floors, null) != "main" ||
        MiniMapFloorSelection.SelectInitial(floors, "level3") != "level3" ||
        MiniMapFloorSelection.Move(floors, "main", 1) != "level3" ||
        MiniMapFloorSelection.Move(floors, "main", -1) != "basement" ||
        MiniMapFloorSelection.Move(floors, "level3", 1) != "level3" ||
        MiniMapFloorSelection.Move(floors, "basement", -1) != "basement")
        throw new InvalidDataException("Minimap floor ordering or navigation is incorrect.");

    if (!MiniMapMarkerVisibilityState.IsCurrentFloor("basement", null) ||
        !MiniMapMarkerVisibilityState.IsCurrentFloor("level3", null) ||
        !MiniMapMarkerVisibilityState.IsCurrentFloor(null, "main") ||
        MiniMapMarkerVisibilityState.IsCurrentFloor("basement", "main") ||
        !MiniMapMarkerVisibilityState.IsCurrentFloor("basement", "basement"))
        throw new InvalidDataException("Unknown minimap floor detection was forced to main.");

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

    var statusTask = new TarkovTask
    {
        Ids = ["actual-status-smoke"],
        NormalizedName = "actual-status-smoke",
        Name = "Actual Status Smoke"
    };
    var eligibleStatus = new ActualQuestStatusEvaluator(
        QuestProgressService.Instance).Evaluate(statusTask);
    if (eligibleStatus != QuestStatus.Active)
    {
        throw new InvalidDataException(
            $"Eligible quest must be Active when the helper has no accept action: actual={eligibleStatus}.");
    }

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
    if (categories.GetParentCategory("Scopes") != "WeaponParts" ||
        categories.GetParentCategory("Magazines") != "Ammunition" ||
        categories.GetParentCategory("unrecognized-category") != "Other")
    {
        throw new InvalidDataException("Practical item category grouping failed.");
    }

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
        var specificSourceRows = await ScalarAsync(connection, "SELECT COUNT(*) FROM Ammo WHERE LOWER(TRIM(COALESCE(AcquisitionSource, ''))) NOT IN ('', 'raid/other', '레이드 획득/기타');");
        if (ammoRows < 150 || linkedAmmoRows != ammoRows || koreanAmmoRows < 150 || caliberRows < 20 || specificSourceRows < 20)
        {
            throw new InvalidDataException(
                $"Live ammo data validation failed: rows={ammoRows}, linked={linkedAmmoRows}, korean={koreanAmmoRows}, calibers={caliberRows}, sources={specificSourceRows}.");
        }

        Console.WriteLine($"Live ammo data validated: rows={ammoRows}, calibers={caliberRows}, sources={specificSourceRows}.");
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
