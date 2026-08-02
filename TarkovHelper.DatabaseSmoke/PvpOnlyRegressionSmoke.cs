using TarkovHelper.Services;

internal static class PvpOnlyRegressionSmoke
{
    public static void Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "PvpOnlySmoke");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        try
        {
            var questLog = Path.Combine(root, "push-notifications_000.log");
            var applicationLog = Path.Combine(root, "application_000.log");
            File.WriteAllText(questLog, "{}");

            File.WriteAllText(applicationLog, "2026-08-02|Info|application|Session mode: Pve\n");
            if (LogSyncService.ShouldProcessPvpQuestLog(questLog))
                throw new InvalidDataException("A positively identified PVE quest log was accepted.");

            File.WriteAllText(applicationLog, "2026-08-02|Info|application|Session mode: Pvp\n");
            if (!LogSyncService.ShouldProcessPvpQuestLog(questLog))
                throw new InvalidDataException("A PVP quest log was rejected.");

            File.Delete(applicationLog);
            if (!LogSyncService.ShouldProcessPvpQuestLog(questLog))
                throw new InvalidDataException("A legacy quest log with unknown mode was rejected.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
