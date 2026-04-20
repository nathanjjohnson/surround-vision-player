using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SurroundVisionPlayer.Logic;

namespace SurroundVisionPlayer;

public partial class MainWindow : Window
{
    // ── Source data ───────────────────────────────────────────────────────────

    private SortedDictionary<string, Dictionary<string, string>> _driveRecordings   = [];
    private List<List<string>>                                    _driveSessions     = [];
    private SortedDictionary<string, Dictionary<string, string>> _archiveRecordings = [];
    private List<List<string>>                                    _archiveSessions   = [];

    // Points to whichever source is currently being played
    private SortedDictionary<string, Dictionary<string, string>> _recordings = [];
    private List<List<string>>                                    _sessions   = [];

    // ── Playback state ────────────────────────────────────────────────────────

    private string?  _currentTs;
    private int      _currentSession = -1;
    private string   _activeAngle    = "FRONT";
    private bool     _isPlaying;
    private double   _playbackRate   = 1.0;
    private bool     _sliderDragging;
    private bool     _suppressSlider;
    private int      _pendingOpens;
    private bool     _suppressTreeChange;

    // ── Session-level timeline ────────────────────────────────────────────────

    private long   _sessionOffsetMs;
    private long   _sessionTotalMs;
    private long[] _clipOffsetsMs = [];
    private long?  _pendingSeekMs;

    // ── Infrastructure ────────────────────────────────────────────────────────

    private readonly Dictionary<string, MediaElement>  _videos;
    private readonly Dictionary<string, ToggleButton>  _angleBtns;
    private readonly DispatcherTimer                   _syncTimer;
    private readonly AppSettings                       _settings;

    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly TimeSpan StepSize      = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SyncThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TickInterval  = TimeSpan.FromMilliseconds(200);

    // ═════════════════════════════════════════════════════════════════════════
    // Construction
    // ═════════════════════════════════════════════════════════════════════════

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();

        _videos = new Dictionary<string, MediaElement>
        {
            ["FRONT"] = VideoFront,
            ["LEFT"]  = VideoLeft,
            ["REAR"]  = VideoRear,
            ["RIGHT"] = VideoRight,
        };
        _angleBtns = new Dictionary<string, ToggleButton>
        {
            ["FRONT"] = BtnFront,
            ["LEFT"]  = BtnLeft,
            ["REAR"]  = BtnRear,
            ["RIGHT"] = BtnRight,
        };

        _syncTimer = new DispatcherTimer { Interval = TickInterval };
        _syncTimer.Tick += SyncTimer_Tick;
        _syncTimer.Start();

        StatusLabel.Text = "Open a thumb drive via File → Open Thumb Drive…";
        LoadArchive();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Source management
    // ═════════════════════════════════════════════════════════════════════════

    private void UseSource(
        SortedDictionary<string, Dictionary<string, string>> recordings,
        List<List<string>> sessions)
    {
        _recordings = recordings;
        _sessions   = sessions;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Folder / data loading
    // ═════════════════════════════════════════════════════════════════════════

    private void LoadFolder(string svrFolder)
    {
        StopAll();
        _driveRecordings = RecordingScanner.Scan(svrFolder);
        List<string> timestamps = [.. _driveRecordings.Keys];
        _driveSessions   = SessionGrouper.Group(timestamps);

        PopulateTree(DriveTree, _driveSessions, _driveRecordings);
        UseSource(_driveRecordings, _driveSessions);
        UpdateCountLabel();

        if (_driveSessions.Count > 0)
            SelectFirstTrip(DriveTree);
        else
            StatusLabel.Text = $"No recordings found in: {svrFolder}";
    }

    private void LoadArchive()
    {
        _archiveRecordings = [];
        _archiveSessions   = [];

        if (!string.IsNullOrEmpty(_settings.ArchiveFolder)
            && Directory.Exists(_settings.ArchiveFolder))
        {
            _archiveRecordings = Archiver.ScanArchive(_settings.ArchiveFolder);
            _archiveSessions   = SessionGrouper.Group([.. _archiveRecordings.Keys]);
        }

        PopulateTree(ArchiveTree, _archiveSessions, _archiveRecordings);

        if (SourceTabs.SelectedIndex == 1)
            UpdateCountLabel();
    }

    private void PopulateTree(
        TreeView tree,
        List<List<string>> sessions,
        SortedDictionary<string, Dictionary<string, string>> recordings)
    {
        _suppressTreeChange = true;
        tree.Items.Clear();

        var dateStyle = (Style)FindResource("DateNodeStyle");
        string? prevDate = null;
        TreeViewItem? dateNode = null;

        foreach (var session in sessions)
        {
            var dt      = SessionGrouper.ParseTimestamp(session[0]);
            var dateStr = dt.ToString("yyyy-MM-dd");

            if (dateStr != prevDate)
            {
                prevDate = dateStr;
                dateNode = new TreeViewItem
                {
                    Header     = dateStr,
                    IsExpanded = true,
                    Style      = dateStyle,
                };
                tree.Items.Add(dateNode);
            }

            var item = new SessionListItem
            {
                Label        = SessionGrouper.Label(session),
                IsHeader     = false,
                SessionIndex = sessions.IndexOf(session),
                FirstTs      = session[0],
                Session      = session,
            };

            dateNode!.Items.Add(new TreeViewItem
            {
                Header  = item.Label,
                Tag     = item,
                Padding = new Thickness(4, 3, 4, 3),
            });
        }

        if (tree.Items.Count == 0)
        {
            var hint = new TreeViewItem
            {
                Header    = tree == ArchiveTree && string.IsNullOrEmpty(_settings.ArchiveFolder)
                            ? "No archive folder set.\nUse File → Set Archive Folder…"
                            : "No recordings found.",
                Focusable = false,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            };
            tree.Items.Add(hint);
        }

        _suppressTreeChange = false;
    }

    private static void SelectFirstTrip(TreeView tree)
    {
        if (tree.Items.Count > 0
            && tree.Items[0] is TreeViewItem dateNode
            && dateNode.Items.Count > 0
            && dateNode.Items[0] is TreeViewItem firstTrip)
        {
            firstTrip.IsSelected = true;
        }
    }

    private void UpdateCountLabel()
    {
        if (SourceTabs.SelectedIndex == 0)
        {
            int n = _driveSessions.Count, c = _driveRecordings.Count;
            CountLabel.Text = n > 0 ? $"{n} trip(s), {c} clips" : string.Empty;
        }
        else
        {
            if (string.IsNullOrEmpty(_settings.ArchiveFolder))
                CountLabel.Text = "No archive folder configured";
            else
            {
                int n = _archiveSessions.Count, c = _archiveRecordings.Count;
                CountLabel.Text = $"{n} trip(s), {c} clips archived";
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Session-level timeline
    // ═════════════════════════════════════════════════════════════════════════

    private async void InitSessionTimeline(int sessionIdx)
    {
        var session    = _sessions[sessionIdx];
        var recordings = _recordings; // capture before any await

        _clipOffsetsMs  = ComputeClipOffsets(session);
        _sessionTotalMs = _clipOffsetsMs[^1] + 300_000; // estimate

        // Probe actual last-clip duration and refine the total
        long lastDur = await ProbeLastClipDurationAsync(session, recordings);

        if (_currentSession == sessionIdx && ReferenceEquals(_recordings, recordings))
        {
            _sessionTotalMs = _clipOffsetsMs[^1] + lastDur;
            if (!_sliderDragging)
                DurLabel.Text = FormatTime(TimeSpan.FromMilliseconds(_sessionTotalMs));
        }
    }

    private static long[] ComputeClipOffsets(List<string> session)
    {
        var offsets = new long[session.Count];
        long acc = 0;
        for (int i = 0; i < session.Count; i++)
        {
            offsets[i] = acc;
            if (i + 1 < session.Count)
                acc += (long)(SessionGrouper.GapSeconds(session[i], session[i + 1]) * 1000);
            else
                acc += 300_000;
        }
        return offsets;
    }

    private static Task<long> ProbeLastClipDurationAsync(
        List<string> session,
        SortedDictionary<string, Dictionary<string, string>> recordings)
    {
        if (session.Count == 0) return Task.FromResult(300_000L);

        if (!recordings.TryGetValue(session[^1], out var files) || files.Count == 0)
            return Task.FromResult(300_000L);

        var path = files.Values.First();
        var tcs  = new TaskCompletionSource<long>();
        var mp   = new System.Windows.Media.MediaPlayer();

        mp.MediaOpened += (_, _) =>
        {
            var dur = mp.NaturalDuration;
            tcs.TrySetResult(dur.HasTimeSpan ? (long)dur.TimeSpan.TotalMilliseconds : 300_000L);
            mp.Close();
        };
        mp.MediaFailed += (_, _) =>
        {
            tcs.TrySetResult(300_000L);
        };
        mp.Open(new Uri(path, UriKind.Absolute));

        return tcs.Task;
    }

    private int FindClipIndex(long sessionMs)
    {
        for (int i = _clipOffsetsMs.Length - 1; i >= 0; i--)
            if (sessionMs >= _clipOffsetsMs[i])
                return i;
        return 0;
    }

    private void SeekToSessionMs(long sessionMs)
    {
        if (_currentSession < 0 || _clipOffsetsMs.Length == 0) return;
        sessionMs = Math.Clamp(sessionMs, 0, _sessionTotalMs - 1);

        int clipIdx = FindClipIndex(sessionMs);
        var session = _sessions[_currentSession];
        long clipMs = sessionMs - _clipOffsetsMs[clipIdx];

        if (session[clipIdx] == _currentTs)
        {
            SeekAll(TimeSpan.FromMilliseconds(clipMs));
        }
        else
        {
            bool wasPlaying = _isPlaying;
            _pendingSeekMs  = clipMs;
            LoadRecording(session[clipIdx], _currentSession);
            if (wasPlaying) { _isPlaying = true; BtnPlay.Content = "⏸"; }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Recording loading
    // ═════════════════════════════════════════════════════════════════════════

    private void LoadRecording(string ts, int sessionIdx)
    {
        StopAll();
        _currentTs      = ts;
        _currentSession = sessionIdx;
        _pendingOpens   = 0;

        var session = _sessions[sessionIdx];
        int clipIdx = session.IndexOf(ts);
        _sessionOffsetMs = clipIdx >= 0 && clipIdx < _clipOffsetsMs.Length
            ? _clipOffsetsMs[clipIdx] : 0;

        var files = _recordings[ts];
        foreach (var (angle, me) in _videos)
        {
            if (files.TryGetValue(angle, out var path))
            {
                _pendingOpens++;
                me.Source = new Uri(path);
            }
            else
            {
                me.Source = null;
            }
        }

        ResetSlider();
        UpdateStatus();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Playback control
    // ═════════════════════════════════════════════════════════════════════════

    private void PlayAll()
    {
        foreach (var me in _videos.Values)
            if (me.Source is not null)
                me.Play();
        _isPlaying = true;
        BtnPlay.Content = "⏸";
    }

    private void PauseAll()
    {
        foreach (var me in _videos.Values)
            me.Pause();
        _isPlaying = false;
        BtnPlay.Content = "▶";
    }

    private void StopAll()
    {
        foreach (var me in _videos.Values)
            me.Stop();
        _isPlaying = false;
        BtnPlay.Content = "▶";
    }

    private void SeekAll(TimeSpan pos)
    {
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        foreach (var me in _videos.Values)
            if (me.Source is not null)
                me.Position = pos;
    }

    private void SetRate(double rate)
    {
        _playbackRate = rate;
        foreach (var me in _videos.Values)
            me.SpeedRatio = rate;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Angle switching
    // ═════════════════════════════════════════════════════════════════════════

    private void SwitchAngle(string angle)
    {
        if (angle == _activeAngle) return;
        var refPos = _videos[_activeAngle].Position;

        _activeAngle = angle;
        AngleLabel.Text = angle;

        foreach (var (a, me) in _videos)
        {
            me.Visibility = a == angle ? Visibility.Visible : Visibility.Hidden;
            me.IsMuted    = a != angle;
        }
        foreach (var (a, btn) in _angleBtns)
            btn.IsChecked = a == angle;

        if (refPos > TimeSpan.Zero && _videos[angle].Source is not null)
            _videos[angle].Position = refPos;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Auto-advance
    // ═════════════════════════════════════════════════════════════════════════

    private void AdvanceToNextClip()
    {
        if (_currentTs is null || _currentSession < 0) return;
        var session = _sessions[_currentSession];
        var idx     = session.IndexOf(_currentTs);
        if (idx < 0 || idx + 1 >= session.Count)
        {
            StopAll();
            StatusLabel.Text = "End of trip.";
            return;
        }
        _pendingSeekMs = null;
        LoadRecording(session[idx + 1], _currentSession);
        _isPlaying = true;
        BtnPlay.Content = "⏸";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Sync timer tick
    // ═════════════════════════════════════════════════════════════════════════

    private void SyncTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentTs is null) return;

        var active       = _videos[_activeAngle];
        var pos          = active.Position;
        var sessionPosMs = _sessionOffsetMs + (long)pos.TotalMilliseconds;

        if (!_sliderDragging && _sessionTotalMs > 0)
        {
            _suppressSlider = true;
            SeekSlider.Value = (double)sessionPosMs / _sessionTotalMs * 10000.0;
            _suppressSlider = false;
        }
        TimeLabel.Text = FormatTime(TimeSpan.FromMilliseconds(sessionPosMs));
        DurLabel.Text  = FormatTime(TimeSpan.FromMilliseconds(_sessionTotalMs));

        if (_isPlaying && pos > TimeSpan.Zero)
        {
            foreach (var (angle, me) in _videos)
            {
                if (angle == _activeAngle || me.Source is null) continue;
                if ((me.Position - pos).Duration() > SyncThreshold)
                    me.Position = pos;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // MediaElement events
    // ═════════════════════════════════════════════════════════════════════════

    private void Media_Opened(object sender, RoutedEventArgs e)
    {
        _pendingOpens = Math.Max(0, _pendingOpens - 1);

        if (sender is MediaElement me)
            me.SpeedRatio = _playbackRate;

        if (_pendingOpens == 0)
        {
            if (_pendingSeekMs.HasValue)
            {
                SeekAll(TimeSpan.FromMilliseconds(_pendingSeekMs.Value));
                _pendingSeekMs = null;
            }
            if (_isPlaying)
                PlayAll();
        }
    }

    private void Media_Ended(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement me && me == _videos[_activeAngle])
            AdvanceToNextClip();
    }

    private void Media_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        _pendingSeekMs = null;
        StatusLabel.Text = $"Media error: {e.ErrorException?.Message}";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UI event handlers — tree selection
    // ═════════════════════════════════════════════════════════════════════════

    private void SourceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl) return;
        BtnArchive.IsEnabled = false;
        UpdateCountLabel();
    }

    private void DriveTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeChange) return;

        // Deselect date-group nodes immediately
        if (e.NewValue is TreeViewItem tvi && tvi.Tag is not SessionListItem)
        {
            tvi.IsSelected = false;
            return;
        }

        if (e.NewValue is not TreeViewItem tv || tv.Tag is not SessionListItem item) return;

        BtnArchive.IsEnabled = true;
        if (item.FirstTs == _currentTs && item.SessionIndex == _currentSession) return;

        bool wasPlaying = _isPlaying;
        UseSource(_driveRecordings, _driveSessions);
        InitSessionTimeline(item.SessionIndex);
        LoadRecording(item.FirstTs, item.SessionIndex);
        if (wasPlaying) PlayAll();
    }

    private void ArchiveTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeChange) return;

        if (e.NewValue is TreeViewItem tvi && tvi.Tag is not SessionListItem)
        {
            tvi.IsSelected = false;
            return;
        }

        if (e.NewValue is not TreeViewItem tv || tv.Tag is not SessionListItem item) return;
        if (item.FirstTs == _currentTs && item.SessionIndex == _currentSession) return;

        bool wasPlaying = _isPlaying;
        UseSource(_archiveRecordings, _archiveSessions);
        InitSessionTimeline(item.SessionIndex);
        LoadRecording(item.FirstTs, item.SessionIndex);
        if (wasPlaying) PlayAll();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UI event handlers — transport
    // ═════════════════════════════════════════════════════════════════════════

    private void Angle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is string angle)
        {
            if (angle == _activeAngle) { btn.IsChecked = true; return; }
            SwitchAngle(angle);
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) PauseAll(); else PlayAll();
    }

    private void Beginning_Click(object sender, RoutedEventArgs e) => SeekToSessionMs(0);

    private void End_Click(object sender, RoutedEventArgs e)
    {
        var dur = _videos[_activeAngle].NaturalDuration;
        if (dur.HasTimeSpan)
            SeekAll(dur.TimeSpan - TimeSpan.FromMilliseconds(200));
    }

    private void StepBack_Click(object sender, RoutedEventArgs e)
    {
        long target = _sessionOffsetMs
                    + (long)_videos[_activeAngle].Position.TotalMilliseconds
                    - (long)StepSize.TotalMilliseconds;
        SeekToSessionMs(target);
    }

    private void StepFwd_Click(object sender, RoutedEventArgs e)
    {
        long target = _sessionOffsetMs
                    + (long)_videos[_activeAngle].Position.TotalMilliseconds
                    + (long)StepSize.TotalMilliseconds;
        SeekToSessionMs(target);
    }

    private void HalfSpeed_Click(object sender, RoutedEventArgs e)  => SetRate(0.5);
    private void NormalSpeed_Click(object sender, RoutedEventArgs e) => SetRate(1.0);
    private void DoubleSpeed_Click(object sender, RoutedEventArgs e) => SetRate(2.0);

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
        => _sliderDragging = true;

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _sliderDragging = false;
        if (_sessionTotalMs == 0) return;
        SeekToSessionMs((long)(SeekSlider.Value / 10000.0 * _sessionTotalMs));
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || !_sliderDragging || _sessionTotalMs == 0) return;
        SeekToSessionMs((long)(SeekSlider.Value / 10000.0 * _sessionTotalMs));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                if (_isPlaying) PauseAll(); else PlayAll();
                e.Handled = true; break;
            case Key.Left:
                StepBack_Click(sender, new RoutedEventArgs());
                e.Handled = true; break;
            case Key.Right:
                StepFwd_Click(sender, new RoutedEventArgs());
                e.Handled = true; break;
            case Key.F: SwitchAngle("FRONT"); e.Handled = true; break;
            case Key.L: SwitchAngle("LEFT");  e.Handled = true; break;
            case Key.R: SwitchAngle("REAR");  e.Handled = true; break;
            case Key.G: SwitchAngle("RIGHT"); e.Handled = true; break;
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _syncTimer.Stop();
        StopAll();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Archive
    // ═════════════════════════════════════════════════════════════════════════

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (DriveTree.SelectedItem is not TreeViewItem tvi
            || tvi.Tag is not SessionListItem item
            || item.Session is null)
            return;

        if (string.IsNullOrEmpty(_settings.ArchiveFolder) && !PickArchiveFolder())
            return;

        BtnArchive.IsEnabled         = false;
        var session     = item.Session;
        var archiveRoot = _settings.ArchiveFolder!;
        var recordings  = _driveRecordings;

        int totalFiles = Archiver.CountFiles(session, recordings);
        ArchiveProgress.Maximum    = totalFiles;
        ArchiveProgress.Value      = 0;
        ArchiveProgress.Visibility = Visibility.Visible;
        StatusLabel.Text           = $"Archiving {session.Count} clip(s) — 0 / {totalFiles} files…";

        var progress = new Progress<int>(n =>
        {
            ArchiveProgress.Value = n;
            StatusLabel.Text      = $"Archiving — {n} / {totalFiles} files…";
        });

        try
        {
            int copied = await Task.Run(() =>
                Archiver.Archive(session, recordings, archiveRoot, progress));

            StatusLabel.Text = copied > 0
                ? $"Archived {copied} new file(s) to {archiveRoot}"
                : "All files already present in archive.";

            LoadArchive();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Archive error: {ex.Message}";
        }
        finally
        {
            ArchiveProgress.Visibility = Visibility.Collapsed;
            BtnArchive.IsEnabled       = true;
        }
    }

    private void SetArchiveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickArchiveFolder()) LoadArchive();
    }

    private bool PickArchiveFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "Select Archive Root Folder",
            InitialDirectory = _settings.ArchiveFolder
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return false;
        _settings.ArchiveFolder = dlg.FolderName;
        _settings.Save();
        return true;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Drive picker
    // ═════════════════════════════════════════════════════════════════════════

    private void OpenDrive_Click(object sender, RoutedEventArgs e) => PromptForDrive();
    private void Quit_Click(object sender, RoutedEventArgs e)       => Close();

    private void PromptForDrive()
    {
        var dlg = new DrivePickerWindow
        {
            Owner       = this,
            InitialPath = _driveRecordings.Count > 0
                ? Path.GetDirectoryName(
                    Path.GetDirectoryName(
                        Path.GetDirectoryName(
                            _driveRecordings.Values.FirstOrDefault()
                                           ?.Values.FirstOrDefault())))
                : null,
        };

        if (dlg.ShowDialog() == true && dlg.SelectedFolder is not null)
            LoadFolder(dlg.SelectedFolder);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════════

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void ResetSlider()
    {
        _suppressSlider = true;
        SeekSlider.Value = _sessionTotalMs > 0
            ? (double)_sessionOffsetMs / _sessionTotalMs * 10000.0
            : 0;
        _suppressSlider = false;
        TimeLabel.Text = FormatTime(TimeSpan.FromMilliseconds(_sessionOffsetMs));
        DurLabel.Text  = FormatTime(TimeSpan.FromMilliseconds(_sessionTotalMs));
    }

    private void UpdateStatus()
    {
        if (_currentTs is null) return;
        var dt      = SessionGrouper.ParseTimestamp(_currentTs);
        var files   = _recordings[_currentTs];
        var have    = string.Join(", ", RecordingScanner.Angles.Where(a => files.ContainsKey(a)));
        var session = _currentSession >= 0 ? _sessions[_currentSession] : null;
        var clip    = session is not null
            ? $"Clip {session.IndexOf(_currentTs) + 1}/{session.Count}"
            : "";
        StatusLabel.Text = $"{dt:yyyy-MM-dd HH:mm:ss}  |  Angles: {have}  |  {clip}";
    }
}

// ── List data model ───────────────────────────────────────────────────────────

internal sealed class SessionListItem
{
    public required string       Label        { get; init; }
    public required bool         IsHeader     { get; init; }
    public required int          SessionIndex { get; init; }
    public required string       FirstTs      { get; init; }
    public List<string>?         Session      { get; init; }
}
