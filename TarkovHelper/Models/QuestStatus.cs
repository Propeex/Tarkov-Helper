namespace TarkovHelper.Models
{
    /// <summary>
    /// Quest progress status exposed by the helper.
    /// There is deliberately no separate "available to accept" state.
    /// </summary>
    public enum QuestStatus
    {
        /// <summary>
        /// Cannot be activated - prerequisites not met
        /// </summary>
        Locked,

        /// <summary>
        /// Quest is shown as in progress after all start conditions are met.
        /// An explicitly persisted Active row separately records a real start event
        /// for prerequisite rules that require the predecessor to be active.
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
        /// Quest is not usable due to edition, prestige, or faction requirements
        /// </summary>
        Unavailable
    }
}
