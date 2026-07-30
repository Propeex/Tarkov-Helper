using TarkovHelper.Models.Map;

namespace TarkovHelper.Services.Map;

/// <summary>
/// 지도와 미니맵에서 동일한 탈출구 표시 단위를 만들기 위한 위치 기반 그룹화 규칙입니다.
/// PMC와 Scav 행이 같은 위치에 따로 저장된 공용 탈출구는 PMC 범주 하나로 표시합니다.
/// </summary>
public static class MapExtractDisplayGrouping
{
    private const double GroupDistance = 10.0;

    public readonly record struct DisplayExtract(
        MapExtract Extract,
        ExtractFaction Faction,
        int SourceCount);

    public static IReadOnlyList<DisplayExtract> GroupForDisplay(
        IEnumerable<MapExtract>? extracts)
    {
        if (extracts == null)
            return Array.Empty<DisplayExtract>();

        var source = extracts.ToList();
        if (source.Count == 0)
            return Array.Empty<DisplayExtract>();

        var used = new bool[source.Count];
        var result = new List<DisplayExtract>();

        for (var index = 0; index < source.Count; index++)
        {
            if (used[index])
                continue;

            var anchor = source[index];
            var group = new List<MapExtract> { anchor };
            used[index] = true;

            for (var otherIndex = index + 1; otherIndex < source.Count; otherIndex++)
            {
                if (used[otherIndex])
                    continue;

                var other = source[otherIndex];
                var distance = Math.Sqrt(
                    Math.Pow(anchor.X - other.X, 2) +
                    Math.Pow(anchor.Z - other.Z, 2));
                var sameName = string.Equals(
                    anchor.Name,
                    other.Name,
                    StringComparison.OrdinalIgnoreCase);
                var differentFaction = anchor.Faction != other.Faction;

                if (distance < GroupDistance && (sameName || differentFaction))
                {
                    group.Add(other);
                    used[otherIndex] = true;
                }
            }

            result.Add(Classify(group));
        }

        return result;
    }

    private static DisplayExtract Classify(IReadOnlyList<MapExtract> group)
    {
        var hasPmc = group.Any(extract =>
            extract.Faction is ExtractFaction.Pmc or ExtractFaction.Shared);
        var hasScav = group.Any(extract => extract.Faction == ExtractFaction.Scav);

        if (hasPmc && hasScav)
        {
            var representative = group.FirstOrDefault(extract =>
                                     extract.Faction == ExtractFaction.Pmc)
                                 ?? group.FirstOrDefault(extract =>
                                     extract.Faction == ExtractFaction.Shared)
                                 ?? group[0];
            return new DisplayExtract(
                representative,
                ExtractFaction.Pmc,
                group.Count);
        }

        var first = group[0];
        var faction = first.Faction == ExtractFaction.Shared
            ? ExtractFaction.Pmc
            : first.Faction;
        return new DisplayExtract(first, faction, group.Count);
    }
}
