using Xunit;
using SurroundVisionPlayer.Logic;

namespace SurroundVisionPlayer.Tests;

public class BookmarkManagerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public BookmarkManagerTests() => Directory.CreateDirectory(_tmp);
    public void Dispose()         => Directory.Delete(_tmp, recursive: true);

    // ── BookmarkFile ──────────────────────────────────────────────────────────

    [Fact]
    public void BookmarkFile_ReturnsExpectedPath()
        => Assert.Equal(Path.Combine(_tmp, "bookmarks.json"), BookmarkManager.BookmarkFile(_tmp));

    // ── Load ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
        => Assert.Empty(BookmarkManager.Load(_tmp));

    [Fact]
    public void Load_InvalidJson_ReturnsEmpty()
    {
        File.WriteAllText(BookmarkManager.BookmarkFile(_tmp), "not json }{");
        Assert.Empty(BookmarkManager.Load(_tmp));
    }

    // ── Save / Load round-trip ────────────────────────────────────────────────

    [Fact]
    public void SaveLoad_RoundTrip_PreservesBookmarks()
    {
        var bookmarks = new List<Bookmark>
        {
            new("2026_04_16_T_15_18_19", 30_000, "lane merge"),
            new("2026_04_16_T_15_18_19", 90_000, "near miss"),
        };
        BookmarkManager.Save(_tmp, bookmarks);
        var loaded = BookmarkManager.Load(_tmp);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("lane merge", loaded[0].Note);
        Assert.Equal(90_000, loaded[1].SessionMs);
    }

    [Fact]
    public void Save_CreatesFile()
    {
        BookmarkManager.Save(_tmp, []);
        Assert.True(File.Exists(BookmarkManager.BookmarkFile(_tmp)));
    }

    [Fact]
    public void SaveLoad_EmptyNote_PreservesEmptyNote()
    {
        BookmarkManager.Save(_tmp, [new Bookmark("ts", 0, "")]);
        var loaded = BookmarkManager.Load(_tmp);
        Assert.Equal("", loaded[0].Note);
    }

    // ── ForSession ────────────────────────────────────────────────────────────

    [Fact]
    public void ForSession_FiltersToMatchingFirstTs()
    {
        var all = new List<Bookmark>
        {
            new("ts_A", 1000, "a"),
            new("ts_B", 2000, "b"),
            new("ts_A", 3000, "c"),
        };
        var result = BookmarkManager.ForSession(all, "ts_A");
        Assert.Equal(2, result.Count);
        Assert.All(result, b => Assert.Equal("ts_A", b.SessionFirstTs));
    }

    [Fact]
    public void ForSession_SortsBySessionMs()
    {
        var all = new List<Bookmark>
        {
            new("ts", 5000, "later"),
            new("ts", 1000, "earlier"),
        };
        var result = BookmarkManager.ForSession(all, "ts");
        Assert.Equal(1000, result[0].SessionMs);
        Assert.Equal(5000, result[1].SessionMs);
    }

    [Fact]
    public void ForSession_NoMatch_ReturnsEmpty()
    {
        var all = new List<Bookmark> { new("ts_A", 1000, "x") };
        Assert.Empty(BookmarkManager.ForSession(all, "ts_Z"));
    }

    // ── PrevBookmark ──────────────────────────────────────────────────────────

    [Fact]
    public void PrevBookmark_ReturnsClosestBefore()
    {
        var all = new List<Bookmark>
        {
            new("ts", 1000, "a"),
            new("ts", 3000, "b"),
            new("ts", 7000, "c"),
        };
        var result = BookmarkManager.PrevBookmark(all, "ts", 6000);
        Assert.Equal(3000, result?.SessionMs);
    }

    [Fact]
    public void PrevBookmark_NoneBeforeCurrent_ReturnsNull()
    {
        var all = new List<Bookmark> { new("ts", 5000, "x") };
        Assert.Null(BookmarkManager.PrevBookmark(all, "ts", 5100));
    }

    [Fact]
    public void PrevBookmark_EmptyList_ReturnsNull()
        => Assert.Null(BookmarkManager.PrevBookmark([], "ts", 10000));

    // ── NextBookmark ──────────────────────────────────────────────────────────

    [Fact]
    public void NextBookmark_ReturnsClosestAfter()
    {
        var all = new List<Bookmark>
        {
            new("ts", 1000, "a"),
            new("ts", 3000, "b"),
            new("ts", 7000, "c"),
        };
        var result = BookmarkManager.NextBookmark(all, "ts", 2000);
        Assert.Equal(3000, result?.SessionMs);
    }

    [Fact]
    public void NextBookmark_NoneAfterCurrent_ReturnsNull()
    {
        var all = new List<Bookmark> { new("ts", 5000, "x") };
        Assert.Null(BookmarkManager.NextBookmark(all, "ts", 4900));
    }

    [Fact]
    public void NextBookmark_EmptyList_ReturnsNull()
        => Assert.Null(BookmarkManager.NextBookmark([], "ts", 0));
}
