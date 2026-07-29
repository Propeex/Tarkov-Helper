using Microsoft.Data.Sqlite;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Persists objective progress against an explicitly captured profile.
/// </summary>
public sealed class ProfileScopedObjectiveProgressStore
{
    private static ProfileScopedObjectiveProgressStore? _instance;
    public static ProfileScopedObjectiveProgressStore Instance =>
        _instance ??= new ProfileScopedObjectiveProgressStore();

    private readonly UserDataDbService _database = UserDataDbService.Instance;

    private ProfileScopedObjectiveProgressStore()
    {
    }

    public async Task SaveAsync(
        string id,
        string? questId,
        bool isCompleted,
        ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql = """
            INSERT INTO ObjectiveProgress (Id, ProfileType, QuestId, IsCompleted, UpdatedAt)
            VALUES (@id, @profileType, @questId, @isCompleted, @updatedAt)
            ON CONFLICT(Id, ProfileType) DO UPDATE SET
                QuestId = @questId,
                IsCompleted = @isCompleted,
                UpdatedAt = @updatedAt;
            """;

        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@profileType", (int)profile);
        command.Parameters.AddWithValue("@questId", questId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@isCompleted", isCompleted ? 1 : 0);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql =
            "DELETE FROM ObjectiveProgress WHERE Id = @id AND ProfileType = @profileType;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@profileType", (int)profile);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task ClearAllAsync(ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql =
            "DELETE FROM ObjectiveProgress WHERE ProfileType = @profileType;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@profileType", (int)profile);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<Dictionary<string, bool>> LoadAsync(ProfileType profile)
    {
        await _database.InitializeAsync().ConfigureAwait(false);
        await using var connection = CreateConnection(readOnly: true);
        await connection.OpenAsync().ConfigureAwait(false);

        const string sql =
            "SELECT Id, IsCompleted FROM ObjectiveProgress WHERE ProfileType = @profileType;";
        await using var command = new SqliteCommand(sql, connection);
        command.Parameters.AddWithValue("@profileType", (int)profile);

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            result[reader.GetString(0)] = reader.GetInt32(1) == 1;

        return result;
    }

    private SqliteConnection CreateConnection(bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _database.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
            Pooling = true,
            Cache = SqliteCacheMode.Shared
        };
        return new SqliteConnection(builder.ConnectionString);
    }
}
