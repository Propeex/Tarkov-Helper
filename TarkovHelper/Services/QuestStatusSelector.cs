namespace TarkovHelper.Services;

public sealed class QuestStatusSelector
{
    public const string DefaultStatus = "All";

    public string SelectedStatus { get; private set; } = DefaultStatus;

    public void ApplyDefault()
    {
        SelectedStatus = DefaultStatus;
    }
}
