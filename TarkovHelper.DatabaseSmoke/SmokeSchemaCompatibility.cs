using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

internal static class SmokeSchemaCompatibility
{
    [ModuleInitializer]
    internal static void EnsureSmokeVerificationColumns()
    {
        if (Environment.GetCommandLineArgs().Contains("--external", StringComparer.OrdinalIgnoreCase))
            return;

        var databasePath = Path.Combine(AppContext.BaseDirectory, "Assets", "tarkov_data.db");
        if (!File.Exists(databasePath))
            return;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ConnectionString);
        connection.Open();

        EnsureColumn(connection, "Items", "NormalizedName", "TEXT");
        EnsureColumn(connection, "QuestObjectives", "DescriptionEN", "TEXT");
        EnsureColumn(connection, "QuestObjectives", "DescriptionKO", "TEXT");
        EnsureColumn(connection, "QuestRequiredItems", "ObjectiveId", "TEXT");
        EnsureColumn(connection, "QuestRequiredItems", "RequirementGroupId", "TEXT");
        EnsureColumn(connection, "QuestRequiredItems", "IsAlternativeGroup", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "QuestRequiredItems", "AlternativeItemIds", "TEXT");
        EnsureColumn(connection, "QuestRequiredItems", "AlternativeItemNames", "TEXT");
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string sqlType)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = @name;";
        check.Parameters.AddWithValue("@name", columnName);
        if (Convert.ToInt32(check.ExecuteScalar()) != 0)
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE [{tableName}] ADD COLUMN [{columnName}] {sqlType};";
        alter.ExecuteNonQuery();
    }
}