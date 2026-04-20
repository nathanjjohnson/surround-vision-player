using Xunit;
using SurroundVisionPlayer.Logic;

namespace SurroundVisionPlayer.Tests;

public class RecordingScannerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public RecordingScannerTests() => Directory.CreateDirectory(_tmp);
    public void Dispose()          => Directory.Delete(_tmp, recursive: true);

    private void Touch(string name) => File.WriteAllBytes(Path.Combine(_tmp, name), []);

    // ── LooksLikeSvr ──────────────────────────────────────────────────────────

    [Fact]
    public void LooksLikeSvr_EmptyFolder_ReturnsFalse()
        => Assert.False(RecordingScanner.LooksLikeSvr(_tmp));

    [Fact]
    public void LooksLikeSvr_WithDashcamFile_ReturnsTrue()
    {
        Touch("FRONT_2026_04_16_T_15_18_19.mp4");
        Assert.True(RecordingScanner.LooksLikeSvr(_tmp));
    }

    [Fact]
    public void LooksLikeSvr_OnlyNonMatchingFiles_ReturnsFalse()
    {
        Touch("README.txt");
        Touch("video.avi");
        Touch("SIDE_2026_04_16_T_15_18_19.mp4");   // unknown angle
        Assert.False(RecordingScanner.LooksLikeSvr(_tmp));
    }

    [Fact]
    public void LooksLikeSvr_NonexistentPath_ReturnsFalse()
        => Assert.False(RecordingScanner.LooksLikeSvr(Path.Combine(_tmp, "nope")));

    // ── Scan ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_EmptyFolder_ReturnsEmpty()
        => Assert.Empty(RecordingScanner.Scan(_tmp));

    [Fact]
    public void Scan_NonexistentFolder_ReturnsEmpty()
        => Assert.Empty(RecordingScanner.Scan(Path.Combine(_tmp, "nope")));

    [Fact]
    public void Scan_GroupsByTimestamp()
    {
        foreach (var angle in RecordingScanner.Angles)
            Touch($"{angle}_2026_04_16_T_15_18_19.mp4");
        Touch("FRONT_2026_04_16_T_15_23_42.mp4");

        var result = RecordingScanner.Scan(_tmp);
        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("2026_04_16_T_15_18_19"));
        Assert.True(result.ContainsKey("2026_04_16_T_15_23_42"));
    }

    [Fact]
    public void Scan_AllFourAnglesInGroup()
    {
        foreach (var angle in RecordingScanner.Angles)
            Touch($"{angle}_2026_04_16_T_15_18_19.mp4");

        var result = RecordingScanner.Scan(_tmp);
        var angles = result["2026_04_16_T_15_18_19"];
        Assert.Equal(4, angles.Count);
        foreach (var a in RecordingScanner.Angles)
            Assert.True(angles.ContainsKey(a));
    }

    [Fact]
    public void Scan_PartialAnglesStillGrouped()
    {
        Touch("FRONT_2026_04_16_T_15_18_19.mp4");
        Touch("LEFT_2026_04_16_T_15_18_19.mp4");

        var result = RecordingScanner.Scan(_tmp);
        var angles = result["2026_04_16_T_15_18_19"];
        Assert.Equal(2, angles.Count);
        Assert.True(angles.ContainsKey("FRONT"));
        Assert.True(angles.ContainsKey("LEFT"));
    }

    [Fact]
    public void Scan_IgnoresNonMatchingFiles()
    {
        Touch("README.txt");
        Touch("SIDE_2026_04_16_T_15_18_19.mp4");
        Assert.Empty(RecordingScanner.Scan(_tmp));
    }

    [Fact]
    public void Scan_ResultIsSortedAscending()
    {
        Touch("FRONT_2026_04_17_T_07_11_45.mp4");
        Touch("FRONT_2026_04_16_T_15_18_19.mp4");
        Touch("FRONT_2026_04_17_T_13_13_40.mp4");

        var keys = RecordingScanner.Scan(_tmp).Keys.ToList();
        Assert.Equal([.. keys.OrderBy(k => k)], keys);
    }

    [Fact]
    public void Scan_PathsAreCorrect()
    {
        var name = "FRONT_2026_04_16_T_15_18_19.mp4";
        Touch(name);
        var result = RecordingScanner.Scan(_tmp);
        Assert.Equal(Path.Combine(_tmp, name), result["2026_04_16_T_15_18_19"]["FRONT"]);
    }

    // ── FindSvrFolder ─────────────────────────────────────────────────────────

    [Fact]
    public void FindSvrFolder_GivenSvrDirectly_ReturnsIt()
    {
        Touch("FRONT_2026_04_16_T_15_18_19.mp4");
        Assert.Equal(_tmp, RecordingScanner.FindSvrFolder(_tmp));
    }

    [Fact]
    public void FindSvrFolder_GivenDriveRoot_FindsSubPath()
    {
        var svr = Path.Combine(_tmp, RecordingScanner.SvrSubPath);
        Directory.CreateDirectory(svr);
        File.WriteAllBytes(Path.Combine(svr, "FRONT_2026_04_16_T_15_18_19.mp4"), []);
        Assert.Equal(svr, RecordingScanner.FindSvrFolder(_tmp));
    }

    [Fact]
    public void FindSvrFolder_NothingFound_ReturnsNull()
        => Assert.Null(RecordingScanner.FindSvrFolder(_tmp));
}
