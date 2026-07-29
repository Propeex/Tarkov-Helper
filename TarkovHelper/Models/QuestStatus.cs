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
        /// All start conditions are met, but the quest has not actually been accepted yet
        /// </summary>
        Available
    }
}
