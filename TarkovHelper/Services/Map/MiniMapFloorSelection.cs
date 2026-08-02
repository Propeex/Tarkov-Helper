using TarkovHelper.Models.Map;

namespace TarkovHelper.Services.Map;

/// <summary>
/// 미니맵 층 목록의 정렬, 자동 선택 및 위/아래 이동 규칙입니다.
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

    /// <summary>
    /// 자동 감지 결과가 실제 설정된 층이면 해당 층을 선택합니다.
    /// 감지 실패 또는 DB에 없는 층이면 지상층(main), 기본층, 첫 번째 층 순서로 대체합니다.
    /// </summary>
    public static string? SelectAutomatic(
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

        return (ordered.FirstOrDefault(floor => string.Equals(
                    floor.LayerId,
                    "main",
                    StringComparison.OrdinalIgnoreCase))
                ?? ordered.FirstOrDefault(floor => floor.IsDefault)
                ?? ordered[0])
            .LayerId;
    }

    /// <summary>
    /// 수동 모드의 초기 층을 선택합니다. 지정된 층이 없으면 기본층을 사용합니다.
    /// </summary>
    public static string? SelectInitial(
        IEnumerable<MapFloorConfig>? floors,
        string? preferredFloorId)
    {
        var ordered = Order(floors);
        if (ordered.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredFloorId))
        {
            var preferred = ordered.FirstOrDefault(floor => string.Equals(
                floor.LayerId,
                preferredFloorId,
                StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
                return preferred.LayerId;
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
