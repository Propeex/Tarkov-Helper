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
        [JsonPropertyName("__typename")]
        public string? TypeName { get; set; }
        public string? Id { get; set; }
        public string? Type { get; set; }
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
    private sealed record LocalizedTask(ApiTask English, ApiTask? Korean);
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
        public RowData() : base(StringComparer.OrdinalIgnoreCase) { }
        public RowData(IDictionary<string, object?> source, IEqualityComparer<string> comparer)
            : base(source, comparer) { }
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
