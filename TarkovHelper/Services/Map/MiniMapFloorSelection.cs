using TarkovHelper.Models.Map;

namespace TarkovHelper.Services.Map;

/// <summary>
/// 미니맵 층 목록의 정렬, 초기 선택 및 위/아래 이동 규칙입니다.
/// WPF 창과 회귀 검사가 동일한 순서 규칙을 사용하도록 UI에서 분리합니다.
/// </summary>
public static class MiniMapFloorSelection
{
    public static IReadOnlyList<MapFloorConfig> Order(IEnumerable<MapFloorConfig>? floors)
    {
        if (floors == null)
            return Array.Empty<MapFloorConfig>();

        return floors
            .Where(floor => !string.IsNullOrWhiteSpace(floor.LayerId))
            .OrderBy(floor => floor.Order)
            .ToList();
    }

    public static bool Contains(
        IEnumerable<MapFloorConfig>? floors,
        string? floorId)
    {
        if (string.IsNullOrWhiteSpace(floorId))
            return false;

        return Order(floors).Any(floor => string.Equals(
            floor.LayerId,
            floorId,
            StringComparison.OrdinalIgnoreCase));
    }

    public static string? SelectInitial(
        IEnumerable<MapFloorConfig>? floors,
        string? detectedFloorId)
    {
        var ordered = Order(floors);
        if (ordered.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(detectedFloorId))
        {
            var detected = ordered.FirstOrDefault(floor => string.Equals(
                floor.LayerId,
                detectedFloorId,
                StringComparison.OrdinalIgnoreCase));
            if (detected != null)
                return detected.LayerId;
        }

        return (ordered.FirstOrDefault(floor => floor.IsDefault) ?? ordered[0]).LayerId;
    }

    public static string? Move(
        IEnumerable<MapFloorConfig>? floors,
        string? currentFloorId,
        int direction)
    {
        var ordered = Order(floors);
        if (ordered.Count == 0)
            return null;

        var currentIndex = -1;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (string.Equals(
                    ordered[index].LayerId,
                    currentFloorId,
                    StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
            currentIndex = ordered.ToList().FindIndex(floor => floor.IsDefault);
        if (currentIndex < 0)
            currentIndex = 0;

        var targetIndex = Math.Clamp(
            currentIndex + Math.Sign(direction),
            0,
            ordered.Count - 1);
        return ordered[targetIndex].LayerId;
    }
}
