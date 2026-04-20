using System.IO;

namespace SurroundVisionPlayer.Logic;

public static class Archiver
{
    /// <summary>
    /// Copies all MP4 files for <paramref name="timestamps"/> into
    /// <c>&lt;archiveRoot&gt;\yyyy-MM-dd\</c> subdirectories.
    /// Skips files that already exist at the destination.
    /// Returns the number of newly copied files.
    /// </summary>
    /// <summary>Returns the total number of source files across all timestamps.</summary>
    public static int CountFiles(
        IReadOnlyList<string> timestamps,
        SortedDictionary<string, Dictionary<string, string>> recordings)
        => timestamps.Sum(ts => recordings.TryGetValue(ts, out var f) ? f.Count : 0);

    public static int Archive(
        IReadOnlyList<string> timestamps,
        SortedDictionary<string, Dictionary<string, string>> recordings,
        string archiveRoot,
        IProgress<int>? progress = null)  // reports running count of files processed
    {
        int copied = 0, processed = 0;
        foreach (var ts in timestamps)
        {
            if (!recordings.TryGetValue(ts, out var files)) continue;
            var dt      = SessionGrouper.ParseTimestamp(ts);
            var destDir = Path.Combine(archiveRoot, dt.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(destDir);

            foreach (var src in files.Values)
            {
                var dest = Path.Combine(destDir, Path.GetFileName(src));
                if (!File.Exists(dest))
                {
                    File.Copy(src, dest);
                    copied++;
                }
                progress?.Report(++processed);
            }
        }
        return copied;
    }

    /// <summary>
    /// Scans <paramref name="archiveRoot"/> one level deep (each subdirectory
    /// is a date folder containing flat MP4 files).
    /// </summary>
    public static SortedDictionary<string, Dictionary<string, string>> ScanArchive(
        string archiveRoot)
    {
        var result = new SortedDictionary<string, Dictionary<string, string>>(
            StringComparer.Ordinal);

        if (!Directory.Exists(archiveRoot)) return result;

        foreach (var dir in Directory.EnumerateDirectories(archiveRoot))
            foreach (var (ts, files) in RecordingScanner.Scan(dir))
                result[ts] = files;

        return result;
    }
}
