using System.Collections;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Rebuilds the API-managed portion of tarkov_data.db from tarkov.dev.
/// The existing database is copied to a temporary file first so map assets,
/// hand-maintained coordinates, and unsupported columns remain intact.
/// </summary>
internal sealed partial class TarkovDataDatabaseBuilder
{
    private interface IApiEntity
    {
        string Id { get; }
    }

    private sealed class ApiItem : IApiEntity
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? NormalizedName { get; set; }
        public string? ShortName { get; set; }
        public string? Description { get; set; }
        public string? IconLink { get; set; }
        public string? WikiLink { get; set; }
        public ApiNamedEntity? Category { get; set; }
        public List<ApiNamedEntity> Categories { get; set; } = [];
    }

    private sealed class ApiTask : IApiEntity
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? NormalizedName { get; set; }
        public string? WikiLink { get; set; }
        public int? MinPlayerLevel { get; set; }
        public string? FactionName { get; set; }
        public bool KappaRequired { get; set; }
        public ApiNamedEntity? Trader { get; set; }
        public ApiNamedEntity? Map { get; set; }
        public ApiPrestige? RequiredPrestige { get; set; }
        public List<ApiTaskRequirement> TaskRequirements { get; set; } = [];
        public List<ApiTaskObjective> Objectives { get; set; } = [];
    }

    private sealed class ApiTaskRequirement
    {
        public ApiIdReference? Task { get; set; }
        public List<string> Status { get; set; } = [];
    }

    private sealed class ApiTaskObjective
    {
        private string? _typeName;
        private string? _type;

        [JsonPropertyName("__typename")]
        public string? TypeName
        {
            // The legacy writer recognizes TaskObjectiveItem through this property.
            // Expose it only for actual item-submission objectives so paired acquisition
            // and handover objectives cannot both become inventory requirements.
            get => QuestRequiredItemObjectivePolicy.IsConsumable(_type) ? _typeName : null;
            set => _typeName = value;
        }

        public string? Id { get; set; }

        public string? Type
        {
            // A generic "item" value carries no spending semantics. Give it a neutral
            // database label so the writer's legacy exact "item" check cannot treat it
            // as a consumable requirement without an explicit HandOver/giveItem type.
            get => string.Equals(_type, "item", StringComparison.OrdinalIgnoreCase)
                ? "genericItem"
                : _type;
            set => _type = value;
        }

        public string? Description { get; set; }
        public bool Optional { get; set; }
        public List<ApiNamedEntity> Maps { get; set; } = [];
        public List<ApiItemReference> Items { get; set; } = [];
        public int? Count { get; set; }
        public bool? FoundInRaid { get; set; }
        public int? DogTagLevel { get; set; }
    }

    private sealed class ApiHideoutStation : IApiEntity
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? NormalizedName { get; set; }
        public string? ImageLink { get; set; }
        public List<ApiHideoutLevel> Levels { get; set; } = [];
    }

    private sealed class ApiHideoutLevel
    {
        public string? Id { get; set; }
        public int Level { get; set; }
        public int ConstructionTime { get; set; }
        public List<ApiHideoutItemRequirement> ItemRequirements { get; set; } = [];
        public List<ApiStationRequirement> StationLevelRequirements { get; set; } = [];
        public List<ApiTraderRequirement> TraderRequirements { get; set; } = [];
        public List<ApiSkillRequirement> SkillRequirements { get; set; } = [];
    }

    private sealed class ApiHideoutItemRequirement
    {
        public ApiItemReference? Item { get; set; }
        public int? Count { get; set; }
        public int? Quantity { get; set; }
    }

    private sealed class ApiStationRequirement
    {
        public ApiNamedEntity? Station { get; set; }
        public int Level { get; set; }
    }

    private sealed class ApiTraderRequirement
    {
        public ApiNamedEntity? Trader { get; set; }
        public string? RequirementType { get; set; }
        public string? CompareMethod { get; set; }
        public int? Value { get; set; }
        public int? Level { get; set; }
    }

    private sealed class ApiSkillRequirement
    {
        public string? Name { get; set; }
        public ApiNamedEntity? Skill { get; set; }
        public int Level { get; set; }
    }

    private sealed class ApiNamedEntity
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? NormalizedName { get; set; }
    }

    private sealed class ApiItemReference
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? NormalizedName { get; set; }
        public string? IconLink { get; set; }
    }

    private sealed class ApiIdReference
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class ApiPrestige
    {
        public int? PrestigeLevel { get; set; }
    }

    private sealed record LocalizedItem(ApiItem English, ApiItem? Korean);

    private sealed record LocalizedTask
    {
        public LocalizedTask(ApiTask english, ApiTask? korean)
        {
            English = english;
            Korean = korean;
            ApplyOfficialKoreanObjectives();
        }

        public ApiTask English { get; }
        public ApiTask? Korean { get; }

        private void ApplyOfficialKoreanObjectives()
        {
            if (Korean == null || English.Objectives.Count == 0 || Korean.Objectives.Count == 0)
                return;

            var englishObjectivesById = English.Objectives
                .Where(objective => !string.IsNullOrWhiteSpace(objective.Id))
                .GroupBy(objective => objective.Id!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < Korean.Objectives.Count; index++)
            {
                var koreanObjective = Korean.Objectives[index];
                ApiTaskObjective? englishObjective = null;

                if (!string.IsNullOrWhiteSpace(koreanObjective.Id))
                    englishObjectivesById.TryGetValue(koreanObjective.Id, out englishObjective);

                if (englishObjective == null && index < English.Objectives.Count)
                    englishObjective = English.Objectives[index];

                if (englishObjective == null)
                    continue;

                koreanObjective.Description = QuestKoreanSourcePolicy.SelectQuestContent(
                    englishObjective.Description,
                    koreanObjective.Description);
            }
        }
    }

    private sealed record LocalizedHideoutStation(ApiHideoutStation English, ApiHideoutStation? Korean);
    private sealed record MergedApiData(
        List<LocalizedItem> Items,
        List<LocalizedTask> Tasks,
        List<LocalizedHideoutStation> HideoutStations);

    private sealed record ColumnInfo(
        string Name,
        string Type,
        bool NotNull,
        string? DefaultValue,
        int PrimaryKeyOrder);

    private sealed record TableSnapshot(List<ColumnInfo> Columns, List<RowData> Rows);
    private sealed record TableWrite(string TableName, List<RowData> Rows);

    private sealed class RowData : Dictionary<string, object?>
    {
        public RowData() : base(StringComparer.OrdinalIgnoreCase)
        {
            // Child requirement tables use TEXT primary keys. Generate an ID for
            // every new row; entity rows replace it with the stable API/database ID.
            this["Id"] = Guid.NewGuid().ToString("N");
        }

        public RowData(IDictionary<string, object?> source, IEqualityComparer<string> comparer)
            : base(source, comparer)
        {
            if (!TryGetValue("Id", out var id) || id is null || string.IsNullOrWhiteSpace(id.ToString()))
                this["Id"] = Guid.NewGuid().ToString("N");
        }
    }

    private sealed record DatabaseCounts(
        int Items,
        int Quests,
        int QuestRequirements,
        int QuestObjectives,
        int QuestRequiredItems,
        int HideoutStations,
        int HideoutLevels,
        int HideoutItemRequirements,
        int HideoutStationRequirements,
        int HideoutTraderRequirements,
        int HideoutSkillRequirements)
    {
        public int TotalRows => Items + Quests + QuestRequirements + QuestObjectives + QuestRequiredItems +
                                HideoutStations + HideoutLevels + HideoutItemRequirements +
                                HideoutStationRequirements + HideoutTraderRequirements + HideoutSkillRequirements;
    }
}

internal sealed record DatabaseBuildResult(
    int ItemCount,
    int QuestCount,
    int QuestRequiredItemCount,
    int HideoutStationCount,
    int HideoutRequiredItemCount,
    string BackupPath);
