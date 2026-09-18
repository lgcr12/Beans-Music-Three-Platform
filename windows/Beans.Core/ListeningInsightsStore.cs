using Microsoft.Data.Sqlite;

namespace Beans.Core;

public sealed record ListeningInsight(LocalMusicTrack Track, int PlayCount, DateTimeOffset LastPlayedAt, bool IsFavorite);

public sealed class ListeningInsightsStore(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS listening_insights (
                track_id TEXT PRIMARY KEY,
                path TEXT NOT NULL,
                title TEXT NOT NULL,
                extension TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified_at TEXT NOT NULL,
                play_count INTEGER NOT NULL DEFAULT 0,
                last_played_at TEXT NOT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task RecordPlayAsync(LocalMusicTrack track)
    {
        await InitializeAsync();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO listening_insights
                (track_id, path, title, extension, size, modified_at, play_count, last_played_at, is_favorite)
            VALUES ($id, $path, $title, $extension, $size, $modified, 1, $played, 0)
            ON CONFLICT(track_id) DO UPDATE SET
                path = excluded.path,
                title = excluded.title,
                extension = excluded.extension,
                size = excluded.size,
                modified_at = excluded.modified_at,
                play_count = listening_insights.play_count + 1,
                last_played_at = excluded.last_played_at;
            """;
        BindTrack(command, track);
        command.Parameters.AddWithValue("$played", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> ToggleFavoriteAsync(string trackId)
    {
        await InitializeAsync();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE listening_insights SET is_favorite = CASE is_favorite WHEN 0 THEN 1 ELSE 0 END WHERE track_id = $id; SELECT is_favorite FROM listening_insights WHERE track_id = $id;";
        command.Parameters.AddWithValue("$id", trackId);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value ?? 0) == 1;
    }

    public async Task<IReadOnlyList<ListeningInsight>> SnapshotAsync(int limit = 12)
    {
        await InitializeAsync();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT track_id, path, title, extension, size, modified_at, play_count, last_played_at, is_favorite
            FROM listening_insights
            ORDER BY last_played_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        var result = new List<ListeningInsight>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var track = new LocalMusicTrack(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                DateTimeOffset.Parse(reader.GetString(5)));
            result.Add(new ListeningInsight(track, reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7)), reader.GetInt32(8) == 1));
        }
        return result;
    }

    private static void BindTrack(SqliteCommand command, LocalMusicTrack track)
    {
        command.Parameters.AddWithValue("$id", track.Id);
        command.Parameters.AddWithValue("$path", track.Path);
        command.Parameters.AddWithValue("$title", track.Title);
        command.Parameters.AddWithValue("$extension", track.Extension);
        command.Parameters.AddWithValue("$size", track.Size);
        command.Parameters.AddWithValue("$modified", track.ModifiedAt.ToString("O"));
    }
}
