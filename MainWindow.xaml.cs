using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.Win32;

// Alias to avoid collision between WPF and WinForms
using WinFormsApp = System.Windows.Forms;

namespace SimplePlayer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // ======================== Enums ========================

        public enum PlayMode
        {
            Sequential,
            Shuffle,
            SingleLoop
        }

        // (Keyboard shortcuts are now handled via Window_PreviewKeyDown)

        // ======================== Win32 Constants for Global Media Keys ========================

        private const int WM_APPCOMMAND = 0x0319;

        // APPCOMMAND values (shifted right 16, masked to 0xFFF)
        private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
        private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
        private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
        private const int APPCOMMAND_MEDIA_STOP = 13;

        // ======================== Constants ========================

        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".flac"
        };

        private static readonly string ConfigFilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private static readonly Regex LrcTimestampRegex = new(
            @"\[(\d{2}):(\d{2})(?:\.(\d{2,3}))?\]",
            RegexOptions.Compiled);

        // ======================== Fields ========================

        private readonly System.Windows.Media.MediaPlayer _player = new();
        private readonly List<string> _songPaths = new();
        private readonly List<string> _songDisplayNames = new();
        private readonly DispatcherTimer _timer;
        private readonly Random _random = new();
        private readonly SortedList<double, string> _lrcLines = new();

        private int _currentIndex = -1;
        private bool _isPlaying;
        private bool _isDraggingSlider;
        private PlayMode _currentMode = PlayMode.Sequential;
        private string _searchFilter = string.Empty;

        // System tray icon (WinForms NotifyIcon)
        private WinFormsApp.NotifyIcon? _trayIcon;

        // Flag to show tray balloon tip only once per session
        private bool _hasShownTrayHint;

        // Current language: "zh-CN" or "en-US"
        private string _currentLang = "zh-CN";

        // Handle for the WndProc hook
        private HwndSource? _hwndSource;

        // ======================== Constructor ========================

        public MainWindow()
        {
            InitializeComponent();

            _player.Volume = 0.5;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += Timer_Tick;

            _player.MediaOpened += Player_MediaOpened;
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaFailed += Player_MediaFailed;

            // Keyboard shortcuts are handled via Window_PreviewKeyDown (see XAML)

            // Window events
            Closed += MainWindow_Closed;
            StateChanged += MainWindow_StateChanged;
            SourceInitialized += MainWindow_SourceInitialized;

            // Initialize system tray icon
            InitializeTrayIcon();

            // Restore saved state
            LoadConfig();
        }

        // ======================== Window Lifecycle ========================

        /// <summary>
        /// Called after the window handle (HWND) is created.
        /// Hooks into the Windows message pump (WndProc) to intercept
        /// global media key messages (WM_APPCOMMAND).
        /// </summary>
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwndSource?.AddHook(WndProc);
        }

        /// <summary>
        /// Handles window state changes. When minimized, hides the window
        /// to the system tray instead of the taskbar.
        /// </summary>
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;

                // Only show the balloon tip once per session
                if (!_hasShownTrayHint)
                {
                    _hasShownTrayHint = true;
                    _trayIcon?.ShowBalloonTip(
                        2000,
                        "Simple Player",
                        GetLangString("Str_TrayBalloonText"),
                        WinFormsApp.ToolTipIcon.Info);
                }
            }
        }

        /// <summary>
        /// Saves config, cleans up tray icon, unhooks WndProc, and releases media resources.
        /// </summary>
        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            SaveConfig();

            // Remove WndProc hook
            _hwndSource?.RemoveHook(WndProc);

            // Dispose tray icon
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            // Stop timer and detach events
            _timer.Stop();
            _timer.Tick -= Timer_Tick;

            _player.MediaOpened -= Player_MediaOpened;
            _player.MediaEnded -= Player_MediaEnded;
            _player.MediaFailed -= Player_MediaFailed;

            _player.Stop();
            _player.Close();
        }

        // ======================== System Tray ========================

        /// <summary>
        /// Creates the WinForms NotifyIcon with a context menu.
        /// Uses the application's icon, or falls back to a system default.
        /// </summary>
        private void InitializeTrayIcon()
        {
            _trayIcon = new WinFormsApp.NotifyIcon
            {
                Text = "Simple Player",
                Visible = true
            };

            // Try to use the application's own icon; fall back to a system icon
            try
            {
                string exePath = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                    {
                        _trayIcon.Icon = icon;
                    }
                    else
                    {
                        _trayIcon.Icon = SystemIcons.Application;
                    }
                }
                else
                {
                    _trayIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _trayIcon.Icon = SystemIcons.Application;
            }

            // Context menu (built dynamically so language switching can rebuild it)
            RebuildTrayContextMenu();

            // Double-click: restore window
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }

        /// <summary>
        /// Restores the window from the system tray back to the taskbar.
        /// </summary>
        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>
        /// Builds or rebuilds the tray icon context menu using current language strings.
        /// </summary>
        private void RebuildTrayContextMenu()
        {
            if (_trayIcon == null) return;

            var contextMenu = new WinFormsApp.ContextMenuStrip();

            var playPauseItem = new WinFormsApp.ToolStripMenuItem(GetLangString("Str_TrayPlayPause"));
            playPauseItem.Click += (_, _) => Dispatcher.Invoke(TogglePlayPause);

            var nextItem = new WinFormsApp.ToolStripMenuItem(GetLangString("Str_TrayNext"));
            nextItem.Click += (_, _) => Dispatcher.Invoke(() =>
            {
                if (_songPaths.Count > 0) PlayNextSong();
            });

            var exitItem = new WinFormsApp.ToolStripMenuItem(GetLangString("Str_TrayExit"));
            exitItem.Click += (_, _) => Dispatcher.Invoke(Close);

            contextMenu.Items.Add(playPauseItem);
            contextMenu.Items.Add(nextItem);
            contextMenu.Items.Add(new WinFormsApp.ToolStripSeparator());
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = contextMenu;
        }

        // ======================== Global Media Keys (WndProc Hook) ========================

        /// <summary>
        /// Intercepts Windows messages to handle global media key events.
        /// Responds to WM_APPCOMMAND for Play/Pause, Next, Previous, and Stop.
        /// This allows the user to control playback using keyboard media keys
        /// even while the application is in the background.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_APPCOMMAND)
            {
                int command = (int)((uint)lParam >> 16) & 0xFFF;

                switch (command)
                {
                    case APPCOMMAND_MEDIA_PLAY_PAUSE:
                        TogglePlayPause();
                        handled = true;
                        break;

                    case APPCOMMAND_MEDIA_NEXTTRACK:
                        if (_songPaths.Count > 0)
                            PlayNextSong();
                        handled = true;
                        break;

                    case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                        if (_songPaths.Count > 0)
                        {
                            if (_currentMode == PlayMode.Shuffle)
                                PlaySongAtIndex(GetRandomIndex());
                            else
                            {
                                int prevIndex = (_currentIndex - 1 + _songPaths.Count) % _songPaths.Count;
                                PlaySongAtIndex(prevIndex);
                            }
                        }
                        handled = true;
                        break;

                    case APPCOMMAND_MEDIA_STOP:
                        if (_isPlaying)
                        {
                            _player.Pause();
                            _timer.Stop();
                            _isPlaying = false;
                            BtnPlayPause.Content = "▶";
                        }
                        handled = true;
                        break;
                }
            }

            return IntPtr.Zero;
        }

        // ======================== Keyboard Shortcut Handler (PreviewKeyDown) ========================

        /// <summary>
        /// Handles keyboard shortcuts at the Window level via PreviewKeyDown.
        /// This tunneling event fires before any child control handles the key,
        /// ensuring shortcuts work regardless of which element has focus.
        /// </summary>
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Do NOT intercept keys if the SearchBox has keyboard focus (user is typing)
            if (SearchBox.IsKeyboardFocusWithin)
                return;

            switch (e.Key)
            {
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;

                case Key.Left:
                    SeekRelative(-5);
                    e.Handled = true;
                    break;

                case Key.Right:
                    SeekRelative(5);
                    e.Handled = true;
                    break;

                case Key.Up:
                    AdjustVolume(0.1);
                    e.Handled = true;
                    break;

                case Key.Down:
                    AdjustVolume(-0.1);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// Seeks the playback position by the given number of seconds (positive or negative).
        /// Clamps to the valid range [0, duration].
        /// </summary>
        private void SeekRelative(double offsetSeconds)
        {
            if (_currentIndex < 0 || !_player.NaturalDuration.HasTimeSpan)
                return;

            double totalSeconds = _player.NaturalDuration.TimeSpan.TotalSeconds;
            double newPosition = Math.Clamp(
                _player.Position.TotalSeconds + offsetSeconds, 0, totalSeconds);

            _player.Position = TimeSpan.FromSeconds(newPosition);
        }

        /// <summary>
        /// Adjusts the volume by the given delta (e.g., +0.1 or -0.1).
        /// Clamps to [0.0, 1.0] and syncs the volume slider.
        /// </summary>
        private void AdjustVolume(double delta)
        {
            double newVolume = Math.Clamp(_player.Volume + delta, 0.0, 1.0);
            _player.Volume = newVolume;
            SliderVolume.Value = newVolume;
        }

        /// <summary>
        /// Shared play/pause toggle logic used by local shortcut, tray menu, and media keys.
        /// </summary>
        private void TogglePlayPause()
        {
            if (_songPaths.Count == 0)
                return;

            if (_isPlaying)
            {
                _player.Pause();
                _timer.Stop();
                _isPlaying = false;
                BtnPlayPause.Content = "▶";
            }
            else
            {
                if (_currentIndex < 0)
                {
                    PlaySongAtIndex(0);
                }
                else
                {
                    _player.Play();
                    _timer.Start();
                    _isPlaying = true;
                    BtnPlayPause.Content = "⏸";
                }
            }
        }

        // ======================== Config Persistence ========================

        private void SaveConfig()
        {
            try
            {
                var config = new PlayerConfig
                {
                    Playlist = new List<string>(_songPaths),
                    PlayModeValue = (int)_currentMode,
                    Volume = _player.Volume,
                    Language = _currentLang
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(ConfigFilePath, json);
            }
            catch { /* Silent fail */ }
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                    return;

                string json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<PlayerConfig>(json);

                if (config == null)
                    return;

                if (config.PlayModeValue >= 0 && config.PlayModeValue <= 2)
                {
                    _currentMode = (PlayMode)config.PlayModeValue;
                    BtnMode.Content = GetModeLabel(_currentMode);
                }

                // Restore language preference
                if (!string.IsNullOrEmpty(config.Language) &&
                    (config.Language == "zh-CN" || config.Language == "en-US"))
                {
                    _currentLang = config.Language;
                    ApplyLanguage(_currentLang);
                }

                double vol = Math.Clamp(config.Volume, 0.0, 1.0);
                _player.Volume = vol;
                SliderVolume.Value = vol;

                foreach (string filePath in config.Playlist)
                {
                    if (string.IsNullOrWhiteSpace(filePath) ||
                        _songPaths.Contains(filePath) ||
                        !File.Exists(filePath))
                        continue;

                    _songPaths.Add(filePath);
                    _songDisplayNames.Add(ReadDisplayName(filePath));
                }

                RefreshDisplayedList();
            }
            catch { /* Silent fail */ }
        }

        // ======================== Timer Tick ========================

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isDraggingSlider || !_player.NaturalDuration.HasTimeSpan)
                return;

            TimeSpan position = _player.Position;
            TimeSpan duration = _player.NaturalDuration.TimeSpan;

            TxtCurrentTime.Text = FormatTime(position);
            TxtTotalTime.Text = FormatTime(duration);

            if (duration.TotalSeconds > 0)
            {
                SliderProgress.Value = (position.TotalSeconds / duration.TotalSeconds) * 100.0;
            }

            UpdateLyricsDisplay(position.TotalSeconds);
        }

        // ======================== Media Events ========================

        private void Player_MediaOpened(object? sender, EventArgs e)
        {
            if (_player.NaturalDuration.HasTimeSpan)
            {
                TxtTotalTime.Text = FormatTime(_player.NaturalDuration.TimeSpan);
            }
            TxtCurrentTime.Text = "00:00";
            SliderProgress.Value = 0;
        }

        private void Player_MediaEnded(object? sender, EventArgs e)
        {
            if (_currentMode == PlayMode.SingleLoop)
            {
                _player.Position = TimeSpan.Zero;
                _player.Play();
            }
            else
            {
                PlayNextSong();
            }
        }

        private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            _timer.Stop();
            _isPlaying = false;
            BtnPlayPause.Content = "▶";

            string errorMsg = e.ErrorException?.Message ?? "Unknown error";
            string msgTemplate = GetLangString("Str_PlaybackErrorMsg");
            string title = GetLangString("Str_PlaybackError");
            System.Windows.MessageBox.Show(
                string.Format(msgTemplate.Replace("\\n", "\n"), errorMsg),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // ======================== Slider Drag Events ========================

        private void SliderProgress_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void SliderProgress_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isDraggingSlider = false;

            if (_currentIndex < 0 || !_player.NaturalDuration.HasTimeSpan)
                return;

            double totalSeconds = _player.NaturalDuration.TimeSpan.TotalSeconds;
            if (totalSeconds <= 0) return;

            double targetSeconds = (SliderProgress.Value / 100.0) * totalSeconds;
            _player.Position = TimeSpan.FromSeconds(targetSeconds);
        }

        /// <summary>
        /// Handles click-to-seek on the progress slider.
        /// When IsMoveToPointEnabled is True, clicking the track jumps the value
        /// immediately, but the Position must still be synced via ValueChanged.
        /// Only applies when NOT dragging (drag uses DragCompleted).
        /// </summary>
        private void SliderProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Skip if user is dragging (handled by DragCompleted)
            if (_isDraggingSlider)
                return;

            // Skip if no song is loaded
            if (_currentIndex < 0 || !_player.NaturalDuration.HasTimeSpan)
                return;

            double totalSeconds = _player.NaturalDuration.TimeSpan.TotalSeconds;
            if (totalSeconds <= 0) return;

            // Only seek if the difference is significant (avoids feedback loop with timer)
            double targetSeconds = (e.NewValue / 100.0) * totalSeconds;
            double currentSeconds = _player.Position.TotalSeconds;
            if (Math.Abs(targetSeconds - currentSeconds) > 0.5)
            {
                _player.Position = TimeSpan.FromSeconds(targetSeconds);
            }
        }

        // ======================== Volume Control ========================

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _player.Volume = e.NewValue;
        }

        // ======================== Search / Filter ========================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchFilter = SearchBox.Text.Trim();
            SearchBox.Tag = string.IsNullOrEmpty(SearchBox.Text) ? "placeholder" : null;
            RefreshDisplayedList();
        }

        private void RefreshDisplayedList()
        {
            PlaylistBox.Items.Clear();
            bool hasFilter = !string.IsNullOrEmpty(_searchFilter);

            for (int i = 0; i < _songDisplayNames.Count; i++)
            {
                if (hasFilter &&
                    !_songDisplayNames[i].Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                PlaylistBox.Items.Add(new PlaylistDisplayItem
                {
                    MasterIndex = i,
                    DisplayName = _songDisplayNames[i]
                });
            }
            HighlightCurrentTrack();
        }

        // ======================== Add Music ========================

        private void BtnAddMusic_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = GetLangString("Str_FileDialogTitle"),
                Filter = GetLangString("Str_FileDialogFilter"),
                Multiselect = true
            };

            if (dialog.ShowDialog() != true) return;
            AddFiles(dialog.FileNames);
        }

        // ======================== Drag & Drop ========================

        private void PlaylistBox_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void PlaylistBox_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] droppedPaths) return;

            var filesToAdd = new List<string>();

            foreach (string path in droppedPaths)
            {
                if (Directory.Exists(path))
                    CollectAudioFiles(path, filesToAdd);
                else if (File.Exists(path) && IsSupportedAudioFile(path))
                    filesToAdd.Add(path);
            }

            if (filesToAdd.Count > 0) AddFiles(filesToAdd);
        }

        private static void CollectAudioFiles(string directoryPath, List<string> results)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(
                    directoryPath, "*.*", SearchOption.AllDirectories))
                {
                    if (IsSupportedAudioFile(file))
                        results.Add(file);
                }
            }
            catch { /* Skip inaccessible directories */ }
        }

        private static bool IsSupportedAudioFile(string filePath)
            => SupportedExtensions.Contains(Path.GetExtension(filePath));

        private void AddFiles(IEnumerable<string> filePaths)
        {
            bool anyAdded = false;

            foreach (string filePath in filePaths)
            {
                if (_songPaths.Contains(filePath) || !File.Exists(filePath))
                    continue;

                _songPaths.Add(filePath);
                _songDisplayNames.Add(ReadDisplayName(filePath));
                anyAdded = true;
            }

            if (anyAdded) RefreshDisplayedList();
        }

        // ======================== Clear List ========================

        private void BtnClearList_Click(object sender, RoutedEventArgs e)
        {
            _player.Stop();
            _player.Close();
            _timer.Stop();

            _songPaths.Clear();
            _songDisplayNames.Clear();
            PlaylistBox.Items.Clear();
            _lrcLines.Clear();

            _currentIndex = -1;
            _isPlaying = false;
            _searchFilter = string.Empty;
            SearchBox.Text = string.Empty;
            BtnPlayPause.Content = "▶";

            TxtCurrentTime.Text = "00:00";
            TxtTotalTime.Text = "00:00";
            SliderProgress.Value = 0;

            TxtSongTitle.Text = GetLangString("Str_NoSongPlaying");
            TxtArtist.Text = "—";
            ImgCoverArt.Source = null;
            LyricsText.Text = "";

            Title = "Simple Player";
        }

        // ======================== Open Folder ========================

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            int targetIndex = -1;

            if (PlaylistBox.SelectedItem is PlaylistDisplayItem item)
            {
                targetIndex = item.MasterIndex;
            }
            else if (_currentIndex >= 0 && _currentIndex < _songPaths.Count)
            {
                targetIndex = _currentIndex;
            }

            if (targetIndex >= 0 && targetIndex < _songPaths.Count)
            {
                string filePath = _songPaths[targetIndex];
                if (File.Exists(filePath))
                {
                    try
                    {
                        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                    }
                    catch { /* Fallback or ignore if fails */ }
                }
            }
        }

        // ======================== Double-Click Playlist ========================

        private void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PlaylistBox.SelectedItem is not PlaylistDisplayItem item) return;
            if (item.MasterIndex < 0 || item.MasterIndex >= _songPaths.Count) return;
            PlaySongAtIndex(item.MasterIndex);
        }

        // ======================== Play / Pause (Button) ========================

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        // ======================== Prev / Next (Buttons) ========================

        private void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_songPaths.Count == 0) return;

            if (_currentMode == PlayMode.Shuffle)
                PlaySongAtIndex(GetRandomIndex());
            else
            {
                int prevIndex = (_currentIndex - 1 + _songPaths.Count) % _songPaths.Count;
                PlaySongAtIndex(prevIndex);
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_songPaths.Count == 0) return;
            PlayNextSong();
        }

        // ======================== Play Mode Toggle ========================

        private void BtnMode_Click(object sender, RoutedEventArgs e)
        {
            _currentMode = (PlayMode)(((int)_currentMode + 1) % 3);
            BtnMode.Content = GetModeLabel(_currentMode);
        }

        // ======================== Core Playback ========================

        private void PlaySongAtIndex(int index)
        {
            if (index < 0 || index >= _songPaths.Count) return;

            string filePath = _songPaths[index];
            if (!File.Exists(filePath))
            {
                string msgTemplate = GetLangString("Str_FileNotFoundMsg");
                string title = GetLangString("Str_FileNotFound");
                System.Windows.MessageBox.Show(
                    string.Format(msgTemplate.Replace("\\n", "\n"), filePath),
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _currentIndex = index;

            _player.Open(new Uri(filePath));
            _player.Play();

            _isPlaying = true;
            BtnPlayPause.Content = "⏸";

            HighlightCurrentTrack();
            _timer.Start();

            LoadMetadata(filePath);
            LoadLrcFile(filePath);

            // Update tray icon tooltip with current track
            if (_trayIcon != null)
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                // NotifyIcon.Text is limited to 127 characters
                _trayIcon.Text = name.Length > 120
                    ? $"♫ {name[..117]}..."
                    : $"♫ {name}";
            }
        }

        private void HighlightCurrentTrack()
        {
            PlaylistBox.SelectedIndex = -1;

            for (int i = 0; i < PlaylistBox.Items.Count; i++)
            {
                if (PlaylistBox.Items[i] is PlaylistDisplayItem item &&
                    item.MasterIndex == _currentIndex)
                {
                    PlaylistBox.SelectedIndex = i;
                    PlaylistBox.ScrollIntoView(PlaylistBox.SelectedItem);
                    return;
                }
            }
        }

        private void PlayNextSong()
        {
            if (_songPaths.Count == 0) return;

            if (_currentMode == PlayMode.Shuffle)
                PlaySongAtIndex(GetRandomIndex());
            else
            {
                int nextIndex = (_currentIndex + 1) % _songPaths.Count;
                PlaySongAtIndex(nextIndex);
            }
        }

        private int GetRandomIndex()
        {
            if (_songPaths.Count <= 1) return 0;

            int randomIndex;
            do { randomIndex = _random.Next(_songPaths.Count); }
            while (randomIndex == _currentIndex);

            return randomIndex;
        }

        // ======================== ID3 Metadata (TagLib) ========================

        private static string ReadDisplayName(string filePath)
        {
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                string? title = tagFile.Tag.Title;
                string? artist = tagFile.Tag.FirstPerformer;

                if (!string.IsNullOrWhiteSpace(title))
                {
                    return string.IsNullOrWhiteSpace(artist)
                        ? title
                        : $"{title}  —  {artist}";
                }
            }
            catch { /* Fall through */ }

            return Path.GetFileNameWithoutExtension(filePath);
        }

        private void LoadMetadata(string filePath)
        {
            string title = Path.GetFileNameWithoutExtension(filePath);
            string artist = GetLangString("Str_UnknownArtist");
            BitmapImage? coverImage = null;

            try
            {
                using var tagFile = TagLib.File.Create(filePath);

                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                    title = tagFile.Tag.Title;

                if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer))
                    artist = tagFile.Tag.FirstPerformer;

                if (tagFile.Tag.Pictures.Length > 0)
                {
                    var picture = tagFile.Tag.Pictures[0];
                    coverImage = new BitmapImage();
                    coverImage.BeginInit();
                    coverImage.CacheOption = BitmapCacheOption.OnLoad;
                    coverImage.StreamSource = new MemoryStream(picture.Data.Data);
                    coverImage.DecodePixelWidth = 112;
                    coverImage.EndInit();
                    coverImage.Freeze();
                }
            }
            catch { /* Use fallback values */ }

            TxtSongTitle.Text = title;
            TxtArtist.Text = artist;
            ImgCoverArt.Source = coverImage;

            Title = $"♫ {title} — Simple Player";
        }

        // ======================== LRC Lyrics ========================

        private void LoadLrcFile(string audioFilePath)
        {
            _lrcLines.Clear();
            LyricsText.Text = "";

            string? dir = Path.GetDirectoryName(audioFilePath);
            string baseName = Path.GetFileNameWithoutExtension(audioFilePath);

            if (string.IsNullOrEmpty(dir)) return;

            string lrcPath = Path.Combine(dir, baseName + ".lrc");
            if (!File.Exists(lrcPath)) return;

            try
            {
                string[] lines = File.ReadAllLines(lrcPath);

                foreach (string line in lines)
                {
                    var matches = LrcTimestampRegex.Matches(line);
                    if (matches.Count == 0) continue;

                    string text = LrcTimestampRegex.Replace(line, "").Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    foreach (Match match in matches)
                    {
                        int minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        int seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

                        double ms = 0;
                        if (match.Groups[3].Success)
                        {
                            string msStr = match.Groups[3].Value;
                            ms = msStr.Length == 2
                                ? int.Parse(msStr, CultureInfo.InvariantCulture) * 10.0
                                : int.Parse(msStr, CultureInfo.InvariantCulture);
                        }

                        double totalSeconds = minutes * 60.0 + seconds + ms / 1000.0;
                        _lrcLines.TryAdd(totalSeconds, text);
                    }
                }
            }
            catch { _lrcLines.Clear(); }
        }

        private void UpdateLyricsDisplay(double currentSeconds)
        {
            if (_lrcLines.Count == 0) return;

            var keys = _lrcLines.Keys;
            int low = 0, high = keys.Count - 1, bestIndex = -1;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (keys[mid] <= currentSeconds) { bestIndex = mid; low = mid + 1; }
                else high = mid - 1;
            }

            string? currentLine = bestIndex >= 0 ? _lrcLines.Values[bestIndex] : null;
            if (LyricsText.Text != currentLine)
                LyricsText.Text = currentLine ?? "";
        }

        // ======================== Utilities ========================

        private static string FormatTime(TimeSpan time)
            => $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";

        // ======================== Display Item Model ========================

        private class PlaylistDisplayItem
        {
            public int MasterIndex { get; init; }
            public required string DisplayName { get; init; }
            public override string ToString() => DisplayName;
        }

        // ======================== Language Switching ========================

        /// <summary>
        /// Retrieves a localized string from the application resource dictionary.
        /// </summary>
        private string GetLangString(string key)
        {
            return System.Windows.Application.Current.TryFindResource(key) as string ?? key;
        }

        /// <summary>
        /// Returns the localized label for a playback mode.
        /// </summary>
        private string GetModeLabel(PlayMode mode)
        {
            return mode switch
            {
                PlayMode.Sequential => GetLangString("Str_ModeSequential"),
                PlayMode.Shuffle    => GetLangString("Str_ModeShuffle"),
                PlayMode.SingleLoop => GetLangString("Str_ModeSingleLoop"),
                _                   => ""
            };
        }

        /// <summary>
        /// Switches the application language by replacing the merged ResourceDictionary.
        /// </summary>
        private void ApplyLanguage(string lang)
        {
            _currentLang = lang;

            var dict = new ResourceDictionary
            {
                Source = new Uri($"Lang/{lang}.xaml", UriKind.Relative)
            };

            // Replace the first (language) merged dictionary
            var mergedDicts = System.Windows.Application.Current.Resources.MergedDictionaries;
            if (mergedDicts.Count > 0)
                mergedDicts[0] = dict;
            else
                mergedDicts.Add(dict);

            // Update elements that are set in C# code (not via DynamicResource)
            BtnMode.Content = GetModeLabel(_currentMode);

            // Update "No song playing" text only if no song is loaded
            if (_currentIndex < 0)
            {
                TxtSongTitle.Text = GetLangString("Str_NoSongPlaying");
            }

            // Rebuild tray context menu with new language
            RebuildTrayContextMenu();
        }

        /// <summary>
        /// Language toggle button click handler.
        /// Switches between Chinese and English.
        /// </summary>
        private void BtnLang_Click(object sender, RoutedEventArgs e)
        {
            string newLang = _currentLang == "zh-CN" ? "en-US" : "zh-CN";
            ApplyLanguage(newLang);
        }
    }
}
