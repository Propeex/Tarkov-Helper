namespace TarkovHelper.Services.Map;

/// <summary>
/// 일반 지도와 오버레이 미니맵이 같은 맵·층·자동 추적 상태를 사용하도록 중계합니다.
/// 화면 객체를 직접 참조하지 않으므로 어느 한쪽이 닫혀 있어도 마지막 상태를 유지합니다.
/// </summary>
public sealed class SharedMapFloorStateService
{
    private static readonly Lazy<SharedMapFloorStateService> LazyInstance =
        new(() => new SharedMapFloorStateService());

    private readonly object _syncRoot = new();

    public static SharedMapFloorStateService Instance => LazyInstance.Value;

    public string? MapKey { get; private set; }
    public string? FloorId { get; private set; }
    public bool IsAutomatic { get; private set; } = true;

    public event EventHandler<SharedMapFloorChangedEventArgs>? FloorChanged;

    private SharedMapFloorStateService()
    {
    }

    public void Publish(
        string? mapKey,
        string? floorId,
        bool isAutomatic,
        object? source = null)
    {
        if (string.IsNullOrWhiteSpace(mapKey))
            return;

        SharedMapFloorChangedEventArgs args;
        lock (_syncRoot)
        {
            var changed = !string.Equals(MapKey, mapKey, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(FloorId, floorId, StringComparison.OrdinalIgnoreCase) ||
                          IsAutomatic != isAutomatic;
            if (!changed)
                return;

            MapKey = mapKey;
            FloorId = floorId;
            IsAutomatic = isAutomatic;
            args = new SharedMapFloorChangedEventArgs(mapKey, floorId, isAutomatic, source);
        }

        FloorChanged?.Invoke(this, args);
    }

    public SharedMapFloorSnapshot Capture()
    {
        lock (_syncRoot)
            return new SharedMapFloorSnapshot(MapKey, FloorId, IsAutomatic);
    }
}

public sealed class SharedMapFloorChangedEventArgs : EventArgs
{
    public SharedMapFloorChangedEventArgs(
        string mapKey,
        string? floorId,
        bool isAutomatic,
        object? source)
    {
        MapKey = mapKey;
        FloorId = floorId;
        IsAutomatic = isAutomatic;
        Source = source;
    }

    public string MapKey { get; }
    public string? FloorId { get; }
    public bool IsAutomatic { get; }
    public object? Source { get; }
}

public readonly record struct SharedMapFloorSnapshot(
    string? MapKey,
    string? FloorId,
    bool IsAutomatic);
