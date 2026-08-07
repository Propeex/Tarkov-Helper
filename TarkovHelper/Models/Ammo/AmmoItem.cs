using System.Windows.Media;

namespace TarkovHelper.Models.Ammo;

public sealed class AmmoItem
{
    public string ItemId { get; init; } = string.Empty;
    public string LocalItemId { get; init; } = string.Empty;
    public string NameKo { get; init; } = string.Empty;
    public string Caliber { get; init; } = string.Empty;
    public string CaliberDisplay { get; init; } = string.Empty;
    public string? IconPath { get; init; }
    public int ProjectileCount { get; init; } = 1;
    public int Damage { get; init; }
    public int PenetrationPower { get; init; }
    public int ArmorDamage { get; init; }
    public double AccuracyModifier { get; init; }
    public double RecoilModifier { get; init; }
    public double FragmentationChance { get; init; }
    public double LightBleedModifier { get; init; }
    public double HeavyBleedModifier { get; init; }
    public double InitialSpeed { get; init; }
    public double RicochetChance { get; init; }
    public double PenetrationChance { get; init; }
    public double BulletMassGrams { get; init; }
    public double BallisticCoefficient { get; init; }
    public double DurabilityBurnFactor { get; init; }
    public double HeatFactor { get; init; }
    public double MisfireChance { get; init; }
    public double FailureToFeedChance { get; init; }
    public bool Tracer { get; init; }
    public string? TracerColor { get; init; }
    public string? AmmoType { get; init; }
    public string AcquisitionSource { get; init; } = "raid-found";

    public string DamageText => ProjectileCount > 1 ? $"{Damage} × {ProjectileCount}" : Damage.ToString();
    public string ArmorDamageText => $"{ArmorDamage}%";
    public string AccuracyText => FormatSignedPercent(AccuracyModifier);
    public string RecoilText => FormatSignedPercent(RecoilModifier);
    public string FragmentationText => $"{FragmentationChance * 100:0.#}%";
    public string BleedText => $"{LightBleedModifier * 100:0.#}% / {HeavyBleedModifier * 100:0.#}%";
    public string InitialSpeedText => $"{InitialSpeed:0.#} m/s";
    public string RicochetText => $"{RicochetChance * 100:0.#}%";
    public string PenetrationChanceText => $"{PenetrationChance * 100:0.#}%";
    public string BulletMassText => BulletMassGrams > 0 ? $"{BulletMassGrams:0.##} g" : "-";
    public string ReliabilityText => $"불발 {MisfireChance * 100:0.###}% · 급탄 {FailureToFeedChance * 100:0.###}%";
    public string TracerText => Tracer ? $"예 ({TracerColor ?? "색상 미상"})" : "아니요";

    public IReadOnlyList<AmmoArmorClassResult> ArmorClasses => Enumerable.Range(1, 6)
        .Select(armorClass => AmmoArmorClassResult.Create(armorClass, PenetrationPower, ArmorDamage))
        .ToArray();

    public int ArmorEfficiencyScore => ArmorClasses.Sum(result => result.Effectiveness);

    private static string FormatSignedPercent(double value)
    {
        var percent = Math.Abs(value) <= 2 ? value * 100 : value;
        return $"{percent:+0.#;-0.#;0}%";
    }
}

public sealed record AmmoArmorClassResult(int ArmorClass, int Effectiveness, Brush Background, Brush Foreground)
{
    public string DisplayText => Effectiveness.ToString();
    public static AmmoArmorClassResult Create(int armorClass, int penetrationPower, int armorDamage)
    {
        var threshold = armorClass * 10;
        var delta = penetrationPower - threshold;
        var effect = delta switch
        {
            >= 0 => 6,
            >= -3 => 5,
            >= -6 => 4,
            >= -10 => 3,
            >= -15 => 2,
            >= -20 => 1,
            _ => 0
        };

        if (effect is > 0 and < 6 && armorDamage >= 55)
            effect = Math.Min(6, effect + 1);

        var color = effect switch
        {
            0 => Color.FromRgb(96, 30, 30),
            1 => Color.FromRgb(177, 45, 45),
            2 => Color.FromRgb(217, 106, 42),
            3 => Color.FromRgb(218, 177, 52),
            4 => Color.FromRgb(164, 190, 58),
            5 => Color.FromRgb(80, 171, 73),
            _ => Color.FromRgb(43, 132, 69)
        };

        return new AmmoArmorClassResult(
            armorClass,
            effect,
            new SolidColorBrush(color),
            Brushes.White);
    }
}
