using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GlyphLatch.Windows;

internal sealed record FolderSpec(string Id, string Name, string? Parent, string? Source);
internal sealed record FontLink(string? Folder, string Path);
internal sealed record SetSpec(string Id, string Name, List<string> Fonts);

internal sealed class LibraryState
{
    public List<FolderSpec> Folders { get; init; } = [];
    public List<FontLink> Links { get; init; } = [];
    public List<SetSpec> Sets { get; init; } = [];
    public List<string> HiddenFonts { get; init; } = [];
    public List<string> HiddenFolders { get; init; } = [];
    public Dictionary<string, string> FolderAliases { get; init; } = [];
    public double SidebarWidth { get; set; } = 215;
    public SandboxSample SandboxMode { get; set; } = SandboxSample.Custom;
    public bool SandboxCompare { get; set; }
    public bool SandboxComparisonSeeded { get; set; }
    public List<SandboxSpec> Sandbox { get; init; } =
    [
        new("Город в деталях", 52, null),
        new("Типографика для нового проекта", 27, null),
        new("У каждой гарнитуры свой характер. Сравните, как разные начертания работают вместе в заголовке и в тексте.", 18, null)
    ];
    public bool Dark { get; set; }
    public string Specimen { get; set; } = "The quick brown fox jumps over the lazy dog";
    public string? PreviewMode { get; set; }
    public double Size { get; set; } = 42;
}

internal sealed class LibraryStore
{
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlyphLatch");
    private readonly string connectionString;
    private readonly string dataDirectory;
    internal string PreviewDirectory => Path.Combine(dataDirectory, "preview-cache");

    internal LibraryStore(string? directory = null)
    {
        dataDirectory = directory ?? DataDirectory;
        Directory.CreateDirectory(dataDirectory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "library.db") }.ToString();
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS library (id INTEGER PRIMARY KEY, state TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS index_cache (id INTEGER PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS registrations (
                path TEXT PRIMARY KEY COLLATE NOCASE, boot TEXT NOT NULL, session INTEGER NOT NULL,
                flags INTEGER NOT NULL, identity TEXT NOT NULL, state TEXT NOT NULL, updated TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    internal LibraryState Load() => Read<LibraryState>("SELECT state FROM library WHERE id = 1") ?? new();
    internal CatalogCache LoadCache() => Read<CatalogCache>("SELECT data FROM index_cache WHERE id = 1") ?? new();

    internal void Save(LibraryState state) => Write(
        "INSERT INTO library VALUES (1, $value) ON CONFLICT(id) DO UPDATE SET state = excluded.state", state);

    internal void SaveCache(CatalogCache state) => Write(
        "INSERT INTO index_cache VALUES (1, $value) ON CONFLICT(id) DO UPDATE SET data = excluded.data", state);

    internal List<(string Path, string Boot, int Session, string Identity, string State)> ReadRegistrations()
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, boot, session, identity, state FROM registrations";
        using var reader = command.ExecuteReader();
        var result = new List<(string, string, int, string, string)>();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetString(3), reader.GetString(4)));
        return result;
    }

    internal void LogRegistration(string path, string boot, int session, string identity, string state)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO registrations VALUES ($path, $boot, $session, 0, $identity, $state, $updated)
            ON CONFLICT(path) DO UPDATE SET boot=excluded.boot, session=excluded.session, identity=excluded.identity,
            state=excluded.state, updated=excluded.updated
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$boot", boot);
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    internal void ForgetRegistration(string path)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM registrations WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    internal void Diagnostic(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(dataDirectory, "diagnostics.log"),
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("{0} ({1})", message, error.Message);
        }
    }

    private T? Read<T>(string sql)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }

    private void Write<T>(string sql, T state)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
