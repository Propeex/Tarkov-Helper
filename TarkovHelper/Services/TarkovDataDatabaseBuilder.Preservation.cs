using System.Text;

namespace TarkovHelper.Services;

internal sealed partial class TarkovDataDatabaseBuilder
{
    private static readonly HashSet<string> ApiManagedObjectiveColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "QuestId",
        "Description",
        "DescriptionEN",
        "DescriptionKO",
        "ObjectiveType",
        "Optional",
        "SortOrder",
        "TargetCount",
        "RequiresFIR",
        "DogtagMinLevel",
        "ItemId",
        "ItemName",
        "UpdatedAt"
    };

    /// <summary>
    /// tarkov.dev exposes every accepted dogtag skin/prestige variant as a separate
    /// item ID. They are alternatives for one faction requirement, not quantities
    /// that must all be collected. Keep one stable representative per faction.
    /// </summary>
    private static List<ApiItemReference> CollapseLogicalRequiredItems(
        IReadOnlyList<ApiItemReference> items)
    {
        if (items.Count <= 1)
            return items.ToList();

        var result = new List<ApiItemReference>(items.Count);
        var emittedDogtagGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var groupKey = GetDogtagGroupKey(item);
            if (groupKey == null)
            {
                result.Add(item);
                continue;
            }

            if (!emittedDogtagGroups.Add(groupKey))
                continue;

            var representative = items
                .Where(candidate => string.Equals(
                    GetDogtagGroupKey(candidate),
                    groupKey,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => DogtagRepresentativeRank(candidate, groupKey))
                .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .First();

            result.Add(representative);
        }

        return result;
    }

    private static string? GetDogtagGroupKey(ApiItemReference item)
    {
        var text = $"{item.Name} {item.NormalizedName}";
        if (!text.Contains("dogtag", StringComparison.OrdinalIgnoreCase))
            return null;

        if (text.Contains("usec", StringComparison.OrdinalIgnoreCase))
            return "dogtag:usec";
        if (text.Contains("bear", StringComparison.OrdinalIgnoreCase))
            return "dogtag:bear";

        return null;
    }

    private static int DogtagRepresentativeRank(ApiItemReference item, string groupKey)
    {
        var faction = groupKey.EndsWith("usec", StringComparison.OrdinalIgnoreCase)
            ? "usec"
            : "bear";
        var expectedNormalizedName = $"dogtag-{faction}";
        var normalizedName = item.NormalizedName?.Trim();
        var normalizedDisplayName = Normalize(item.Name);

        if (string.Equals(normalizedName, expectedNormalizedName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedDisplayName, expectedNormalizedName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return 1;
    }

    /// <summary>
    /// API objective IDs are not guaranteed to match the hand-maintained marker
    /// objective IDs in the bundled database. Preserve every coordinate-bearing
    /// legacy row whose quest still exists. Prefer merging coordinates into the
    /// matching API objective; otherwise retain a dedicated legacy marker row.
    /// </summary>
    private static int PreserveLegacyQuestObjectiveLocations(
        TableSnapshot objectiveSnapshot,
        IReadOnlyCollection<RowData> questRows,
        List<RowData> objectiveRows)
    {
        var validQuestIds = questRows
            .Select(row => ReadString(row, "Id"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var usedObjectiveIds = objectiveRows
            .Select(row => ReadString(row, "Id"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var preserved = 0;
        foreach (var legacyRow in objectiveSnapshot.Rows.Where(HasCoordinatePayload))
        {
            var legacyQuestId = ReadString(legacyRow, "QuestId");
            if (string.IsNullOrWhiteSpace(legacyQuestId) || !validQuestIds.Contains(legacyQuestId))
                continue;

            var legacyId = ReadString(legacyRow, "Id");
            var target = !string.IsNullOrWhiteSpace(legacyId)
                ? objectiveRows.FirstOrDefault(row =>
                    string.Equals(ReadString(row, "Id"), legacyId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ReadString(row, "QuestId"), legacyQuestId, StringComparison.OrdinalIgnoreCase))
                : null;

            target ??= FindMatchingApiObjective(legacyRow, objectiveRows, legacyQuestId);
            if (target != null)
            {
                CopyManualObjectiveFields(legacyRow, target);
                preserved++;
                continue;
            }

            var retainedRow = CloneRow(legacyRow);
            var retainedId = string.IsNullOrWhiteSpace(legacyId)
                ? $"legacy-location:{legacyQuestId}:{preserved + 1}"
                : legacyId;

            if (!usedObjectiveIds.Add(retainedId))
            {
                var baseId = $"{retainedId}:legacy-location:{NormalizeKeyPart(legacyQuestId)}";
                retainedId = baseId;
                var collisionIndex = 2;
                while (!usedObjectiveIds.Add(retainedId))
                {
                    retainedId = $"{baseId}:{collisionIndex}";
                    collisionIndex++;
                }
            }

            Set(retainedRow, "Id", retainedId);
            Set(retainedRow, "QuestId", legacyQuestId);

            // A legacy coordinate row can reference an item that no longer exists in
            // the current API data set. Markers need only quest/map/coordinate data;
            // retaining the stale item foreign key would reject the rebuilt database.
            Set(retainedRow, "ItemId", null);

            objectiveRows.Add(retainedRow);
            preserved++;
        }

        return preserved;
    }

    private static RowData? FindMatchingApiObjective(
        RowData legacyRow,
        IReadOnlyCollection<RowData> objectiveRows,
        string questId)
    {
        var legacyDescription = ObjectiveDescriptionKey(legacyRow);
        if (string.IsNullOrWhiteSpace(legacyDescription))
            return null;

        var legacyType = ReadString(legacyRow, "ObjectiveType");
        var candidates = objectiveRows
            .Where(row => string.Equals(
                ReadString(row, "QuestId"),
                questId,
                StringComparison.OrdinalIgnoreCase))
            .Where(row => string.Equals(
                ObjectiveDescriptionKey(row),
                legacyDescription,
                StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(legacyType) ||
                          string.IsNullOrWhiteSpace(ReadString(row, "ObjectiveType")) ||
                          string.Equals(
                              ReadString(row, "ObjectiveType"),
                              legacyType,
                              StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool HasCoordinatePayload(RowData row)
    {
        return !string.IsNullOrWhiteSpace(ReadString(row, "LocationPoints")) ||
               !string.IsNullOrWhiteSpace(ReadString(row, "OptionalPoints"));
    }

    private static void CopyManualObjectiveFields(RowData source, RowData target)
    {
        foreach (var (column, value) in source)
        {
            if (!ApiManagedObjectiveColumns.Contains(column))
                target[column] = value;
        }
    }

    private static string ObjectiveDescriptionKey(RowData row)
    {
        var text = ReadString(row, "DescriptionEN") ?? ReadString(row, "Description");
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string NormalizeKeyPart(string value)
    {
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "quest" : normalized;
    }
}