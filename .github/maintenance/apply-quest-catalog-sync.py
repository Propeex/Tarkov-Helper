from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"expected exactly one match in {path}, found {count}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


json_api = "TarkovHelper/Services/TarkovDataDatabaseBuilder.JsonApi.cs"
replace_once(
    json_api,
    '''        catch (OperationCanceledException)\n        {\n            CleanupFile(tempPath);\n            throw;\n        }\n        catch (Exception staticException) when (!staticDataReady)\n''',
    '''        catch (OperationCanceledException)\n        {\n            CleanupFile(tempPath);\n            throw;\n        }\n        catch (QuestCatalogOverlayException overlayException)\n        {\n            CleanupFile(tempPath);\n            throw new InvalidOperationException(\n                "현재 퀘스트 목록 보정 데이터를 검증하지 못해 DB 업데이트를 중단했습니다.",\n                overlayException);\n        }\n        catch (Exception staticException) when (!staticDataReady)\n''')

replace_once(
    json_api,
    '''        var taskDocuments = await FetchLocalizedJsonAsync(\n            "regular/tasks",\n            "퀘스트",\n            32,\n            52,\n            cancellationToken);\n        var tasksEn = ParseTasks(taskDocuments.English, itemLookupEn, tradersEn, mapsEn);\n        var tasksKo = ParseTasks(taskDocuments.Korean, itemLookupKo, tradersKo, mapsKo);\n''',
    '''        var taskDocuments = await FetchLocalizedJsonAsync(\n            "regular/tasks",\n            "퀘스트",\n            32,\n            48,\n            cancellationToken);\n\n        Report("API", "현재 퀘스트 목록 보정 데이터를 받는 중", 48, 0, null);\n        var (questOverlay, _) = await DownloadQuestCatalogOverlayAsync(cancellationToken);\n        var questOverlayInfo = ApplyQuestCatalogOverlay(\n            taskDocuments.English,\n            taskDocuments.Korean,\n            questOverlay);\n\n        var tasksEn = ParseTasks(taskDocuments.English, itemLookupEn, tradersEn, mapsEn);\n        var tasksKo = ParseTasks(taskDocuments.Korean, itemLookupKo, tradersKo, mapsKo);\n''')

replace_once(
    json_api,
    '''        return MergeApiData(itemsEn, itemsKo, tasksEn, tasksKo, hideoutEn, hideoutKo)\n            with { Transport = "static-json" };\n''',
    '''        return MergeApiData(itemsEn, itemsKo, tasksEn, tasksKo, hideoutEn, hideoutKo)\n            with\n            {\n                Source = $"tarkov.dev + tarkov-data-overlay {questOverlayInfo.Version}",\n                Transport = "static-json+overlay"\n            };\n''')

replace_once(
    json_api,
    '''        var prestigeLookup = data["prestige"] as JsonObject;\n''',
    '''        var prestigeLookup = data["prestige"];\n''')

replace_once(
    json_api,
    '''    private static ApiPrestige? ResolvePrestige(\n        JsonNode? node,\n        JsonObject? prestigeLookup)\n    {\n        var directLevel = NodeInt(node);\n        if (directLevel.HasValue)\n            return new ApiPrestige { PrestigeLevel = directLevel };\n\n        if (node is JsonObject prestigeObject)\n            return new ApiPrestige { PrestigeLevel = GetInt(prestigeObject, "prestigeLevel") };\n\n        var id = NodeString(node);\n        if (!string.IsNullOrWhiteSpace(id) &&\n            prestigeLookup?[id] is JsonObject lookupObject)\n        {\n            return new ApiPrestige\n            {\n                PrestigeLevel = GetInt(lookupObject, "prestigeLevel")\n            };\n        }\n\n        return null;\n    }\n''',
    '''    private static ApiPrestige? ResolvePrestige(\n        JsonNode? node,\n        JsonNode? prestigeLookup)\n    {\n        var directLevel = NodeInt(node);\n        if (directLevel.HasValue)\n            return new ApiPrestige { PrestigeLevel = directLevel };\n\n        if (node is JsonObject prestigeObject)\n            return new ApiPrestige { PrestigeLevel = GetInt(prestigeObject, "prestigeLevel") };\n\n        var id = NodeString(node);\n        if (!string.IsNullOrWhiteSpace(id) &&\n            ResolvePrestigeObject(prestigeLookup, id) is JsonObject lookupObject)\n        {\n            return new ApiPrestige\n            {\n                PrestigeLevel = GetInt(lookupObject, "prestigeLevel")\n            };\n        }\n\n        return null;\n    }\n''')

fixture = "TarkovHelper.DatabaseSmoke/FixtureTarkovApiHandler.cs"
replace_once(
    fixture,
    '''            "regular/tasks_ko" => TranslationResponse(KoreanTranslations()),\n\n            "regular/hideout" => JsonResponse(HttpStatusCode.OK, CreateStaticHideout()),\n''',
    '''            "regular/tasks_ko" => TranslationResponse(KoreanTranslations()),\n\n            "tarkovtracker-org/tarkov-data-overlay/main/dist/overlay.json" =>\n                JsonResponse(HttpStatusCode.OK, CreateQuestCatalogOverlay()),\n\n            "regular/hideout" => JsonResponse(HttpStatusCode.OK, CreateStaticHideout()),\n''')

replace_once(
    fixture,
    '''                    ["fixture-quest-third"] = new\n                    {\n                        id = "fixture-quest-third",\n                        name = "fixture-quest-third Name",\n                        normalizedName = "third-fixture-quest",\n                        wikiLink = "https://example.invalid/quest-third",\n                        minPlayerLevel = 1,\n                        factionName = "Any",\n                        kappaRequired = false,\n                        trader = "fixture-trader",\n                        map = (string?)null,\n                        requiredPrestige = (string?)null,\n                        taskRequirements = Array.Empty<object>(),\n                        objectives = Array.Empty<object>()\n                    }\n                },\n                prestige = new Dictionary<string, object>()\n''',
    '''                    ["fixture-quest-third"] = new\n                    {\n                        id = "fixture-quest-third",\n                        name = "fixture-quest-third Name",\n                        normalizedName = "third-fixture-quest",\n                        wikiLink = "https://example.invalid/quest-third",\n                        minPlayerLevel = 1,\n                        factionName = "Any",\n                        kappaRequired = false,\n                        trader = "fixture-trader",\n                        map = (string?)null,\n                        requiredPrestige = "fixture-prestige-1",\n                        taskRequirements = Array.Empty<object>(),\n                        objectives = Array.Empty<object>()\n                    },\n                    ["fixture-quest-disabled"] = new\n                    {\n                        id = "fixture-quest-disabled",\n                        name = "Disabled Fixture Quest",\n                        normalizedName = "disabled-fixture-quest",\n                        minPlayerLevel = 1,\n                        factionName = "Any",\n                        kappaRequired = false,\n                        trader = "fixture-trader",\n                        map = (string?)null,\n                        requiredPrestige = (string?)null,\n                        taskRequirements = Array.Empty<object>(),\n                        objectives = Array.Empty<object>()\n                    }\n                },\n                prestige = new object[]\n                {\n                    new\n                    {\n                        id = "fixture-prestige-1",\n                        prestigeLevel = 1\n                    }\n                }\n''')

marker = '''    private static object CreateStaticHideout()\n'''
insert = '''    private static object CreateQuestCatalogOverlay()\n    {\n        var locales = new Dictionary<string, object>\n        {\n            ["en"] = new\n            {\n                tasks = new Dictionary<string, object>\n                {\n                    ["fixture-quest-first"] = new { name = "Corrected First Fixture Quest" }\n                }\n            },\n            ["ko"] = new\n            {\n                tasks = new Dictionary<string, object>\n                {\n                    ["fixture-quest-first"] = new { name = "보정된 첫 번째 퀘스트" }\n                }\n            }\n        };\n\n        return new Dictionary<string, object>\n        {\n            ["$meta"] = new\n            {\n                version = "fixture-1",\n                generated = "2026-08-07T00:00:00Z",\n                sha256 = "fixture"\n            },\n            ["tasks"] = new Dictionary<string, object>\n            {\n                ["fixture-quest-first"] = new { minPlayerLevel = 3 },\n                ["fixture-quest-disabled"] = new { disabled = true }\n            },\n            ["tasksAdd"] = new Dictionary<string, object>\n            {\n                ["fixture-quest-overlay-added"] = new\n                {\n                    id = "fixture-quest-overlay-added",\n                    name = "Overlay Added Quest",\n                    normalizedName = "overlay-added-quest",\n                    wikiLink = "https://example.invalid/overlay-added",\n                    minPlayerLevel = 5,\n                    factionName = "Any",\n                    kappaRequired = false,\n                    trader = "fixture-trader",\n                    map = (string?)null,\n                    requiredPrestige = new { prestigeLevel = 5 },\n                    taskRequirements = Array.Empty<object>(),\n                    objectives = Array.Empty<object>()\n                }\n            },\n            ["prestige"] = new Dictionary<string, object>\n            {\n                ["fixture-prestige-1"] = new { prestigeLevel = 2 }\n            },\n            ["locales"] = locales,\n            ["modes"] = new Dictionary<string, object>\n            {\n                ["regular"] = new\n                {\n                    tasks = new Dictionary<string, object>()\n                }\n            }\n        };\n    }\n\n'''
p = Path(fixture)
text = p.read_text(encoding="utf-8")
if text.count(marker) != 1:
    raise SystemExit("fixture insertion marker mismatch")
p.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")

program = "TarkovHelper.DatabaseSmoke/Program.cs"
replace_once(
    program,
    '''    if (QuestDbService.Instance.GetQuestById("fixture-quest-first") == null ||\n        QuestDbService.Instance.GetQuestById("fixture-quest-second") == null ||\n        QuestDbService.Instance.GetQuestById("fixture-quest-third") == null)\n    {\n        throw new InvalidDataException("Quest ID lookup lost one of the fixture quests.");\n    }\n\n''',
    '''    if (QuestDbService.Instance.GetQuestById("fixture-quest-first") == null ||\n        QuestDbService.Instance.GetQuestById("fixture-quest-second") == null ||\n        QuestDbService.Instance.GetQuestById("fixture-quest-third") == null ||\n        QuestDbService.Instance.GetQuestById("fixture-quest-overlay-added") == null)\n    {\n        throw new InvalidDataException("Quest ID lookup lost one of the effective fixture quests.");\n    }\n    if (QuestDbService.Instance.GetQuestById("fixture-quest-disabled") != null)\n        throw new InvalidDataException("Overlay-disabled quest leaked into the application quest catalog.");\n\n    var correctedFixtureQuest = QuestDbService.Instance.GetQuestById("fixture-quest-first")!;\n    var prestigeFixtureQuest = QuestDbService.Instance.GetQuestById("fixture-quest-third")!;\n    var addedPrestigeQuest = QuestDbService.Instance.GetQuestById("fixture-quest-overlay-added")!;\n    if (!string.Equals(correctedFixtureQuest.Name, "Corrected First Fixture Quest", StringComparison.Ordinal) ||\n        !string.Equals(correctedFixtureQuest.NameKo, "보정된 첫 번째 퀘스트", StringComparison.Ordinal))\n    {\n        throw new InvalidDataException(\n            $"Quest locale overlay was not applied: en={correctedFixtureQuest.Name}, ko={correctedFixtureQuest.NameKo}.");\n    }\n    if (correctedFixtureQuest.RequiredLevel != 3 ||\n        prestigeFixtureQuest.RequiredPrestigeLevel != 2 ||\n        addedPrestigeQuest.RequiredPrestigeLevel != 5)\n    {\n        throw new InvalidDataException(\n            $"Quest overlay/prestige mapping failed: level={correctedFixtureQuest.RequiredLevel}, " +\n            $"prestige={prestigeFixtureQuest.RequiredPrestigeLevel}, added={addedPrestigeQuest.RequiredPrestigeLevel}.");\n    }\n\n''')

replace_once(
    program,
    '''    if (result.ItemCount != 4 || result.AmmoCount != 1 || result.QuestCount != 3 || result.HideoutStationCount != 1)\n''',
    '''    if (result.ItemCount != 4 || result.AmmoCount != 1 || result.QuestCount != 4 || result.HideoutStationCount != 1)\n''')

print("quest catalog sync patch applied")
