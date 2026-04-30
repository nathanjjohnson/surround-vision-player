using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Line = System.Windows.Shapes.Line;
using SurroundVisionPlayer.Logic;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace SurroundVisionPlayer;

public partial class MainWindow : Window
{
    // ── Source data ───────────────────────────────────────────────────────────

    private string?                                               _driveFolderPath;
    private SortedDictionary<string, Dictionary<string, string>> _driveRecordings   = [];
    private List<List<string>>                                    _driveSessions     = [];
    private SortedDictionary<string, Dictionary<string, string>> _archiveRecordings = [];
    private List<List<string>>                                    _archiveSessions   = [];
    private List<ClipEntry>                                       _clips             = [];
    private List<Bookmark>                                        _bookmarks         = [];

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
    private bool     _quadView;
    private bool     _bookmarkInputActive;

    // ── Clip mode & in/out state ──────────────────────────────────────────────

    private string? _clipModePath;
    private long    _inPointMs  = -1;
    private long    _outPointMs = -1;

    // ── Fullscreen state ──────────────────────────────────────────────────────

    private bool        _fullscreen;
    private WindowStyle _savedWindowStyle;
    private WindowState _savedWindowState;

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
    // USB auto-load
    // ═════════════════════════════════════════════════════════════════════════

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
    }

    private const int WmDeviceChange   = 0x0219;
    private const int DbtDeviceArrival = 0x8000;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange && wParam.ToInt32() == DbtDeviceArrival)
            Dispatcher.BeginInvoke(TryAutoLoadDrive);
        return IntPtr.Zero;
    }

    private void TryAutoLoadDrive()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable))
        {
            try { if (!drive.IsReady) continue; }
            catch { continue; }

            var svrPath = RecordingScanner.FindSvrFolder(drive.RootDirectory.FullName);
            if (svrPath is null) continue;
            if (_driveFolderPath == svrPath) return;

            LoadFolder(svrPath);
            SourceTabs.SelectedIndex = 0;
            StatusLabel.Text = $"Auto-loaded from {drive.Name}";
            return;
        }
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
        _driveFolderPath = svrFolder;
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
        LoadClips();
        LoadBookmarks();

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
        switch (SourceTabs.SelectedIndex)
        {
            case 0:
            {
                int n = _driveSessions.Count, c = _driveRecordings.Count;
                CountLabel.Text = n > 0 ? $"{n} trip(s), {c} clips" : string.Empty;
                break;
            }
            case 1:
                if (string.IsNullOrEmpty(_settings.ArchiveFolder))
                    CountLabel.Text = "No archive folder configured";
                else
                {
                    int n = _archiveSessions.Count, c = _archiveRecordings.Count;
                    CountLabel.Text = $"{n} trip(s), {c} clips archived";
                }
                break;
            case 2:
                CountLabel.Text = _clips.Count > 0 ? $"{_clips.Count} exported clip(s)" : "No clips yet";
                break;
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
            RefreshBookmarkCanvas();
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

        foreach (var (a, btn) in _angleBtns)
            btn.IsChecked = a == angle;

        foreach (var (a, me) in _videos)
            me.IsMuted = a != angle;

        if (_quadView)
        {
            AngleLabel.Text = $"QUAD ({angle})";
        }
        else
        {
            AngleLabel.Text = angle;
            foreach (var (a, me) in _videos)
                me.Visibility = a == angle ? Visibility.Visible : Visibility.Hidden;
            if (refPos > TimeSpan.Zero && _videos[angle].Source is not null)
                _videos[angle].Position = refPos;
        }
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
        if (_currentTs is null && _clipModePath is null) return;

        var active       = _videos[_activeAngle];
        var pos          = active.Position;
        // While a cross-clip seek is in flight, show the target time so the
        // display doesn't flash the clip-boundary offset before the seek lands.
        var sessionPosMs = _pendingSeekMs.HasValue && _pendingOpens > 0
            ? _sessionOffsetMs + _pendingSeekMs.Value
            : _sessionOffsetMs + (long)pos.TotalMilliseconds;

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

        // Capture duration when a clip file opens
        if (_clipModePath is not null && sender == VideoFront)
        {
            var dur = VideoFront.NaturalDuration;
            if (dur.HasTimeSpan)
            {
                _sessionTotalMs = (long)dur.TimeSpan.TotalMilliseconds;
                DurLabel.Text   = FormatTime(dur.TimeSpan);
            }
        }

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
        {
            if (_clipModePath is not null)
                StopAll();
            else
                AdvanceToNextClip();
        }
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

        // Clips tab: re-sync list in case archive folder changed
        if (SourceTabs.SelectedIndex == 2)
            LoadClips();
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
        BtnDelete.IsEnabled  = true;
        if (item.FirstTs == _currentTs && item.SessionIndex == _currentSession) return;

        ExitClipMode();
        ResetInOut();
        bool wasPlaying = _isPlaying;
        UseSource(_driveRecordings, _driveSessions);
        InitSessionTimeline(item.SessionIndex);
        LoadRecording(item.FirstTs, item.SessionIndex);
        RefreshBookmarkCanvas();
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

        BtnDeleteArchive.IsEnabled = true;
        if (item.FirstTs == _currentTs && item.SessionIndex == _currentSession) return;

        ExitClipMode();
        ResetInOut();
        bool wasPlaying = _isPlaying;
        UseSource(_archiveRecordings, _archiveSessions);
        InitSessionTimeline(item.SessionIndex);
        LoadRecording(item.FirstTs, item.SessionIndex);
        RefreshBookmarkCanvas();
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
        if (e.Key == Key.Escape)
        {
            if (_bookmarkInputActive) { DismissBookmarkInput(); e.Handled = true; }
            else if (_fullscreen)     { ToggleFullscreen();     e.Handled = true; }
            return;
        }

        if (_bookmarkInputActive) return;

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
            case Key.I:
                MarkIn();
                e.Handled = true; break;
            case Key.O:
                MarkOut();
                e.Handled = true; break;
            case Key.Tab:
                SwitchAngle(RecordingScanner.CycleAngle(_activeAngle));
                e.Handled = true; break;
            case Key.Q:
                SetQuadView(!_quadView);
                e.Handled = true; break;
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true; break;
            case Key.B:
                ShowBookmarkInput();
                e.Handled = true; break;
            case Key.S:
                CaptureFrame();
                e.Handled = true; break;
            case Key.OemOpenBrackets:
                SeekToPrevBookmark();
                e.Handled = true; break;
            case Key.OemCloseBrackets:
                SeekToNextBookmark();
                e.Handled = true; break;
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
    // Delete
    // ═════════════════════════════════════════════════════════════════════════

    private void DeleteDrive_Click(object sender, RoutedEventArgs e)
    {
        if (DriveTree.SelectedItem is not TreeViewItem tvi
            || tvi.Tag is not SessionListItem item
            || item.Session is null
            || _driveFolderPath is null) return;

        int fileCount = Archiver.CountFiles(item.Session, _driveRecordings);
        var confirm = MessageBox.Show(
            $"Permanently delete {item.Session.Count} clip(s) ({fileCount} file(s)) from the thumb drive?\n\nThis cannot be undone.",
            "Delete Trip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        DeleteFiles(item.Session, _driveRecordings);
        LoadFolder(_driveFolderPath);
    }

    private void DeleteArchive_Click(object sender, RoutedEventArgs e)
    {
        if (ArchiveTree.SelectedItem is not TreeViewItem tvi
            || tvi.Tag is not SessionListItem item
            || item.Session is null
            || string.IsNullOrEmpty(_settings.ArchiveFolder)) return;

        int fileCount = Archiver.CountFiles(item.Session, _archiveRecordings);
        var confirm = MessageBox.Show(
            $"Permanently delete {item.Session.Count} clip(s) ({fileCount} file(s)) from the archive?\n\nThis cannot be undone.",
            "Delete Trip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (ReferenceEquals(_recordings, _archiveRecordings))
        {
            StopAll();
            foreach (var me in _videos.Values) me.Source = null;
            _currentTs      = null;
            _currentSession = -1;
        }

        DeleteFiles(item.Session, _archiveRecordings);
        PruneEmptyDateDirs(_settings.ArchiveFolder);
        LoadArchive();
        StatusLabel.Text = $"Deleted {fileCount} file(s) from archive.";
    }

    private static void DeleteFiles(
        IReadOnlyList<string> timestamps,
        SortedDictionary<string, Dictionary<string, string>> recordings)
    {
        foreach (var ts in timestamps)
        {
            if (!recordings.TryGetValue(ts, out var files)) continue;
            foreach (var path in files.Values)
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void PruneEmptyDateDirs(string archiveRoot)
    {
        if (!Directory.Exists(archiveRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(archiveRoot))
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Clips tab
    // ═════════════════════════════════════════════════════════════════════════

    private void LoadClips()
    {
        _clips = string.IsNullOrEmpty(_settings.ArchiveFolder)
            ? []
            : ClipsManager.ScanClips(_settings.ArchiveFolder);

        PopulateClipsTree(_clips);

        if (SourceTabs.SelectedIndex == 2)
            UpdateCountLabel();
    }

    private void PopulateClipsTree(List<ClipEntry> clips)
    {
        _suppressTreeChange = true;
        ClipsTree.Items.Clear();

        var dateStyle = (Style)FindResource("DateNodeStyle");
        string? prevDate = null;
        TreeViewItem? dateNode = null;

        foreach (var clip in clips)
        {
            var dateStr = clip.Date.ToString("yyyy-MM-dd");
            if (dateStr != prevDate)
            {
                prevDate = dateStr;
                dateNode = new TreeViewItem
                {
                    Header     = dateStr,
                    IsExpanded = true,
                    Style      = dateStyle,
                };
                ClipsTree.Items.Add(dateNode);
            }

            dateNode!.Items.Add(new TreeViewItem
            {
                Header  = clip.DisplayLabel,
                Tag     = new ClipListItem { FilePath = clip.FilePath, Label = clip.DisplayLabel },
                Padding = new Thickness(4, 3, 4, 3),
            });
        }

        if (ClipsTree.Items.Count == 0)
        {
            ClipsTree.Items.Add(new TreeViewItem
            {
                Header    = string.IsNullOrEmpty(_settings.ArchiveFolder)
                            ? "No archive folder set.\nUse File → Set Archive Folder…"
                            : "No exported clips yet.\nUse I/O to mark in/out points, then Export.",
                Focusable = false,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            });
        }

        _suppressTreeChange = false;
    }

    private void ClipsTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeChange) return;

        if (e.NewValue is TreeViewItem tvi && tvi.Tag is not ClipListItem)
        {
            tvi.IsSelected = false;
            return;
        }

        if (e.NewValue is not TreeViewItem tv || tv.Tag is not ClipListItem item) return;

        BtnDeleteClip.IsEnabled = true;
        LoadClipFile(item.FilePath);
    }

    private void LoadClipFile(string path)
    {
        if (_quadView) SetQuadView(false);
        StopAll();

        _clipModePath    = path;
        _currentTs       = null;
        _currentSession  = -1;
        _sessionOffsetMs = 0;
        _sessionTotalMs  = 0;
        _clipOffsetsMs   = [0];
        _pendingOpens    = 1;
        _pendingSeekMs   = null;

        foreach (var (angle, me) in _videos)
        {
            me.Source     = angle == "FRONT" ? new Uri(path, UriKind.Absolute) : null;
            me.Visibility = angle == "FRONT" ? Visibility.Visible : Visibility.Hidden;
            me.IsMuted    = false;
        }
        foreach (var (a, btn) in _angleBtns)
        {
            btn.IsChecked = a == "FRONT";
            btn.IsEnabled = false;
        }
        BtnQuad.IsEnabled    = false;
        BtnMarkIn.IsEnabled  = false;
        BtnMarkOut.IsEnabled = false;
        _activeAngle    = "FRONT";
        AngleLabel.Text = "CLIP";

        ResetSlider();
        ResetInOut();
        StatusLabel.Text = Path.GetFileName(path);
    }

    private void ExitClipMode()
    {
        if (_clipModePath is null) return;
        _clipModePath = null;
        foreach (var btn in _angleBtns.Values) btn.IsEnabled = true;
        BtnQuad.IsEnabled    = true;
        BtnMarkIn.IsEnabled  = true;
        BtnMarkOut.IsEnabled = true;
        foreach (var (a, me) in _videos)
            me.Visibility = _quadView ? Visibility.Visible
                                      : (a == _activeAngle ? Visibility.Visible : Visibility.Hidden);
        AngleLabel.Text = _quadView ? $"QUAD ({_activeAngle})" : _activeAngle;
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsTree.SelectedItem is not TreeViewItem tvi
            || tvi.Tag is not ClipListItem item) return;

        var confirm = MessageBox.Show(
            $"Permanently delete this exported clip?\n\n{item.Label}\n\nThis cannot be undone.",
            "Delete Clip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (_clipModePath == item.FilePath)
        {
            StopAll();
            foreach (var me in _videos.Values) me.Source = null;
            _clipModePath   = null;
            _currentSession = -1;
            foreach (var btn in _angleBtns.Values) btn.IsEnabled = true;
            AngleLabel.Text = _activeAngle;
        }

        if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
        LoadClips();
        StatusLabel.Text = "Clip deleted.";
    }

    // ═════════════════════════════════════════════════════════════════════════
    // In/Out markers
    // ═════════════════════════════════════════════════════════════════════════

    private void MarkIn()
    {
        if (_currentSession < 0) return;
        _inPointMs = _sessionOffsetMs + (long)_videos[_activeAngle].Position.TotalMilliseconds;
        if (_outPointMs >= 0 && _outPointMs <= _inPointMs) _outPointMs = -1;
        UpdateInOutDisplay();
    }

    private void MarkOut()
    {
        if (_currentSession < 0) return;
        _outPointMs = _sessionOffsetMs + (long)_videos[_activeAngle].Position.TotalMilliseconds;
        if (_inPointMs >= 0 && _inPointMs >= _outPointMs) _inPointMs = -1;
        UpdateInOutDisplay();
    }

    private void ResetInOut()
    {
        _inPointMs  = -1;
        _outPointMs = -1;
        UpdateInOutDisplay();
    }

    private void UpdateInOutDisplay()
    {
        InLabel.Text  = _inPointMs  >= 0 ? FormatTime(TimeSpan.FromMilliseconds(_inPointMs))  : "–";
        OutLabel.Text = _outPointMs >= 0 ? FormatTime(TimeSpan.FromMilliseconds(_outPointMs)) : "–";
        SelectionDurLabel.Text = (_inPointMs >= 0 && _outPointMs > _inPointMs)
            ? FormatTime(TimeSpan.FromMilliseconds(_outPointMs - _inPointMs))
            : "";
        BtnExportClip.IsEnabled = _inPointMs >= 0 && _outPointMs > _inPointMs
                                  && _currentSession >= 0;
    }

    private void MarkIn_Click(object sender, RoutedEventArgs e)  => MarkIn();
    private void MarkOut_Click(object sender, RoutedEventArgs e) => MarkOut();

    // ═════════════════════════════════════════════════════════════════════════
    // Export
    // ═════════════════════════════════════════════════════════════════════════

    private async void ExportClip_Click(object sender, RoutedEventArgs e)
    {
        if (_inPointMs < 0 || _outPointMs <= _inPointMs || _currentSession < 0) return;
        if (string.IsNullOrEmpty(_settings.ArchiveFolder) && !PickArchiveFolder()) return;

        BtnExportClip.IsEnabled = false;

        var session    = _sessions[_currentSession];
        var firstTs    = session[0];
        var angle      = _activeAngle;
        var inMs       = _inPointMs;
        var outMs      = _outPointMs;
        var recs       = _recordings;
        var offsets    = _clipOffsetsMs;

        int startIdx   = FindClipIndex(inMs);
        int endIdx     = FindClipIndex(outMs);
        int partCount  = endIdx - startIdx + 1;

        var clipsDir = ClipsManager.ClipsFolder(_settings.ArchiveFolder!);
        Directory.CreateDirectory(clipsDir);

        ArchiveProgress.Maximum    = 100 * partCount;
        ArchiveProgress.Value      = 0;
        ArchiveProgress.Visibility = Visibility.Visible;
        StatusLabel.Text           = "Exporting clip…";

        var exported = new List<string>();

        try
        {
            for (int i = startIdx; i <= endIdx; i++)
            {
                var ts = session[i];
                if (!recs.TryGetValue(ts, out var files) || !files.TryGetValue(angle, out var src))
                    continue;

                long segStartMs =  i == startIdx ? inMs  - offsets[i] : 0;
                long? segEndMs  =  i == endIdx   ? outMs - offsets[i] : null;
                int  partIdx    =  partCount > 1  ? i - startIdx + 1  : 0;

                var outName = ClipsManager.BuildClipName(firstTs, inMs, outMs, angle, partIdx);
                var outPath = Path.Combine(clipsDir, outName);

                int partBase = (i - startIdx) * 100;
                var progress = new Progress<double>(pct =>
                    ArchiveProgress.Value = partBase + pct);

                await ExportSegmentAsync(src, outPath, segStartMs, segEndMs, progress);
                exported.Add(outPath);
            }

            StatusLabel.Text = partCount > 1
                ? $"Exported {partCount} parts to clips folder."
                : "Clip exported to clips folder.";

            LoadClips();
            SourceTabs.SelectedIndex = 2;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Export error: {ex.Message}";
        }
        finally
        {
            ArchiveProgress.Visibility = Visibility.Collapsed;
            UpdateInOutDisplay();
        }
    }

    private static async Task ExportSegmentAsync(
        string sourcePath, string destPath,
        long startMs, long? endMs,
        IProgress<double>? progress = null)
    {
        var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
        var destFolder = await StorageFolder.GetFolderFromPathAsync(
            Path.GetDirectoryName(destPath)!);
        var destFile = await destFolder.CreateFileAsync(
            Path.GetFileName(destPath),
            CreationCollisionOption.ReplaceExisting);

        var profile    = await MediaEncodingProfile.CreateFromFileAsync(sourceFile);
        var transcoder = new MediaTranscoder
        {
            TrimStartTime = TimeSpan.FromMilliseconds(startMs),
        };
        if (endMs.HasValue)
            transcoder.TrimStopTime = TimeSpan.FromMilliseconds(endMs.Value);

        var prepared = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"Cannot transcode: {prepared.FailureReason}");

        if (progress is not null)
            await prepared.TranscodeAsync().AsTask(new Progress<double>(p => progress.Report(p)));
        else
            await prepared.TranscodeAsync();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Quad view
    // ═════════════════════════════════════════════════════════════════════════

    private void SetQuadView(bool quad)
    {
        _quadView = quad;
        BtnQuad.IsChecked = quad;

        VideoGrid.RowDefinitions.Clear();
        VideoGrid.ColumnDefinitions.Clear();

        if (quad)
        {
            VideoGrid.RowDefinitions.Add(new RowDefinition());
            VideoGrid.RowDefinitions.Add(new RowDefinition());
            VideoGrid.ColumnDefinitions.Add(new ColumnDefinition());
            VideoGrid.ColumnDefinitions.Add(new ColumnDefinition());

            Grid.SetRow(VideoFront, 0); Grid.SetColumn(VideoFront, 0);
            Grid.SetRow(VideoLeft,  0); Grid.SetColumn(VideoLeft,  1);
            Grid.SetRow(VideoRear,  1); Grid.SetColumn(VideoRear,  0);
            Grid.SetRow(VideoRight, 1); Grid.SetColumn(VideoRight, 1);

            foreach (var (a, me) in _videos)
            {
                me.Visibility = Visibility.Visible;
                me.IsMuted    = a != _activeAngle;
            }
            AngleLabel.Text = $"QUAD ({_activeAngle})";
        }
        else
        {
            foreach (var me in _videos.Values)
            {
                Grid.SetRow(me, 0);
                Grid.SetColumn(me, 0);
            }
            foreach (var (a, me) in _videos)
            {
                me.Visibility = a == _activeAngle ? Visibility.Visible : Visibility.Hidden;
                me.IsMuted    = a != _activeAngle;
            }
            AngleLabel.Text = _activeAngle;
        }
    }

    private void BtnQuad_Click(object sender, RoutedEventArgs e)
        => SetQuadView(BtnQuad.IsChecked == true);

    // ═════════════════════════════════════════════════════════════════════════
    // Fullscreen
    // ═════════════════════════════════════════════════════════════════════════

    private void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            WindowStyle = _savedWindowStyle;
            WindowState = _savedWindowState;
            MainMenu.Visibility       = Visibility.Visible;
            Sidebar.Visibility        = Visibility.Visible;
            AngleBtnsPanel.Visibility = Visibility.Visible;
            AngleLabel.Visibility     = Visibility.Visible;
            InOutPanel.Visibility     = Visibility.Visible;
            SeekBarGrid.Visibility    = Visibility.Visible;
            TransportPanel.Visibility = Visibility.Visible;
            StatusLabel.Visibility    = Visibility.Visible;
            MainGrid.ColumnDefinitions[0].Width = new GridLength(240);
        }
        else
        {
            _savedWindowStyle = WindowStyle;
            _savedWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            MainMenu.Visibility       = Visibility.Collapsed;
            Sidebar.Visibility        = Visibility.Collapsed;
            AngleBtnsPanel.Visibility = Visibility.Collapsed;
            AngleLabel.Visibility     = Visibility.Collapsed;
            InOutPanel.Visibility     = Visibility.Collapsed;
            SeekBarGrid.Visibility    = Visibility.Collapsed;
            TransportPanel.Visibility = Visibility.Collapsed;
            StatusLabel.Visibility    = Visibility.Collapsed;
            MainGrid.ColumnDefinitions[0].Width = new GridLength(0);
        }
        _fullscreen = !_fullscreen;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Bookmarks
    // ═════════════════════════════════════════════════════════════════════════

    private void LoadBookmarks()
    {
        _bookmarks = string.IsNullOrEmpty(_settings.ArchiveFolder)
            ? []
            : BookmarkManager.Load(_settings.ArchiveFolder);
        RefreshBookmarkCanvas();
    }

    private void AddBookmark(string firstTs, long sessionMs, string note)
    {
        if (string.IsNullOrEmpty(_settings.ArchiveFolder)) return;
        _bookmarks.Add(new Bookmark(firstTs, sessionMs, note));
        BookmarkManager.Save(_settings.ArchiveFolder, _bookmarks);
        RefreshBookmarkCanvas();
    }

    private void DeleteBookmark(Bookmark bm)
    {
        if (string.IsNullOrEmpty(_settings.ArchiveFolder)) return;
        _bookmarks.Remove(bm);
        BookmarkManager.Save(_settings.ArchiveFolder, _bookmarks);
        RefreshBookmarkCanvas();
    }

    private void RefreshBookmarkCanvas()
    {
        BookmarkCanvas.Children.Clear();
        if (_currentSession < 0 || _sessionTotalMs == 0) return;

        var session = _sessions[_currentSession];
        var sessionBookmarks = BookmarkManager.ForSession(_bookmarks, session[0]);
        double width = BookmarkCanvas.ActualWidth;
        if (width <= 0) return;

        foreach (var bm in sessionBookmarks)
        {
            double x = (double)bm.SessionMs / _sessionTotalMs * width;
            var line = new Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = Math.Max(BookmarkCanvas.ActualHeight, 14),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 233, 69, 96)),
                StrokeThickness = 2,
                ToolTip = string.IsNullOrWhiteSpace(bm.Note)
                    ? FormatTime(TimeSpan.FromMilliseconds(bm.SessionMs))
                    : $"{FormatTime(TimeSpan.FromMilliseconds(bm.SessionMs))}: {bm.Note}",
            };
            var captured = bm;
            line.MouseRightButtonDown += (_, e2) =>
            {
                var r = MessageBox.Show(
                    $"Delete bookmark at {FormatTime(TimeSpan.FromMilliseconds(captured.SessionMs))}?",
                    "Delete Bookmark", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) DeleteBookmark(captured);
                e2.Handled = true;
            };
            BookmarkCanvas.Children.Add(line);
        }
    }

    private void ShowBookmarkInput()
    {
        if (_currentSession < 0) return;
        _bookmarkInputActive = true;
        BookmarkInputPanel.Visibility = Visibility.Visible;
        BookmarkNoteBox.Text = "";
        BookmarkNoteBox.Focus();
    }

    private void CommitBookmark()
    {
        if (_currentSession < 0) return;
        long sessionMs = _sessionOffsetMs + (long)_videos[_activeAngle].Position.TotalMilliseconds;
        var firstTs = _sessions[_currentSession][0];
        AddBookmark(firstTs, sessionMs, BookmarkNoteBox.Text.Trim());
        StatusLabel.Text = $"Bookmark added at {FormatTime(TimeSpan.FromMilliseconds(sessionMs))}";
        DismissBookmarkInput();
    }

    private void DismissBookmarkInput()
    {
        _bookmarkInputActive = false;
        BookmarkInputPanel.Visibility = Visibility.Collapsed;
    }

    private void BtnBookmark_Click(object sender, RoutedEventArgs e)       => ShowBookmarkInput();
    private void BtnBookmarkSave_Click(object sender, RoutedEventArgs e)   => CommitBookmark();
    private void BtnBookmarkCancel_Click(object sender, RoutedEventArgs e) => DismissBookmarkInput();

    private void BookmarkNoteBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitBookmark(); e.Handled = true; }
    }

    private void BookmarkCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RefreshBookmarkCanvas();

    private void SeekToPrevBookmark()
    {
        if (_currentSession < 0) return;
        long currentMs = _sessionOffsetMs + (long)_videos[_activeAngle].Position.TotalMilliseconds;
        var bm = BookmarkManager.PrevBookmark(_bookmarks, _sessions[_currentSession][0], currentMs);
        if (bm is not null) SeekToSessionMs(bm.SessionMs);
    }

    private void SeekToNextBookmark()
    {
        if (_currentSession < 0) return;
        long currentMs = _sessionOffsetMs + (long)_videos[_activeAngle].Position.TotalMilliseconds;
        var bm = BookmarkManager.NextBookmark(_bookmarks, _sessions[_currentSession][0], currentMs);
        if (bm is not null) SeekToSessionMs(bm.SessionMs);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Frame capture
    // ═════════════════════════════════════════════════════════════════════════

    private void CaptureFrame()
    {
        var target = _quadView ? (FrameworkElement)VideoGrid : _videos[_activeAngle];
        int w = (int)target.ActualWidth;
        int h = (int)target.ActualHeight;
        if (w == 0 || h == 0) return;

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(target);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        var savePath = BuildCapturePath();
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        using var stream = File.Create(savePath);
        encoder.Save(stream);
        StatusLabel.Text = $"Frame saved: {savePath}";
    }

    private string BuildCapturePath()
    {
        var folder = !string.IsNullOrEmpty(_settings.ArchiveFolder)
            ? _settings.ArchiveFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var ts = _currentTs is not null
            ? SessionGrouper.ParseTimestamp(_currentTs).ToString("yyyy-MM-dd_HH-mm-ss")
            : DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        var angle = _quadView ? "QUAD" : _activeAngle;
        return Path.Combine(folder, $"capture_{ts}_{angle}.png");
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

internal sealed class ClipListItem
{
    public required string FilePath { get; init; }
    public required string Label    { get; init; }
}
