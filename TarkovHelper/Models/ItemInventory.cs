using System.Text.Json.Serialization;

namespace TarkovHelper.Models
{
    /// <summary>
    /// Represents user's inventory quantity for an item with FIR/Non-FIR separation
    /// </summary>
    public class ItemInventory
    {
        /// <summary>
        /// Item normalized name (key for lookup)
        /// </summary>
        [JsonPropertyName("itemNormalizedName")]
        public string ItemNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Found in Raid quantity
        /// </summary>
        [JsonPropertyName("firQuantity")]
        public int FirQuantity { get; set; }

        /// <summary>
        /// Non-FIR quantity (purchased from flea market, etc.)
        /// </summary>
        [JsonPropertyName("nonFirQuantity")]
        public int NonFirQuantity { get; set; }

        /// <summary>
        /// Total quantity (FIR + Non-FIR)
        /// </summary>
        [JsonIgnore]
        public int TotalQuantity => FirQuantity + NonFirQuantity;
    }

    /// <summary>
    /// Container for all item inventory data (for JSON serialization)
    /// </summary>
    public class ItemInventoryData
    {
        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("items")]
        public Dictionary<string, ItemInventory> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fulfillment status for an item requirement
    /// </summary>
    public enum ItemFulfillmentStatus
    {
        /// <summary>
        /// No items owned (0/required)
        /// </summary>
        NotStarted,

        /// <summary>
        /// Some items owned but not enough
        /// </summary>
        PartiallyFulfilled,

        /// <summary>
        /// All requirements met
        /// </summary>
        Fulfilled
    }

    /// <summary>
    /// Detailed fulfillment information for an item
    /// </summary>
    public class ItemFulfillmentInfo
    {
        /// <summary>
        /// Item normalized name
        /// </summary>
        public string ItemNormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Total required quantity
        /// </summary>
        public int RequiredTotal { get; set; }

        /// <summary>
        /// Required FIR quantity (if FIR is required)
        /// </summary>
        public int RequiredFir { get; set; }

        /// <summary>
        /// User's FIR quantity owned
        /// </summary>
        public int OwnedFir { get; set; }

        /// <summary>
        /// User's Non-FIR quantity owned
        /// </summary>
        public int OwnedNonFir { get; set; }

        /// <summary>
        /// Total owned (FIR + Non-FIR)
        /// </summary>
        public int OwnedTotal => OwnedFir + OwnedNonFir;

        /// <summary>
        /// Whether FIR requirement is met
        /// </summary>
        public bool IsFirFulfilled => OwnedFir >= RequiredFir;

        /// <summary>
        /// Whether total requirement is met
        /// </summary>
        public bool IsTotalFulfilled => OwnedTotal >= RequiredTotal;

        /// <summary>
        /// Overall fulfillment status
        /// </summary>
        public ItemFulfillmentStatus Status => ItemRequirementFulfillment.GetStatus(
            RequiredTotal,
            RequiredFir,
            OwnedFir,
            OwnedNonFir);

        /// <summary>
        /// Progress percentage (0-100)
        /// </summary>
        public double ProgressPercent => ItemRequirementFulfillment.GetProgressPercent(
            RequiredTotal,
            RequiredFir,
            OwnedFir,
            OwnedNonFir);
    }

    /// <summary>
    /// Calculates mixed FIR and unrestricted item fulfillment without counting the same
    /// FIR item twice. FIR requirements and total requirements must both be satisfied.
    /// </summary>
    internal static class ItemRequirementFulfillment
    {
        public static ItemFulfillmentStatus GetStatus(
            int requiredTotal,
            int requiredFir,
            int ownedFir,
            int ownedNonFir)
        {
            var values = Normalize(requiredTotal, requiredFir, ownedFir, ownedNonFir);
            if (values.RequiredTotal == 0)
                return ItemFulfillmentStatus.Fulfilled;

            var ownedTotal = (long)values.OwnedFir + values.OwnedNonFir;
            if (values.OwnedFir >= values.RequiredFir && ownedTotal >= values.RequiredTotal)
                return ItemFulfillmentStatus.Fulfilled;

            return ownedTotal > 0
                ? ItemFulfillmentStatus.PartiallyFulfilled
                : ItemFulfillmentStatus.NotStarted;
        }

        public static double GetProgressPercent(
            int requiredTotal,
            int requiredFir,
            int ownedFir,
            int ownedNonFir)
        {
            var values = Normalize(requiredTotal, requiredFir, ownedFir, ownedNonFir);
            if (values.RequiredTotal == 0)
                return 100;

            // FIR items first satisfy the FIR-only bucket. Only surplus FIR items may
            // contribute to the unrestricted remainder, alongside non-FIR items.
            var firSatisfied = Math.Min(values.OwnedFir, values.RequiredFir);
            var unrestrictedRequired = values.RequiredTotal - values.RequiredFir;
            var unrestrictedAvailable =
                (long)values.OwnedNonFir + Math.Max(0, values.OwnedFir - values.RequiredFir);
            var unrestrictedSatisfied = Math.Min((long)unrestrictedRequired, unrestrictedAvailable);
            var satisfied = (long)firSatisfied + unrestrictedSatisfied;

            return Math.Min(100, (double)satisfied / values.RequiredTotal * 100);
        }

        private static FulfillmentValues Normalize(
            int requiredTotal,
            int requiredFir,
            int ownedFir,
            int ownedNonFir)
        {
            var normalizedTotal = Math.Max(0, Math.Max(requiredTotal, requiredFir));
            var normalizedFir = Math.Clamp(requiredFir, 0, normalizedTotal);

            return new FulfillmentValues(
                normalizedTotal,
                normalizedFir,
                Math.Max(0, ownedFir),
                Math.Max(0, ownedNonFir));
        }

        private readonly record struct FulfillmentValues(
            int RequiredTotal,
            int RequiredFir,
            int OwnedFir,
            int OwnedNonFir);
    }
}
