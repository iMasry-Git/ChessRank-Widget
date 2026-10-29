// ChessRank Widget v3.1.0 - Native Windows desktop widget for Chess.com ratings.
// Build: run build.bat (uses the C# compiler that ships with .NET Framework 4.x).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ChessRankWidget
{
    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------
    public class WidgetConfig
    {
        public const string DemoUser = "GRANDMASTER_USER";

        public string Username { get; set; }
        public string TimeControl { get; set; }
        public int TargetRank { get; set; }
        public int BaselineRank { get; set; }      // -1 = auto (target - 150)
        public int RefreshInterval { get; set; }   // seconds, 0 = manual
        public string Layer { get; set; }          // desktop | topmost | normal
        public double Scale { get; set; }
        public double Opacity { get; set; }
        public bool LockPosition { get; set; }
        public bool SoundEnabled { get; set; }
        public bool StartWithWindows { get; set; }
        public bool HasPosition { get; set; }
        public double WindowX { get; set; }
        public double WindowY { get; set; }
        public int LastRating { get; set; }        // 0 = no rating available
        public int LastPeak { get; set; }
        public int LastDelta { get; set; }
        public string LastSynced { get; set; }

        public WidgetConfig()
        {
            Username = DemoUser;
            TimeControl = "chess_rapid";
            TargetRank = 2200;
            BaselineRank = 2050;
            RefreshInterval = 1800;
            Layer = "desktop";
            Scale = 0.9;
            Opacity = 1.0;
            SoundEnabled = true;
            LoadDemoValues();
        }

        [ScriptIgnore]
        public bool IsDemo
        {
            get { return string.IsNullOrWhiteSpace(Username) || string.Equals(Username.Trim(), DemoUser, StringComparison.OrdinalIgnoreCase); }
        }

        public void LoadDemoValues()
        {
            LastRating = 2145;
            LastPeak = 2210;
            LastDelta = 12;
            LastSynced = null;
        }

        public int EffectiveBaseline
        {
            get { return (BaselineRank >= 0 && BaselineRank < TargetRank) ? BaselineRank : Math.Max(0, TargetRank - 150); }
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Username)) Username = DemoUser;
            Username = Username.Trim();
            if (TimeControl != "chess_rapid" && TimeControl != "chess_blitz" && TimeControl != "chess_bullet" && TimeControl != "chess_daily")
                TimeControl = "chess_rapid";
            if (TargetRank <= 0) TargetRank = 2200;
            if (BaselineRank < -1) BaselineRank = -1;
            if (RefreshInterval < 0) RefreshInterval = 1800;
            if (RefreshInterval > 0 && RefreshInterval < 60) RefreshInterval = 60;
            if (Layer != "desktop" && Layer != "topmost" && Layer != "normal") Layer = "desktop";
            if (Scale < 0.2 || Scale > 2.0) Scale = 0.9;
            if (Opacity < 0.3 || Opacity > 1.0) Opacity = 1.0;
        }
    }

    public static class ConfigStore
    {
        public static readonly string Folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChessRankWidget");
        public static readonly string FilePath = System.IO.Path.Combine(Folder, "settings.json");

        public static WidgetConfig Load()
        {
            WidgetConfig cfg = null;
            try
            {
                if (File.Exists(FilePath))
                    cfg = new JavaScriptSerializer().Deserialize<WidgetConfig>(File.ReadAllText(FilePath));
            }
            catch (Exception ex) { Log.Write("Config load failed", ex); }
            if (cfg == null) cfg = new WidgetConfig();
            cfg.Normalize();
            return cfg;
        }

        // Write to a temp file first so a crash mid-write can't corrupt the settings.
        public static void Save(WidgetConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(cfg));
                File.Copy(tmp, FilePath, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { Log.Write("Config save failed", ex); }
        }
    }

    public static class Log
    {
        public static void Write(string message, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Folder);
                File.AppendAllText(System.IO.Path.Combine(ConfigStore.Folder, "error.log"),
                    DateTime.Now.ToString("s") + "  " + message + (ex != null ? ": " + ex : "") + Environment.NewLine);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Chess.com public API
    // ------------------------------------------------------------------
    public class ChessApiException : Exception
    {
        public ChessApiException(string message) : base(message) { }
    }

    public class PlayerStats
    {
        public bool HasRating;
        public string DisplayUsername;
        public int Rating;
        public int Peak;
        public int Delta;
    }

    public static class ChessApi
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChessRankWidget/3.0 (Windows desktop widget)");
            return client;
        }

        private static JavaScriptSerializer Serializer()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue; // monthly archives of active players exceed the 2 MB default
            return s;
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
        {
            object o;
            return (d != null && d.TryGetValue(key, out o)) ? o as Dictionary<string, object> : null;
        }

        private static async Task<string> GetAsync(string url, bool allowNotFound)
        {
            using (var resp = await Http.GetAsync(url).ConfigureAwait(false))
            {
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    if (allowNotFound) return null;
                    throw new ChessApiException("Player not found on Chess.com");
                }
                if ((int)resp.StatusCode == 429) throw new ChessApiException("Rate-limited by Chess.com, will retry later");
                if (!resp.IsSuccessStatusCode) throw new ChessApiException("Chess.com returned HTTP " + (int)resp.StatusCode);
                return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        public static async Task<PlayerStats> FetchAsync(string username, string timeControl)
        {
            string user = username.Trim().ToLowerInvariant();
            string json = await GetAsync("https://api.chess.com/pub/player/" + Uri.EscapeDataString(user) + "/stats", false).ConfigureAwait(false);
            var root = Serializer().Deserialize<Dictionary<string, object>>(json);

            var result = new PlayerStats();
            var mode = Dict(root, timeControl);
            var last = Dict(mode, "last");
            var best = Dict(mode, "best");
            if (last != null && last.ContainsKey("rating"))
            {
                result.HasRating = true;
                result.Rating = Convert.ToInt32(last["rating"]);
                result.Peak = (best != null && best.ContainsKey("rating")) ? Convert.ToInt32(best["rating"]) : result.Rating;
                if (result.Peak < result.Rating) result.Peak = result.Rating;
                try
                {
                    var deltaInfo = await ComputeDeltaTodayAsync(user, timeControl.Replace("chess_", ""), result.Rating).ConfigureAwait(false);
                    result.Delta = deltaInfo.Item1;
                    result.DisplayUsername = deltaInfo.Item2;
                }
                catch (Exception ex) { Log.Write("Delta computation failed", ex); }
            }
            return result;
        }

        // Real rating change since local midnight: current rating minus the rating
        // after the last rated game played before today in the same time class.
        private static async Task<Tuple<int, string>> ComputeDeltaTodayAsync(string user, string timeClass, int currentRating)
        {
            long startOfToday = ToUnix(DateTime.Now.Date.ToUniversalTime());
            DateTime nowUtc = DateTime.UtcNow;
            DateTime prevMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1).AddMonths(-1);

            var current = await GetRatedGamesAsync(user, timeClass, nowUtc.Year, nowUtc.Month).ConfigureAwait(false);
            string displayUser = current.Item2;
            var games = new List<long[]>();
            if (current.Item1.Count == 0 || current.Item1[0][0] >= startOfToday)
            {
                var prev = await GetRatedGamesAsync(user, timeClass, prevMonth.Year, prevMonth.Month).ConfigureAwait(false);
                games.AddRange(prev.Item1);
                if (string.IsNullOrEmpty(displayUser)) displayUser = prev.Item2;
            }
            games.AddRange(current.Item1);

            long? baseline = null;
            bool playedToday = false;
            foreach (var g in games)
            {
                if (g[0] >= startOfToday) playedToday = true;
                else baseline = g[1];
            }
            int delta = (!playedToday || !baseline.HasValue) ? 0 : (int)(currentRating - baseline.Value);
            return Tuple.Create(delta, displayUser);
        }

        // Returns ([endTimeUnix, playerRatingAfterGame] sorted by end time, canonicalDisplayUsername).
        private static async Task<Tuple<List<long[]>, string>> GetRatedGamesAsync(string user, string timeClass, int year, int month)
        {
            var list = new List<long[]>();
            string displayUser = null;
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.chess.com/pub/player/{0}/games/{1}/{2:D2}", Uri.EscapeDataString(user), year, month);
            string json = await GetAsync(url, true).ConfigureAwait(false);
            if (json == null) return Tuple.Create(list, displayUser);

            var root = Serializer().Deserialize<Dictionary<string, object>>(json);
            object gamesObj;
            if (root == null || !root.TryGetValue("games", out gamesObj) || !(gamesObj is IEnumerable)) return Tuple.Create(list, displayUser);

            foreach (object o in (IEnumerable)gamesObj)
            {
                var g = o as Dictionary<string, object>;
                if (g == null) continue;
                var white = Dict(g, "white");
                var black = Dict(g, "black");
                Dictionary<string, object> me = null;
                if (white != null && string.Equals(Str(white, "username"), user, StringComparison.OrdinalIgnoreCase)) me = white;
                else if (black != null && string.Equals(Str(black, "username"), user, StringComparison.OrdinalIgnoreCase)) me = black;
                if (me != null && string.IsNullOrEmpty(displayUser)) displayUser = Str(me, "username");

                if (!string.Equals(Str(g, "time_class"), timeClass, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(Str(g, "rules"), "chess", StringComparison.OrdinalIgnoreCase)) continue;
                object rated;
                if (g.TryGetValue("rated", out rated) && rated is bool && !(bool)rated) continue;
                if (me == null || !me.ContainsKey("rating") || !g.ContainsKey("end_time")) continue;

                list.Add(new long[] { Convert.ToInt64(g["end_time"]), Convert.ToInt64(me["rating"]) });
            }
            list.Sort((a, b) => a[0].CompareTo(b[0]));
            return Tuple.Create(list, displayUser);
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            return (d.TryGetValue(key, out o) && o != null) ? o.ToString() : "";
        }

        private static long ToUnix(DateTime utc)
        {
            return (long)(utc - Epoch).TotalSeconds;
        }
    }

    // ------------------------------------------------------------------
    // Win32 interop for desktop pinning
    // ------------------------------------------------------------------
    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int GWL_HWNDPARENT = -8;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WM_WINDOWPOSCHANGING = 0x0046;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));
        }

        public static void SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, index, value);
            else SetWindowLong32(hWnd, index, value.ToInt32());
        }
    }

    // ------------------------------------------------------------------
    // Theme
    // ------------------------------------------------------------------
    internal static class Theme
    {
        public static SolidColorBrush Brush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static readonly SolidColorBrush Card = Brush("#202220");
        public static readonly SolidColorBrush CardBorder = Brush("#4A4B46");
        public static readonly SolidColorBrush Text = Brush("#F5F7F5");
        public static readonly SolidColorBrush Muted = Brush("#A6A8A2");
        public static readonly SolidColorBrush Dim = Brush("#80A6A8A2");
        public static readonly SolidColorBrush Green = Brush("#82CB43");
        public static readonly SolidColorBrush Ok = Brush("#52C41A");
        public static readonly SolidColorBrush Busy = Brush("#FAAD14");
        public static readonly SolidColorBrush Error = Brush("#FF5A4E");
        public static readonly SolidColorBrush DialogBg = Brush("#1C1D1B");
        public static readonly SolidColorBrush InputBg = Brush("#272926");
        public static readonly SolidColorBrush InputBorder = Brush("#3E413B");
    }

    internal enum SyncState { Ok, Busy, Error }

    // ------------------------------------------------------------------
    // The widget
    // ------------------------------------------------------------------
    public class MainWindow : Window
    {
        private const double BaseW = 420, BaseH = 190, ShadowPad = 12, Radius = 22;

        private readonly WidgetConfig config;
        private readonly DispatcherTimer refreshTimer = new DispatcherTimer();
        private readonly ScaleTransform scale = new ScaleTransform(1, 1);
        private IntPtr hwnd = IntPtr.Zero;
        private bool isFetching;
        private WinForms.NotifyIcon tray;
        private WinForms.ToolStripMenuItem trayLockItem;
        private MenuItem menuLockItem;
        private SettingsWindow settingsWindow;

        private Grid cardHost;
        private TextBlock txtUsername, txtMode, txtRating, txtDelta, txtPeak, txtGoal, txtRemaining;
        private Ellipse statusDot;
        private Border bubble;
        private Polygon bubbleArrow;
        private FrameworkElement bubbleGroup;
        private ColumnDefinition fillCol, restCol;

        public WidgetConfig Config { get { return config; } }

        public MainWindow()
        {
            config = ConfigStore.Load();

            Title = "ChessRank Widget";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Theme.Font;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            Content = BuildUI();
            ContextMenu = BuildContextMenu();
            ApplyVisualSettings();
            PlaceWindow();
            Render();

            SourceInitialized += OnSourceInitialized;
            Loaded += (s, e) => RefreshNow(false);
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
            MouseLeftButtonDown += OnDragStart;
            Closed += (s, e) => DisposeTray();

            refreshTimer.Tick += (s, e) => RefreshNow(false);
            SetupTimer();
            CreateTray();
        }

        // ---------------- Native widget behavior ----------------
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;

            // Tool window: no taskbar button, not listed in Alt+Tab.
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex = (ex | Native.WS_EX_TOOLWINDOW) & ~(long)Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            ApplyLayer();
        }

        private IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_WINDOWPOSCHANGING && config.Layer == "desktop")
            {
                // Keep the widget glued to the desktop: never let it rise above other windows.
                var wp = (Native.WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(Native.WINDOWPOS));
                if ((wp.flags & Native.SWP_NOZORDER) == 0)
                {
                    wp.hwndInsertAfter = Native.HWND_BOTTOM;
                    Marshal.StructureToPtr(wp, lParam, false);
                }
            }
            else if (msg == Native.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == Native.SC_MINIMIZE)
            {
                handled = true; // widgets don't minimize
            }
            return IntPtr.Zero;
        }

        private void ApplyLayer()
        {
            if (hwnd == IntPtr.Zero) return;
            if (config.Layer == "desktop")
            {
                Topmost = false;
                // Owning the window by the desktop (Progman) keeps it visible on "Show desktop".
                Native.SetWindowLongPtr(hwnd, Native.GWL_HWNDPARENT, Native.FindWindow("Progman", null));
                Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else
            {
                Native.SetWindowLongPtr(hwnd, Native.GWL_HWNDPARENT, IntPtr.Zero);
                Topmost = config.Layer == "topmost";
            }
        }

        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (config.LockPosition || e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { return; }
            config.WindowX = Left;
            config.WindowY = Top;
            config.HasPosition = true;
            ConfigStore.Save(config);
        }

        private void PlaceWindow()
        {
            double w = (BaseW + 2 * ShadowPad) * config.Scale;
            double h = (BaseH + 2 * ShadowPad) * config.Scale;
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;

            bool onScreen = config.HasPosition &&
                            config.WindowX > vl - w / 2 && config.WindowX < vr - w / 2 &&
                            config.WindowY > vt - h / 2 && config.WindowY < vb - h / 2;
            if (onScreen)
            {
                Left = config.WindowX;
                Top = config.WindowY;
            }
            else
            {
                Rect wa = SystemParameters.WorkArea;
                Left = wa.Right - w - 8;
                Top = wa.Top + 8;
            }
        }

        private void ResetPosition()
        {
            config.HasPosition = false;
            PlaceWindow();
            ConfigStore.Save(config);
        }

        private void ApplyVisualSettings()
        {
            scale.ScaleX = scale.ScaleY = config.Scale;
            Opacity = config.Opacity;
            ApplyLayer();
            if (menuLockItem != null) menuLockItem.IsChecked = config.LockPosition;
            if (trayLockItem != null) trayLockItem.Checked = config.LockPosition;
        }

        // ---------------- UI ----------------
        private FrameworkElement BuildUI()
        {
            var shadow = new Border
            {
                Width = BaseW, Height = BaseH,
                CornerRadius = new CornerRadius(Radius),
                Background = Theme.Card,
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.6, Color = Colors.Black }
            };

            var card = new Border
            {
                Width = BaseW, Height = BaseH,
                CornerRadius = new CornerRadius(Radius),
                Background = Theme.Card,
                BorderBrush = Theme.CardBorder,
                BorderThickness = new Thickness(1.5)
            };

            var inner = new Grid
            {
                Clip = new RectangleGeometry(new Rect(0, 0, BaseW - 3, BaseH - 3), Radius - 1.5, Radius - 1.5)
            };
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Faint chessboard squares fading in toward the right edge (as in the mockup).
            var squares = new DrawingGroup();
            squares.Children.Add(new GeometryDrawing(Theme.Brush("#0DFFFFFF"), null, new RectangleGeometry(new Rect(0, 0, 36, 36))));
            squares.Children.Add(new GeometryDrawing(Theme.Brush("#0DFFFFFF"), null, new RectangleGeometry(new Rect(36, 36, 36, 36))));
            var board = new Rectangle
            {
                Fill = new DrawingBrush(squares)
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 72, 72), ViewportUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, 72, 72), ViewboxUnits = BrushMappingMode.Absolute
                },
                OpacityMask = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), 0.0),
                IsHitTestVisible = false
            };
            Grid.SetColumn(board, 1);
            inner.Children.Add(board);

            var knight = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(knight, BitmapScalingMode.HighQuality);
            string asset = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "card_left.png");
            if (File.Exists(asset))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(asset, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                bi.Freeze();
                knight.Source = bi;
            }
            inner.Children.Add(knight);

            var stats = BuildStatsPanel();
            Grid.SetColumn(stats, 1);
            inner.Children.Add(stats);

            card.Child = inner;

            cardHost = new Grid { Margin = new Thickness(ShadowPad) };
            cardHost.Children.Add(shadow);
            cardHost.Children.Add(card);
            ToolTipService.SetInitialShowDelay(cardHost, 600);

            var root = new Grid { LayoutTransform = scale };
            root.Children.Add(cardHost);
            return root;
        }

        private FrameworkElement BuildStatsPanel()
        {
            var panel = new StackPanel { Margin = new Thickness(4, 12, 18, 10) };

            // Header: username, status dot, mode badge, settings gear
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            txtUsername = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Theme.Text,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(txtUsername);

            statusDot = new Ellipse { Width = 7, Height = 7, Fill = Theme.Ok, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(statusDot, 1);
            header.Children.Add(statusDot);

            txtMode = new TextBlock { FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("#C9CBBF") };
            var modeBadge = new Border
            {
                Background = Theme.Brush("#1CFFFFFF"), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Child = txtMode
            };
            Grid.SetColumn(modeBadge, 2);
            header.Children.Add(modeBadge);

            var gear = new TextBlock
            {
                Text = "\u2699", FontSize = 14, Foreground = Theme.Dim, Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Settings"
            };
            gear.MouseLeftButtonDown += (s, e) => { e.Handled = true; OpenSettings(); };
            gear.MouseEnter += (s, e) => gear.Foreground = Theme.Text;
            gear.MouseLeave += (s, e) => gear.Foreground = Theme.Dim;
            Grid.SetColumn(gear, 3);
            header.Children.Add(gear);
            panel.Children.Add(header);

            // Hero rating + delta bubble
            var ratingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -2, 0, 6) };
            txtRating = new TextBlock
            {
                FontSize = 52, FontWeight = FontWeights.Black, Foreground = Theme.Green,
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Open Chess.com profile",
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black }
            };
            txtRating.MouseLeftButtonDown += (s, e) => { e.Handled = true; OpenProfile(); };
            ratingRow.Children.Add(txtRating);

            var bubblePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            bubbleArrow = new Polygon { Points = new PointCollection { new Point(7, 0), new Point(0, 6), new Point(7, 12) }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -1, 0) };
            bubble = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3, 9, 4) };
            var bubbleStack = new StackPanel();
            txtDelta = new TextBlock { FontSize = 16, FontWeight = FontWeights.Black, Foreground = Theme.Brush("#EFF8DB"), HorizontalAlignment = HorizontalAlignment.Center };
            bubbleStack.Children.Add(txtDelta);
            bubbleStack.Children.Add(new TextBlock { Text = "Today", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("#D0E8C4"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
            bubble.Child = bubbleStack;
            bubblePanel.Children.Add(bubbleArrow);
            bubblePanel.Children.Add(bubble);
            bubbleGroup = bubblePanel;
            ratingRow.Children.Add(bubblePanel);
            panel.Children.Add(ratingRow);

            // Peak pill
            var peakStack = new StackPanel { Orientation = Orientation.Horizontal };
            peakStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 7, 0),
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#FDB62F"), (Color)ColorConverter.ConvertFromString("#EA810C"), 90.0),
                Child = new TextBlock { Text = "PEAK", FontSize = 10, FontWeight = FontWeights.Black, Foreground = Theme.Brush("#241400") }
            });
            txtPeak = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            peakStack.Children.Add(txtPeak);
            peakStack.Children.Add(new TextBlock { Text = "(Historical Max)", FontSize = 11, Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new Border
            {
                Background = Theme.Brush("#21221F"), BorderBrush = Theme.Brush("#4D4D46"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(2, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 7), Child = peakStack
            });

            // Goal progress (star-sized columns -> scales correctly at any widget size)
            var track = new Border
            {
                Height = 10, CornerRadius = new CornerRadius(5),
                Background = Theme.Brush("#262923"), BorderBrush = Theme.Brush("#383D34"), BorderThickness = new Thickness(1)
            };
            var trackGrid = new Grid();
            fillCol = new ColumnDefinition();
            restCol = new ColumnDefinition();
            trackGrid.ColumnDefinitions.Add(fillCol);
            trackGrid.ColumnDefinitions.Add(restCol);
            trackGrid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#689E3A"), (Color)ColorConverter.ConvertFromString("#86CD47"), 0.0)
            });
            track.Child = trackGrid;
            panel.Children.Add(track);

            var labels = new Grid { Margin = new Thickness(1, 3, 1, 0) };
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            txtGoal = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Muted };
            txtRemaining = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Theme.Muted };
            Grid.SetColumn(txtRemaining, 1);
            labels.Children.Add(txtGoal);
            labels.Children.Add(txtRemaining);
            panel.Children.Add(labels);

            return panel;
        }

        private static string ModeLabel(string tc)
        {
            switch (tc)
            {
                case "chess_blitz": return "BLITZ";
                case "chess_bullet": return "BULLET";
                case "chess_daily": return "DAILY";
                default: return "RAPID";
            }
        }

        private void Render()
        {
            bool has = config.LastRating > 0;
            txtUsername.Text = config.Username;
            txtMode.Text = ModeLabel(config.TimeControl);
            txtRating.Text = has ? config.LastRating.ToString(CultureInfo.InvariantCulture) : "\u2014";
            txtPeak.Text = config.LastPeak > 0 ? config.LastPeak.ToString(CultureInfo.InvariantCulture) : "\u2014";

            bubbleGroup.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            string bg, border;
            if (config.LastDelta > 0) { txtDelta.Text = "+" + config.LastDelta; bg = "#5C7D37"; border = "#6D9342"; }
            else if (config.LastDelta < 0) { txtDelta.Text = "\u2212" + Math.Abs(config.LastDelta); bg = "#7A3030"; border = "#94403C"; }
            else { txtDelta.Text = "0"; bg = "#3F473D"; border = "#555E52"; }
            bubble.Background = Theme.Brush(bg);
            bubble.BorderBrush = Theme.Brush(border);
            bubbleArrow.Fill = Theme.Brush(border);

            int target = config.TargetRank;
            int baseline = config.EffectiveBaseline;
            int rating = config.LastRating;
            double pct = !has ? 0 : rating >= target ? 100 : rating <= baseline ? 0 : 100.0 * (rating - baseline) / (target - baseline);
            double shown = has ? Math.Max(pct, 2) : 0;
            fillCol.Width = new GridLength(shown, GridUnitType.Star);
            restCol.Width = new GridLength(100 - shown, GridUnitType.Star);

            txtGoal.Text = "Goal " + target;
            if (has && rating >= target)
            {
                txtRemaining.Text = "Goal reached";
                txtRemaining.Foreground = Theme.Green;
            }
            else
            {
                txtRemaining.Text = has ? string.Format(CultureInfo.InvariantCulture, "{0} to go", target - rating) : "No rating yet";
                txtRemaining.Foreground = Theme.Muted;
            }
            UpdateTrayText();
        }

        private void SetStatus(SyncState state, string message)
        {
            statusDot.Fill = state == SyncState.Ok ? Theme.Ok : state == SyncState.Busy ? Theme.Busy : Theme.Error;
            cardHost.ToolTip = message;
            statusDot.ToolTip = message;
        }

        // ---------------- Data refresh ----------------
        public void SetupTimer()
        {
            refreshTimer.Stop();
            if (config.RefreshInterval > 0)
            {
                refreshTimer.Interval = TimeSpan.FromSeconds(config.RefreshInterval);
                refreshTimer.Start();
            }
        }

        public async void RefreshNow(bool notifyErrors)
        {
            if (config.IsDemo)
            {
                Render();
                SetStatus(SyncState.Ok, "Demo mode \u2014 right-click \u203A Settings to enter your Chess.com username");
                return;
            }
            if (isFetching) return;

            isFetching = true;
            string user = config.Username;
            string tc = config.TimeControl;
            SetStatus(SyncState.Busy, "Updating\u2026");
            txtRating.Opacity = 0.6;
            try
            {
                PlayerStats s = await ChessApi.FetchAsync(user, tc);
                if (user != config.Username || tc != config.TimeControl) return; // settings changed meanwhile

                if (!s.HasRating)
                {
                    config.LastRating = config.LastPeak = config.LastDelta = 0;
                    SetStatus(SyncState.Error, "No " + ModeLabel(tc).ToLowerInvariant() + " rating for this player");
                }
                else
                {
                    int previous = config.LastRating;
                    if (config.SoundEnabled && previous > 0)
                    {
                        if (previous < config.TargetRank && s.Rating >= config.TargetRank) System.Media.SystemSounds.Exclamation.Play();
                        else if (s.Rating > previous) System.Media.SystemSounds.Asterisk.Play();
                    }
                    if (!string.IsNullOrEmpty(s.DisplayUsername) && string.Equals(config.Username, s.DisplayUsername, StringComparison.OrdinalIgnoreCase))
                        config.Username = s.DisplayUsername;
                    config.LastRating = s.Rating;
                    config.LastPeak = s.Peak;
                    config.LastDelta = s.Delta;
                    config.LastSynced = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                    SetStatus(SyncState.Ok, "Last updated " + DateTime.Now.ToString("t"));
                }
                ConfigStore.Save(config);
                Render();
            }
            catch (Exception ex)
            {
                string msg = ex is ChessApiException ? ex.Message : "Offline \u2014 could not reach Chess.com";
                if (!(ex is ChessApiException)) Log.Write("Refresh failed", ex);
                SetStatus(SyncState.Error, msg + " (showing last known values)");
                if (notifyErrors && tray != null) tray.ShowBalloonTip(4000, "ChessRank Widget", msg, WinForms.ToolTipIcon.Warning);
            }
            finally
            {
                isFetching = false;
                txtRating.Opacity = 1.0;
            }
        }

        // ---------------- Settings ----------------
        public void OpenSettings()
        {
            if (settingsWindow != null)
            {
                settingsWindow.Activate();
                return;
            }
            settingsWindow = new SettingsWindow(this, config);
            settingsWindow.Closed += (s, e) => settingsWindow = null;
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        public void OnSettingsSaved(string previousUser, string previousMode)
        {
            bool identityChanged = !string.Equals(previousUser, config.Username, StringComparison.OrdinalIgnoreCase) || previousMode != config.TimeControl;
            if (identityChanged)
            {
                if (config.IsDemo) config.LoadDemoValues();
                else { config.LastRating = config.LastPeak = config.LastDelta = 0; config.LastSynced = null; }
            }
            Startup.Apply(config.StartWithWindows);
            ApplyVisualSettings();
            PlaceWindow();
            SetupTimer();
            ConfigStore.Save(config);
            Render();
            RefreshNow(true);
        }

        private void ToggleLock()
        {
            config.LockPosition = !config.LockPosition;
            ApplyVisualSettings();
            ConfigStore.Save(config);
        }

        private void ResetDemo()
        {
            config.Username = WidgetConfig.DemoUser;
            config.TimeControl = "chess_rapid";
            config.TargetRank = 2200;
            config.BaselineRank = 2050;
            config.LoadDemoValues();
            ConfigStore.Save(config);
            Render();
            RefreshNow(false);
        }

        private void OpenProfile()
        {
            try
            {
                Process.Start(config.IsDemo ? "https://www.chess.com" : "https://www.chess.com/member/" + Uri.EscapeDataString(config.Username));
            }
            catch (Exception ex) { Log.Write("Open profile failed", ex); }
        }

        private void ExitApp()
        {
            ConfigStore.Save(config);
            DisposeTray();
            Application.Current.Shutdown();
        }

        // ---------------- Menus & tray ----------------
        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItemFor("Settings\u2026", OpenSettings));
            menu.Items.Add(MenuItemFor("Refresh now", () => RefreshNow(true)));
            menu.Items.Add(MenuItemFor("Open Chess.com profile", OpenProfile));
            menu.Items.Add(new Separator());
            menuLockItem = new MenuItem { Header = "Lock position", IsCheckable = true, IsChecked = config.LockPosition };
            menuLockItem.Click += (s, e) => ToggleLock();
            menu.Items.Add(menuLockItem);
            menu.Items.Add(MenuItemFor("Reset position", ResetPosition));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Exit", ExitApp));
            return menu;
        }

        private static MenuItem MenuItemFor(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            return item;
        }

        private void CreateTray()
        {
            tray = new WinForms.NotifyIcon();
            string ico = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
            try { tray.Icon = File.Exists(ico) ? new System.Drawing.Icon(ico) : System.Drawing.SystemIcons.Application; }
            catch { tray.Icon = System.Drawing.SystemIcons.Application; }

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Settings\u2026", null, (s, e) => OpenSettings());
            menu.Items.Add("Refresh now", null, (s, e) => RefreshNow(true));
            menu.Items.Add("Open Chess.com profile", null, (s, e) => OpenProfile());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            trayLockItem = new WinForms.ToolStripMenuItem("Lock position", null, (s, e) => ToggleLock());
            trayLockItem.Checked = config.LockPosition;
            menu.Items.Add(trayLockItem);
            menu.Items.Add("Reset position", null, (s, e) => ResetPosition());
            menu.Items.Add("Reset to demo", null, (s, e) => ResetDemo());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => OpenSettings();
            tray.Visible = true;
            UpdateTrayText();
        }

        private void UpdateTrayText()
        {
            if (tray == null) return;
            string text = "ChessRank \u2014 " + config.Username + " " + (config.LastRating > 0 ? config.LastRating.ToString(CultureInfo.InvariantCulture) : "\u2014");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void DisposeTray()
        {
            if (tray == null) return;
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }
    }

    // ------------------------------------------------------------------
    // Start with Windows (per-user Run key, no admin needed)
    // ------------------------------------------------------------------
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ChessRankWidget";

        public static void Apply(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    if (enable) key.SetValue(ValueName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex) { Log.Write("Startup registration failed", ex); }
        }
    }

    // ------------------------------------------------------------------
    // Settings window
    // ------------------------------------------------------------------
    public class SettingsWindow : Window
    {
        private static readonly Regex UsernamePattern = new Regex("^[A-Za-z0-9_-]{2,25}$");

        private readonly MainWindow widget;
        private readonly WidgetConfig cfg;
        private readonly TextBox txtUser, txtTarget, txtBaseline;
        private readonly ComboBox cmbMode, cmbRefresh, cmbSize, cmbLayer, cmbOpacity;
        private readonly CheckBox chkLock, chkSound, chkStartup;
        private readonly TextBlock txtError;

        public SettingsWindow(MainWindow widget, WidgetConfig cfg)
        {
            this.widget = widget;
            this.cfg = cfg;

            Title = "ChessRank Widget \u2014 Settings";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = Theme.DialogBg;
            Foreground = Theme.Text;
            FontFamily = Theme.Font;
            FontSize = 12;
            string ico = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
            if (File.Exists(ico)) { try { Icon = BitmapFrame.Create(new Uri(ico)); } catch { } }

            var body = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };
            body.Children.Add(new TextBlock { Text = "Widget settings", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Theme.Green, Margin = new Thickness(0, 0, 0, 6) });

            body.Children.Add(Section("PLAYER"));
            txtUser = Input(cfg.Username);
            body.Children.Add(Field("Chess.com username", txtUser));
            cmbMode = Combo(cfg.TimeControl, "Rapid", "chess_rapid", "Blitz", "chess_blitz", "Bullet", "chess_bullet", "Daily", "chess_daily");
            cmbRefresh = Combo(cfg.RefreshInterval.ToString(CultureInfo.InvariantCulture),
                "Every 1 minute", "60", "Every 5 minutes", "300", "Every 15 minutes", "900", "Every 30 minutes", "1800", "Every hour", "3600", "Manual only", "0");
            body.Children.Add(TwoColumns(Field("Rating type", cmbMode), Field("Auto-refresh", cmbRefresh)));

            body.Children.Add(Section("GOAL"));
            txtTarget = Input(cfg.TargetRank.ToString(CultureInfo.InvariantCulture));
            txtBaseline = Input(cfg.BaselineRank >= 0 ? cfg.BaselineRank.ToString(CultureInfo.InvariantCulture) : "");
            body.Children.Add(TwoColumns(Field("Target rating", txtTarget), Field("Bar starts at (blank = auto)", txtBaseline)));

            body.Children.Add(Section("APPEARANCE"));
            cmbSize = Combo(cfg.Scale.ToString("0.##", CultureInfo.InvariantCulture),
                "25%", "0.25", "30%", "0.3", "40%", "0.4", "50%", "0.5", "60%", "0.6", "75%", "0.75", "90%", "0.9", "100%", "1", "125%", "1.25");
            cmbOpacity = Combo(cfg.Opacity.ToString("0.##", CultureInfo.InvariantCulture),
                "100%", "1", "90%", "0.9", "80%", "0.8", "70%", "0.7", "60%", "0.6");
            body.Children.Add(TwoColumns(Field("Size", cmbSize), Field("Opacity", cmbOpacity)));
            cmbLayer = Combo(cfg.Layer, "Pinned to desktop (widget)", "desktop", "Always on top", "topmost", "Normal window layer", "normal");
            body.Children.Add(Field("Placement", cmbLayer));

            chkLock = Check("Lock position (prevent accidental dragging)", cfg.LockPosition);
            chkStartup = Check("Start with Windows", cfg.StartWithWindows);
            chkSound = Check("Play a sound when rating goes up or the goal is reached", cfg.SoundEnabled);
            body.Children.Add(chkLock);
            body.Children.Add(chkStartup);
            body.Children.Add(chkSound);

            txtError = new TextBlock { Foreground = Theme.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            body.Children.Add(txtError);

            var cancel = FlatButton("Cancel", "#333631", "#484C45", Theme.Text);
            cancel.Click += (s, e) => Close();
            var save = FlatButton("Save", "#5BA72B", "#5BA72B", Brushes.White);
            save.IsDefault = true;
            save.Click += (s, e) => Save();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            cancel.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);

            var footer = new Border
            {
                Background = Theme.Brush("#141513"), BorderBrush = Theme.Brush("#2D2F2B"), BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 12, 20, 12), Child = buttons
            };

            var root = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            root.Children.Add(body);
            Content = root;

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (s, e) => { txtUser.Focus(); txtUser.SelectAll(); };
        }

        private void Save()
        {
            string user = txtUser.Text.Trim();
            if (user.Length == 0) user = WidgetConfig.DemoUser;
            if (!string.Equals(user, WidgetConfig.DemoUser, StringComparison.OrdinalIgnoreCase) && !UsernamePattern.IsMatch(user))
            {
                ShowError("Username can only contain letters, digits, '_' and '-' (2\u201325 characters).");
                return;
            }

            int target;
            if (!int.TryParse(txtTarget.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out target) || target < 100 || target > 4000)
            {
                ShowError("Target rating must be a number between 100 and 4000.");
                return;
            }

            int baseline = -1;
            string b = txtBaseline.Text.Trim();
            if (b.Length > 0 && (!int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out baseline) || baseline < 0 || baseline >= target))
            {
                ShowError("Bar start must be blank or a number from 0 up to (but below) the target.");
                return;
            }

            string previousUser = cfg.Username, previousMode = cfg.TimeControl;
            cfg.Username = user;
            cfg.TimeControl = SelectedTag(cmbMode);
            cfg.RefreshInterval = int.Parse(SelectedTag(cmbRefresh), CultureInfo.InvariantCulture);
            cfg.TargetRank = target;
            cfg.BaselineRank = baseline;
            cfg.Scale = double.Parse(SelectedTag(cmbSize), CultureInfo.InvariantCulture);
            cfg.Opacity = double.Parse(SelectedTag(cmbOpacity), CultureInfo.InvariantCulture);
            cfg.Layer = SelectedTag(cmbLayer);
            cfg.LockPosition = chkLock.IsChecked == true;
            cfg.StartWithWindows = chkStartup.IsChecked == true;
            cfg.SoundEnabled = chkSound.IsChecked == true;
            cfg.Normalize();

            Close();
            widget.OnSettingsSaved(previousUser, previousMode);
        }

        private void ShowError(string message)
        {
            txtError.Text = message;
            txtError.Visibility = Visibility.Visible;
        }

        // ---- small UI helpers ----
        private static TextBlock Section(string text)
        {
            return new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Theme.Green, Margin = new Thickness(0, 12, 0, 4) };
        }

        private static FrameworkElement Field(string label, Control control)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Theme.Muted, Margin = new Thickness(0, 0, 0, 3) });
            sp.Children.Add(control);
            return sp;
        }

        private static FrameworkElement TwoColumns(FrameworkElement left, FrameworkElement right)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(right, 2);
            g.Children.Add(left);
            g.Children.Add(right);
            return g;
        }

        private static TextBox Input(string text)
        {
            return new TextBox
            {
                Text = text, Padding = new Thickness(6, 5, 6, 5), FontSize = 13,
                Background = Theme.InputBg, Foreground = Theme.Text, BorderBrush = Theme.InputBorder, CaretBrush = Theme.Text
            };
        }

        private static ComboBox Combo(string selectedTag, params string[] labelTagPairs)
        {
            var cb = new ComboBox { Height = 28, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
            int selected = -1;
            for (int i = 0; i + 1 < labelTagPairs.Length; i += 2)
            {
                cb.Items.Add(new ComboBoxItem { Content = labelTagPairs[i], Tag = labelTagPairs[i + 1] });
                if (labelTagPairs[i + 1] == selectedTag) selected = i / 2;
            }
            cb.SelectedIndex = selected >= 0 ? selected : 0;
            return cb;
        }

        private static string SelectedTag(ComboBox cb)
        {
            var item = cb.SelectedItem as ComboBoxItem;
            return item != null ? (string)item.Tag : "";
        }

        private static CheckBox Check(string text, bool value)
        {
            return new CheckBox { Content = text, IsChecked = value, Foreground = Theme.Text, Margin = new Thickness(0, 3, 0, 3) };
        }

        private static Button FlatButton(string text, string bg, string border, Brush fg)
        {
            var template = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            template.VisualTree = bd;

            var btn = new Button
            {
                Content = text, Template = template, Width = 96, Height = 32, Cursor = Cursors.Hand,
                Background = Theme.Brush(bg), BorderBrush = Theme.Brush(border), Foreground = fg, FontWeight = FontWeights.SemiBold
            };
            btn.MouseEnter += (s, e) => btn.Opacity = 0.85;
            btn.MouseLeave += (s, e) => btn.Opacity = 1.0;
            return btn;
        }
    }

    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool firstInstance;
            using (var mutex = new Mutex(true, "Local\\ChessRankWidget.SingleInstance", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("ChessRank Widget is already running.\nRight-click the widget or its tray icon for options.",
                        "ChessRank Widget", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
                }
                catch (NotSupportedException)
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2 only on older systems
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += (s, e) => { Log.Write("Unhandled UI exception", e.Exception); e.Handled = true; };
                app.Run(new MainWindow());
                GC.KeepAlive(mutex);
            }
        }
    }
}
