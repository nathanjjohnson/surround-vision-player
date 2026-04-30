using System.IO;
using System.Text.Json;

namespace SurroundVisionPlayer.Logic;

public sealed record Bookmark(string SessionFirstTs, long SessionMs, string Note);

public static class BookmarkManager
{
    public static string BookmarkFile(string archiveRoot)
        => Path.Combine(archiveRoot, "bookmarks.json");

    public static List<Bookmark> Load(string archiveRoot)
    {
        var path = BookmarkFile(archiveRoot);
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<Bookmark>>(json) ?? [];
        }
        catch { return []; }
    }

    public static void Save(string archiveRoot, List<Bookmark> bookmarks)
    {
        var path = BookmarkFile(archiveRoot);
        File.WriteAllText(path, JsonSerializer.Serialize(bookmarks,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<Bookmark> ForSession(List<Bookmark> all, string firstTs)
        => [.. all.Where(b => b.SessionFirstTs == firstTs).OrderBy(b => b.SessionMs)];

    public static Bookmark? PrevBookmark(List<Bookmark> all, string firstTs, long currentMs, long toleranceMs = 500)
        => ForSession(all, firstTs).LastOrDefault(b => b.SessionMs < currentMs - toleranceMs);

    public static Bookmark? NextBookmark(List<Bookmark> all, string firstTs, long currentMs, long toleranceMs = 500)
        => ForSession(all, firstTs).FirstOrDefault(b => b.SessionMs > currentMs + toleranceMs);
}
