using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models.Ammo;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services.Ammo;

public sealed class AmmoDbService
{
    private static readonly ILogger Log = TarkovHelper.Services.Logging.Log.For<AmmoDbService>();
    private static readonly Lazy<AmmoDbService> LazyInstance = new(() => new AmmoDbService());
    private readonly string _databasePath = DatabaseUpdateService.Instance.DatabasePath;
    private IReadOnlyList<AmmoItem> _items = Array.Empty<AmmoItem>();

    public static AmmoDbService Instance => LazyInstance.Value;
    public IReadOnlyList<AmmoItem> Items => _items;
    public event EventHandler? DataRefreshed;

    private AmmoDbService()
    {
        DatabaseUpdateService.Instance.DatabaseUpdated += async (_, _) => await RefreshAsync();
    }

    public async Task<bool> RefreshAsync()
    {
        if (!File.Exists(_databasePath))
            return false;

        try
        {
            var loaded = new List<AmmoItem>();
            await using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
            await connection.OpenAsync();

            await using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Ammo';";
                if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
                {
                    _items = Array.Empty<AmmoItem>();
                    DataRefreshed?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.ItemId,
                       i.Id,
                       COALESCE(NULLIF(i.NameKO, ''), NULLIF(i.NameEN, ''), i.Name),
                       a.Caliber,
                       a.ProjectileCount,
                       a.Damage,
                       a.PenetrationPower,
                       a.ArmorDamage,
                       a.AccuracyModifier,
                       a.RecoilModifier,
                       a.FragmentationChance,
                       a.LightBleedModifier,
                       a.HeavyBleedModifier,
                       COALESCE(NULLIF(a.AcquisitionSource, ''), '레이드 획득/기타')
                FROM Ammo a
                JOIN Items i ON i.BsgId = a.ItemId OR i.Id = a.ItemId
                ORDER BY a.Caliber, COALESCE(NULLIF(i.NameKO, ''), NULLIF(i.NameEN, ''), i.Name);
                """;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var localItemId = reader.GetString(1);
                var iconFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Icons", localItemId + ".png");
                loaded.Add(new AmmoItem
                {
                    ItemId = reader.GetString(0),
                    LocalItemId = localItemId,
                    NameKo = reader.IsDBNull(2) ? reader.GetString(0) : reader.GetString(2),
                    Caliber = reader.IsDBNull(3) ? "기타" : reader.GetString(3),
                    CaliberDisplay = AmmoLocalization.GetCaliberDisplay(reader.IsDBNull(3) ? "기타" : reader.GetString(3)),
                    IconPath = File.Exists(iconFile) ? iconFile : null,
                    ProjectileCount = reader.IsDBNull(4) ? 1 : Math.Max(1, reader.GetInt32(4)),
                    Damage = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    PenetrationPower = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    ArmorDamage = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    AccuracyModifier = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                    RecoilModifier = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                    FragmentationChance = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                    LightBleedModifier = reader.IsDBNull(11) ? 0 : reader.GetDouble(11),
                    HeavyBleedModifier = reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
                    AcquisitionSource = AmmoLocalization.TranslateAcquisition(reader.IsDBNull(13) ? null : reader.GetString(13))
                });
            }

            _items = loaded;
            DataRefreshed?.Invoke(this, EventArgs.Empty);
            Log.Info($"Loaded {loaded.Count} ammo records from DB");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load ammo data", ex);
            return false;
        }
    }
}

internal static class AmmoLocalization
{
    private static readonly Dictionary<string, string> Calibers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Caliber1143x23ACP"] = ".45 ACP",
        ["Caliber127x33"] = "12.7×33mm",
        ["Caliber127x55"] = "12.7×55mm",
        ["Caliber127x99"] = ".50 BMG (12.7×99mm)",
        ["Caliber12g"] = "12/70",
        ["Caliber20g"] = "20/70",
        ["Caliber20x1mm"] = "20×1mm",
        ["Caliber23x75"] = "23×75mmR",
        ["Caliber26x75"] = "26×75mm 신호탄",
        ["Caliber366TKM"] = ".366 TKM",
        ["Caliber40mmRU"] = "40mm 러시아 유탄",
        ["Caliber40x46"] = "40×46mm 유탄",
        ["Caliber46x30"] = "4.6×30mm HK",
        ["Caliber545x39"] = "5.45×39mm",
        ["Caliber556x45NATO"] = "5.56×45mm NATO",
        ["Caliber57x28"] = "5.7×28mm FN",
        ["Caliber68x51"] = "6.8×51mm",
        ["Caliber762x25TT"] = "7.62×25mm 토카레프",
        ["Caliber762x35"] = ".300 블랙아웃",
        ["Caliber762x39"] = "7.62×39mm",
        ["Caliber762x51"] = "7.62×51mm NATO",
        ["Caliber762x54R"] = "7.62×54mmR",
        ["Caliber784x49"] = "7.84×49mm",
        ["Caliber86x70"] = ".338 라푸아 매그넘 (8.6×70mm)",
        ["Caliber93x64"] = "9.3×64mm",
        ["Caliber9x18PM"] = "9×18mm 마카로프",
        ["Caliber9x19PARA"] = "9×19mm 파라벨룸",
        ["Caliber9x21"] = "9×21mm 규르자",
        ["Caliber9x33R"] = ".357 매그넘",
        ["Caliber9x39"] = "9×39mm"
    };

    public static string GetCaliberDisplay(string caliber)
    {
        if (Calibers.TryGetValue(caliber, out var translated))
            return translated;
        return caliber.Replace("Caliber", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace('x', '×');
    }

    public static string TranslateAcquisition(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "레이드 획득/기타";

        return source
            .Replace("Prapor", "프라퍼", StringComparison.OrdinalIgnoreCase)
            .Replace("Therapist", "테라피스트", StringComparison.OrdinalIgnoreCase)
            .Replace("Skier", "스키어", StringComparison.OrdinalIgnoreCase)
            .Replace("Peacekeeper", "피스키퍼", StringComparison.OrdinalIgnoreCase)
            .Replace("Mechanic", "메카닉", StringComparison.OrdinalIgnoreCase)
            .Replace("Ragman", "라그맨", StringComparison.OrdinalIgnoreCase)
            .Replace("Jaeger", "예거", StringComparison.OrdinalIgnoreCase)
            .Replace("Fence", "펜스", StringComparison.OrdinalIgnoreCase)
            .Replace("Ref", "레프", StringComparison.OrdinalIgnoreCase)
            .Replace("Lightkeeper", "라이트키퍼", StringComparison.OrdinalIgnoreCase)
            .Replace("Workbench", "작업대", StringComparison.OrdinalIgnoreCase)
            .Replace("Lavatory", "화장실", StringComparison.OrdinalIgnoreCase)
            .Replace("Nutrition Unit", "영양 공급소", StringComparison.OrdinalIgnoreCase)
            .Replace("purchase", "구매", StringComparison.OrdinalIgnoreCase)
            .Replace("barter", "교환", StringComparison.OrdinalIgnoreCase)
            .Replace("craft", "제작", StringComparison.OrdinalIgnoreCase)
            .Replace("task reward", "퀘스트 보상", StringComparison.OrdinalIgnoreCase)
            .Replace("raid/other", "레이드 획득/기타", StringComparison.OrdinalIgnoreCase)
            .Replace("trader purchase", "상인 구매", StringComparison.OrdinalIgnoreCase)
            .Replace("trader barter", "상인 교환", StringComparison.OrdinalIgnoreCase)
            .Replace("hideout craft", "은신처 제작", StringComparison.OrdinalIgnoreCase);
    }
}
