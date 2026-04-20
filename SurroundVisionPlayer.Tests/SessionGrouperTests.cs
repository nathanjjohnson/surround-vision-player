using Xunit;
using SurroundVisionPlayer.Logic;

namespace SurroundVisionPlayer.Tests;

public class SessionGrouperTests
{
    // Real timestamps from the thumb drive
    private static readonly List<string> Apr16 =
    [
        "2026_04_16_T_15_18_19",   // session 1 start
        "2026_04_16_T_15_23_42",   // +323 s
        "2026_04_16_T_15_28_42",   // +300 s
        "2026_04_16_T_15_33_42",   // +300 s
        "2026_04_16_T_15_38_42",   // +300 s
        "2026_04_16_T_15_43_42",   // +300 s
        "2026_04_16_T_15_48_42",   // +300 s
        "2026_04_16_T_15_53_42",   // +300 s
        "2026_04_16_T_15_58_42",   // +300 s
        "2026_04_16_T_16_03_42",   // +300 s
        "2026_04_16_T_16_08_42",   // +300 s
    ];
    private static readonly List<string> Apr17Am =
    [
        "2026_04_17_T_07_11_45",
        "2026_04_17_T_07_16_45",   // +300 s
        "2026_04_17_T_07_21_45",
        "2026_04_17_T_07_26_45",
        "2026_04_17_T_07_31_45",
        "2026_04_17_T_07_36_45",
        "2026_04_17_T_07_41_45",
        "2026_04_17_T_07_46_45",
    ];
    private static readonly List<string> Apr17Pm =
    [
        "2026_04_17_T_13_13_40",
        "2026_04_17_T_13_18_40",   // +300 s
        "2026_04_17_T_13_23_40",
        "2026_04_17_T_13_28_40",
        "2026_04_17_T_13_33_40",
        "2026_04_17_T_13_38_40",
        "2026_04_17_T_13_43_40",
        "2026_04_17_T_13_48_40",
        "2026_04_17_T_13_52_13",   // +213 s (short final clip)
    ];
    private static readonly List<string> All = [.. Apr16, .. Apr17Am, .. Apr17Pm];

    // ── ParseTimestamp ────────────────────────────────────────────────────────

    [Fact]
    public void ParseTimestamp_ReturnsCorrectDateTime()
    {
        var dt = SessionGrouper.ParseTimestamp("2026_04_16_T_15_18_19");
        Assert.Equal(new DateTime(2026, 4, 16, 15, 18, 19), dt);
    }

    [Fact]
    public void ParseTimestamp_Midnight()
    {
        var dt = SessionGrouper.ParseTimestamp("2026_01_01_T_00_00_00");
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0), dt);
    }

    [Fact]
    public void ParseTimestamp_InvalidThrows()
        => Assert.Throws<FormatException>(() => SessionGrouper.ParseTimestamp("bad"));

    // ── GapSeconds ────────────────────────────────────────────────────────────

    [Fact]
    public void GapSeconds_ExactlyFiveMinutes()
        => Assert.Equal(300.0,
            SessionGrouper.GapSeconds("2026_04_16_T_15_18_42", "2026_04_16_T_15_23_42"),
            precision: 1);

    [Fact]
    public void GapSeconds_ZeroGap()
        => Assert.Equal(0.0,
            SessionGrouper.GapSeconds("2026_04_16_T_15_18_19", "2026_04_16_T_15_18_19"),
            precision: 1);

    [Fact]
    public void GapSeconds_AcrossHourBoundary()
        => Assert.Equal(300.0,
            SessionGrouper.GapSeconds("2026_04_16_T_15_58_42", "2026_04_16_T_16_03_42"),
            precision: 1);

    [Fact]
    public void GapSeconds_LargeInterSessionGap()
    {
        var g = SessionGrouper.GapSeconds("2026_04_16_T_16_08_42", "2026_04_17_T_07_11_45");
        Assert.True(g > 3600, $"Expected > 3600 s, got {g}");
    }

    // ── Group ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Group_EmptyInput_ReturnsEmpty()
        => Assert.Empty(SessionGrouper.Group([]));

    [Fact]
    public void Group_SingleTimestamp_ReturnsSingleSession()
    {
        var result = SessionGrouper.Group(["2026_04_16_T_15_18_19"]);
        Assert.Single(result);
        Assert.Single(result[0]);
    }

    [Fact]
    public void Group_FullDrive_ReturnsThreeSessions()
        => Assert.Equal(3, SessionGrouper.Group(All).Count);

    [Fact]
    public void Group_SessionSizesMatchActualData()
    {
        var sizes = SessionGrouper.Group(All).Select(s => s.Count).OrderBy(x => x).ToList();
        Assert.Equal([8, 9, 11], sizes);
    }

    [Fact]
    public void Group_323sGapStaysInSameSession()
    {
        // First gap on Apr 16 is 323 s, which is within the 375 s threshold
        var sessions = SessionGrouper.Group(All[..2]);
        Assert.Single(sessions);
    }

    [Fact]
    public void Group_ExactFiveMinGapStaysInSession()
    {
        var ts = new List<string>
        {
            "2026_04_16_T_15_23_42",
            "2026_04_16_T_15_28_42"   // exactly 300 s
        };
        Assert.Single(SessionGrouper.Group(ts));
    }

    [Fact]
    public void Group_LargeGapSplitsSessions()
    {
        var ts = new List<string>
        {
            "2026_04_16_T_16_08_42",
            "2026_04_17_T_07_11_45"   // ~57 000 s gap
        };
        Assert.Equal(2, SessionGrouper.Group(ts).Count);
    }

    [Fact]
    public void Group_ShortFinalClipStaysInSession()
    {
        // 13:48:40 → 13:52:13 = 213 s — still within the same session
        var ts = new List<string>
        {
            "2026_04_17_T_13_48_40",
            "2026_04_17_T_13_52_13"
        };
        Assert.Single(SessionGrouper.Group(ts));
    }

    [Fact]
    public void Group_CustomThresholdSplitsEarlier()
    {
        // With a 200 s threshold the 323 s first gap should split (300 s second gap also splits)
        var sessions = SessionGrouper.Group(All[..2], gapThreshold: 200);
        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void Group_SessionOrderIsPreserved()
    {
        var sessions = SessionGrouper.Group(All);
        Assert.Equal("2026_04_16_T_15_18_19", sessions[0][0]);
        Assert.Equal("2026_04_17_T_07_11_45", sessions[1][0]);
        Assert.Equal("2026_04_17_T_13_13_40", sessions[2][0]);
    }

    [Fact]
    public void Group_ClipsWithinSessionAreOrdered()
    {
        foreach (var session in SessionGrouper.Group(All))
            Assert.Equal([.. session.OrderBy(x => x)], session);
    }

    [Fact]
    public void Group_WithinSessionGapsAllBelowThreshold()
    {
        foreach (var session in SessionGrouper.Group(All))
            for (int i = 1; i < session.Count; i++)
            {
                var gap = SessionGrouper.GapSeconds(session[i - 1], session[i]);
                Assert.True(gap <= SessionGrouper.GapThresholdSeconds,
                    $"Within-session gap {session[i-1]} → {session[i]} = {gap:F0} s");
            }
    }

    [Fact]
    public void Group_BetweenSessionGapsAllAboveThreshold()
    {
        var sessions = SessionGrouper.Group(All);
        for (int i = 1; i < sessions.Count; i++)
        {
            var gap = SessionGrouper.GapSeconds(sessions[i - 1][^1], sessions[i][0]);
            Assert.True(gap > SessionGrouper.GapThresholdSeconds,
                $"Expected session break, gap was only {gap:F0} s");
        }
    }

    // ── DurationSeconds ───────────────────────────────────────────────────────

    [Fact]
    public void DurationSeconds_SingleClip_Returns300()
        => Assert.Equal(300, SessionGrouper.DurationSeconds(["2026_04_16_T_15_18_19"]));

    [Fact]
    public void DurationSeconds_TwoClips300sApart_Returns600()
    {
        var dur = SessionGrouper.DurationSeconds(
        [
            "2026_04_16_T_15_23_42",
            "2026_04_16_T_15_28_42"
        ]);
        Assert.Equal(600, dur);
    }

    [Fact]
    public void DurationSeconds_ElevenClipSession_AboutFiftyFiveMin()
    {
        var dur = SessionGrouper.DurationSeconds(Apr16);
        // Span 15:18:19 → 16:08:42 = 3023 s, + 300 = 3323 s (~55 min)
        Assert.InRange(dur, 3200, 3400);
    }

    // ── Label ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Label_ContainsStartTime()
        => Assert.Contains("15:18", SessionGrouper.Label(Apr16));

    [Fact]
    public void Label_ContainsEndTime()
        => Assert.Contains("16:08", SessionGrouper.Label(Apr16));

    [Fact]
    public void Label_ContainsClipsPlural()
        => Assert.Contains("11 clips", SessionGrouper.Label(Apr16));

    [Fact]
    public void Label_SingleClipUsesSingular()
        => Assert.Contains("1 clip", SessionGrouper.Label(["2026_04_16_T_15_18_19"]));

    [Fact]
    public void Label_SingleClipDoesNotUsePlural()
        => Assert.DoesNotContain("1 clips", SessionGrouper.Label(["2026_04_16_T_15_18_19"]));

    [Fact]
    public void Label_ContainsMinutes()
        => Assert.Contains("min", SessionGrouper.Label(Apr16));
}
