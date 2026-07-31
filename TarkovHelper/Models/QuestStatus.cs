namespace TarkovHelper.Models
{
    /// <summary>
    /// Quest completion status
    /// </summary>
    public enum QuestStatus
    {
        /// <summary>
        /// Cannot be activated - prerequisites not met
        /// </summary>
        Locked,

        /// <summary>
        /// Quest was actually accepted/started and is currently in progress
        /// </summary>
        Active,

        /// <summary>
        /// Completed successfully
        /// </summary>
        Done,

        /// <summary>
        /// Failed (user marked as failed)
        /// </summary>
        Failed,

        /// <summary>
        /// Prerequisites met but player level is too low
        /// </summary>
        LevelLocked,

        /// <summary>
        /// Quest is not available due to edition, prestige, or faction requirements
        /// </summary>
        Unavailable,

        /// <summary>
        /// Legacy persisted value from v1.8.1. Runtime status evaluation normalizes
        /// this value to Active and the UI never exposes it.
        /// </summary>
        [Obsolete("Available is a legacy persisted value; eligible quests are Active.")]
        Available
    }
}
