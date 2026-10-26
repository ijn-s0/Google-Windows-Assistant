using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GeminiLauncher
{
    public partial class MainWindow : Window
    {
        App AppRef => (App)Application.Current;
        Settings S => AppRef.Settings;

        readonly ObservableCollection<ResultItem> _results = new ObservableCollection<ResultItem>();
        int _ver;
        CancellationTokenSource _cts, _answerCts;
        string _q = "";
        bool _userNavigated;
        List<string> _sugs = new List<string>();
        List<FileHit> _files = new List<FileHit>();
        List<(AppEntry app, double score)> _apps = new List<(AppEntry, double)>();
        List<((string name, string keywords, string uri) page, double score)> _pages = new List<((string, string, string), double)>();
        string _calc, _url;
        GeminiAnswer _bound;
        int _renderQueued;
        readonly DispatcherTimer _instantTimer;
        public bool SuppressHide;

        public MainWindow()
        {
            InitializeComponent();
            ResultsList.ItemsSource = _results;
            SearchBox.TextChanged += (s, e) => OnQueryChanged();
            SearchBox.SelectionChanged += (s, e) => UpdateGhost();
            PreviewKeyDown += OnKey;
            PreviewTextInput += OnTextInput;
            Deactivated += (s, e) => { if (!SuppressHide && IsVisible) AppRef.HideLauncher(); };
            SourceInitialized += (s, e) =>
            {
                // Remove the system menu so DWM doesn't draw a caption close button on the glass frame.
                var h = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(h, -16 /*GWL_STYLE*/, Native.GetWindowLong(h, -16) & ~0x00080000 /*WS_SYSMENU*/);
                Theme.ApplyBackdrop(this);
            };
            Theme.Changed += () => { Theme.ApplyBackdrop(this); if (_bound != null) RenderAnswer(); };
            _instantTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _instantTimer.Tick += (s, e) => { _instantTimer.Stop(); MaybeInstantAnswer(); };
            AppIndex.Updated += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                AppRef.EnsureDefaultPins();
                if (IsVisible && HomeView.Visibility == Visibility.Visible) RefreshHome();
            }));
            UpdateModelBadge();
        }

        public void UpdateModelBadge()
        {
            ModelBadge.Text = string.IsNullOrWhiteSpace(S.ApiKey) ? "Gemini not connected" : S.Model;
        }

        // ======================= show / position =======================

        public void PrepareForShow()
        {
            _answerCts?.Cancel();
            Unbind();
            SearchBox.Text = "";
            ShowHome();
            RefreshHome();
            Position();
        }

        public void Position()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.GetCursorPos(out var pt);
            var scr = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pt.x, pt.y));
            var wa = scr.WorkingArea;
            double scale = 1;
            try
            {
                var mon = Native.MonitorFromPoint(pt, 2);
                if (Native.GetDpiForMonitor(mon, 0, out uint dx, out uint dy) == 0) scale = dx / 96.0;
            }
            catch { }
            int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
            if (Native.GetWindowRect(hwnd, out var r) && IsVisible) { w = r.right - r.left; h = r.bottom - r.top; }
            int margin = (int)(12 * scale);
            int x = wa.Left + (wa.Width - w) / 2;
            int y = wa.Bottom - h - margin;
            if (wa.Top > scr.Bounds.Top) y = wa.Top + margin;          // taskbar at top
            if (y < wa.Top) y = wa.Top;
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public void AnimateIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }

        public void SetQuery(string q)
        {
            SearchBox.Text = q ?? "";
            FocusSearch();
        }

        public void FocusSearch()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        }

        // ======================= HOME =======================

        void ShowHome()
        {
            HomeView.Visibility = Visibility.Visible;
            ResultsView.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            ClearButton.Visibility = Visibility.Collapsed;
            GhostTyped.Text = GhostRest.Text = "";
            FooterHint.Text = "Type to search apps, files and the web  ·  Enter to open  ·  Esc to close";
            HomeView.ScrollToTop();
        }

        void ShowResults()
        {
            HomeView.Visibility = Visibility.Collapsed;
            ResultsView.Visibility = Visibility.Visible;
            Placeholder.Visibility = Visibility.Collapsed;
            ClearButton.Visibility = Visibility.Visible;
            FooterHint.Text = "↑↓ move  ·  Enter open  ·  Tab complete  ·  Ctrl+Enter Google  ·  Right-click for more";
        }

        public void RefreshHome()
        {
            // Pinned
            var pins = new List<ResultItem>();
            foreach (var p in S.Pins)
            {
                var it = FromPin(p);
                if (it != null) pins.Add(it);
            }
            PinsList.ItemsSource = pins;
            NoPins.Visibility = pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Recommended: frequent apps + recent files
            var rec = new ObservableCollection<ResultItem>();
            foreach (var a in AppIndex.Frequent(S, 3))
            {
                var it = MakeApp(a, 0);
                it.Subtitle = "Frequently used";
                rec.Add(it);
            }
            RecommendedList.ItemsSource = rec;
            Task.Run(() => FileSearch.Recent(6)).ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                foreach (var f in t.Result)
                {
                    var it = new ResultItem { Kind = ItemKind.File, Title = f.Name, Target = f.Path, Subtitle = FileSearch.Ago(f.Modified), When = f.Modified };
                    IconLoader.Request(it);
                    rec.Add(it);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

            // Searches: recent + trending
            var searches = new ObservableCollection<ResultItem>();
            foreach (var q in S.RecentSearches.Take(3))
                searches.Add(new ResultItem { Kind = ItemKind.Recent, Title = q, Target = q, Subtitle = "Recent search", Glyph = Glyphs.History });
            SearchesList.ItemsSource = searches;
            if (S.ShowTrending)
            {
                var geo = S.EffectiveRegion;
                Trends.GetAsync(geo).ContinueWith(t =>
                {
                    if (t.IsFaulted) return;
                    foreach (var tr in t.Result.Take(8 - searches.Count))
                        searches.Add(new ResultItem { Kind = ItemKind.Trending, Title = tr, Target = tr, Subtitle = "Trending in " + geo, Glyph = Glyphs.Trend });
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        ResultItem FromPin(Pin p)
        {
            ResultItem it;
            switch (p.Kind)
            {
                case "app":
                    var a = AppIndex.FindById(p.Target);
                    it = new ResultItem { Kind = ItemKind.App, Title = p.Name, Target = p.Target, LnkPath = a?.Lnk, Subtitle = p.Name };
                    break;
                case "file":
                    it = new ResultItem { Kind = ItemKind.File, Title = p.Name, Target = p.Target, Subtitle = p.Target }; break;
                case "folder":
                    it = new ResultItem { Kind = ItemKind.Folder, Title = p.Name, Target = p.Target, Subtitle = p.Target }; break;
                case "url":
                    it = new ResultItem { Kind = ItemKind.Url, Title = p.Name, Target = p.Target, Subtitle = p.Target, Glyph = Glyphs.Globe }; break;
                case "setting":
                    it = new ResultItem { Kind = ItemKind.Setting, Title = p.Name, Target = p.Target, Subtitle = "Settings", Glyph = Glyphs.Gear }; break;
                default: return null;
            }
            it.IsPinned = true;
            IconLoader.Request(it);
            return it;
        }

        // ======================= QUERY PIPELINE =======================

        void OnQueryChanged()
        {
            var q = SearchBox.Text ?? "";
            var qt = q.Trim();
            if (qt.Length == 0)
            {
                _ver++;
                _cts?.Cancel();
                _instantTimer.Stop();
                _results.Clear();
                ShowHome();
                return;
            }
            ShowResults();
            _ver++;
            int v = _ver;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _q = qt;
            _userNavigated = false;

            // Keep still-matching async results to avoid flicker while typing
            _sugs = _sugs.Where(x => x.StartsWith(qt, StringComparison.OrdinalIgnoreCase)).ToList();
            var tokens = qt.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            _files = _files.Where(f => tokens.All(t => (f.Name ?? "").ToLowerInvariant().Contains(t))).ToList();

            _calc = Calc.TryEval(qt, out var cr) ? cr : null;
            _url = Launcher.LooksLikeUrl(qt, out var u) ? u : null;
            _apps = AppIndex.Search(qt, S, 6);
            _pages = SettingsPages.Search(qt, 3);
            Rebuild();
            UpdateGhost();

            _ = LoadSuggestions(qt, v, ct);
            _ = LoadFiles(qt, v, ct);

            _instantTimer.Stop();
            _instantTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, S.InstantDelayMs));
            _instantTimer.Start();
        }

        async Task LoadSuggestions(string q, int v, CancellationToken ct)
        {
            try
            {
                await Task.Delay(30, ct);
                var list = await GoogleSuggest.GetAsync(q, ct);
                if (v != _ver) return;
                _sugs = list;
                Rebuild();
                UpdateGhost();
            }
            catch { }
        }

        async Task LoadFiles(string q, int v, CancellationToken ct)
        {
            try
            {
                await Task.Delay(110, ct);
                var hits = await Task.Run(() => FileSearch.Search(q, S.SearchFileContents, 8, ct), ct);
                if (v != _ver) return;
                _files = hits;
                Rebuild();
            }
            catch { }
        }

        ResultItem MakeApp(AppEntry a, double score)
        {
            var it = new ResultItem { Kind = ItemKind.App, Title = a.Name, Target = a.Id, LnkPath = a.Lnk, Score = score, Subtitle = "App" };
            it.IsPinned = S.Pins.Any(p => p.Kind == "app" && string.Equals(p.Target, a.Id, StringComparison.OrdinalIgnoreCase));
            IconLoader.Request(it);
            return it;
        }

        ResultItem MakeFile(FileHit f)
        {
            var dir = System.IO.Path.GetDirectoryName(f.Path) ?? "";
            var sub = (f.ContentMatch ? "Contains match  ·  " : "") + ShortenPath(dir) + (f.Modified != DateTime.MinValue ? "  ·  " + FileSearch.Ago(f.Modified) : "");
            var it = new ResultItem { Kind = f.IsFolder ? ItemKind.Folder : ItemKind.File, Title = f.Name, Target = f.Path, Subtitle = sub, Score = f.Score, When = f.Modified };
            it.IsPinned = S.Pins.Any(p => string.Equals(p.Target, f.Path, StringComparison.OrdinalIgnoreCase));
            IconLoader.Request(it);
            return it;
        }

        static string ShortenPath(string p)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (p.StartsWith(home, StringComparison.OrdinalIgnoreCase)) p = "~" + p.Substring(home.Length);
            return p;
        }

        void Rebuild()
        {
            var prev = ResultsList.SelectedItem as ResultItem;
            string prevKey = prev != null ? prev.Kind + "|" + prev.Target : null;
            var items = new List<ResultItem>();
            ResultItem def = null;
            var q = _q;

            if (_calc != null)
            {
                var c = new ResultItem { Kind = ItemKind.Calc, Title = "= " + _calc, Subtitle = q + "   ·   Enter to copy", Target = _calc, Glyph = Glyphs.Calc };
                items.Add(ResultItem.Header("Calculator")); items.Add(c); def = c;
            }
            if (_url != null)
            {
                var host = _url; try { host = new Uri(_url).Host; } catch { }
                var ui = new ResultItem { Kind = ItemKind.Url, Title = host, Subtitle = "Open website  ·  " + _url, Target = _url, Glyph = Glyphs.Globe };
                ui.IsPinned = S.Pins.Any(p => p.Target == _url);
                IconLoader.Request(ui);
                items.Add(ResultItem.Header("Website")); items.Add(ui); if (def == null) def = ui;
            }

            var appItems = _apps.Select(a => MakeApp(a.app, a.score)).ToList();
            var appNames = new HashSet<string>(appItems.Select(a => a.Title), StringComparer.OrdinalIgnoreCase);
            var pageItems = _pages.Where(p => !appNames.Contains(p.page.name)).Select(p => new ResultItem
            {
                Kind = ItemKind.Setting, Title = p.page.name, Subtitle = "System settings", Target = p.page.uri, Score = p.score, Glyph = Glyphs.Gear,
                IsPinned = S.Pins.Any(x => x.Target == p.page.uri)
            }).ToList();
            var fileItems = _files.Select(MakeFile).ToList();

            // Best match
            var candidates = appItems.Concat(pageItems).Concat(fileItems.Where(f => f.Score >= 80)).OrderByDescending(x => x.Score).ToList();
            var best = candidates.FirstOrDefault();
            if (best != null && best.Score >= 65 && _calc == null)
            {
                items.Add(ResultItem.Header("Best match"));
                items.Add(best);
                if (def == null) def = best;
                appItems.Remove(best); pageItems.Remove(best); fileItems.Remove(best);
            }

            // Web: Gemini + Google suggestions
            items.Add(ResultItem.Header("Search the web"));
            var gem = new ResultItem { Kind = ItemKind.Gemini, Title = q, Target = q, Glyph = Glyphs.Sparkle, Subtitle = "Ask Gemini  ·  answers grounded in Google Search" };
            items.Add(gem);
            if (def == null) def = gem;
            foreach (var s in _sugs.Where(s => !string.Equals(s.Trim(), q, StringComparison.OrdinalIgnoreCase)).Take(6))
                items.Add(new ResultItem { Kind = ItemKind.Suggestion, Title = s, Target = s, Glyph = Glyphs.Search });

            if (appItems.Count > 0) { items.Add(ResultItem.Header("Apps")); items.AddRange(appItems.Take(4)); }
            if (pageItems.Count > 0) { items.Add(ResultItem.Header("Settings")); items.AddRange(pageItems.Take(3)); }
            if (fileItems.Count > 0) { items.Add(ResultItem.Header("Documents & files")); items.AddRange(fileItems.Take(6)); }

            _results.Clear();
            foreach (var it in items) _results.Add(it);

            ResultItem sel = null;
            if (_userNavigated && prevKey != null) sel = items.FirstOrDefault(i => !i.IsHeader && i.Kind + "|" + i.Target == prevKey);
            if (sel == null) { sel = def; _userNavigated = false; }
            ResultsList.SelectedItem = sel;
            if (sel != null && items.IndexOf(sel) <= 2) ResultsList.ScrollIntoView(items[0]);
        }

        ResultItem Selected => ResultsList.SelectedItem as ResultItem;

        void MoveSel(int d)
        {
            if (_results.Count == 0) return;
            int i = ResultsList.SelectedIndex;
            int steps = Math.Abs(d), dir = Math.Sign(d);
            for (int s = 0; s < steps; s++)
            {
                int j = i;
                do { j += dir; } while (j >= 0 && j < _results.Count && _results[j].IsHeader);
                if (j < 0 || j >= _results.Count) break;
                i = j;
            }
            if (i < 0) return;
            ResultsList.SelectedIndex = i;
            int view = i;
            if (dir < 0 && i > 0 && _results[i - 1].IsHeader) view = i - 1; // keep header visible
            ResultsList.ScrollIntoView(_results[view]);
            _userNavigated = true;
        }

        // ======================= GHOST AUTOCOMPLETE =======================

        string GhostCompletion()
        {
            var t = SearchBox.Text ?? "";
            if (t.Length == 0 || SearchBox.CaretIndex != t.Length || SearchBox.SelectionLength > 0 || t.EndsWith("  ")) return null;
            var s = _sugs.FirstOrDefault(x => x.Length > t.Length && x.StartsWith(t, StringComparison.OrdinalIgnoreCase));
            if (s == null)
            {
                var a = _apps.Select(x => x.app.Name).FirstOrDefault(n => n.Length > t.Length && n.StartsWith(t, StringComparison.OrdinalIgnoreCase));
                s = a;
            }
            return s == null ? null : s.Substring(t.Length);
        }

        void UpdateGhost()
        {
            var rest = GhostCompletion();
            GhostTyped.Text = rest == null ? "" : SearchBox.Text;
            GhostRest.Text = rest ?? "";
        }

        bool AcceptGhost()
        {
            var rest = GhostCompletion();
            if (rest == null) return false;
            SearchBox.Text = SearchBox.Text + rest;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            return true;
        }

        // ======================= PREVIEW / ANSWER =======================

        void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

        void UpdatePreview()
        {
            var it = Selected;
            if (it == null) { AnswerPanel.Visibility = DetailPanel.Visibility = Visibility.Collapsed; return; }
            if (it.Kind == ItemKind.Gemini || it.Kind == ItemKind.Suggestion) ShowAnswerFor(it.Target, false);
            else ShowDetail(it);
        }

        void MaybeInstantAnswer()
        {
            if (!S.InstantAnswers || string.IsNullOrWhiteSpace(S.ApiKey)) return;
            if (!IsVisible || ResultsView.Visibility != Visibility.Visible) return;
            var it = Selected;
            if (it == null || it.Kind != ItemKind.Gemini) return;    // don't spend API calls when an app/file is the obvious match
            if (it.Target.Length < 3 || _calc != null) return;
            ShowAnswerFor(it.Target, true);
        }

        void ShowAnswerFor(string query, bool start)
        {
            AnswerPanel.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(S.ApiKey))
            {
                Unbind();
                RenderStatic("**Connect Gemini** to get instant AI answers grounded in Google Search, plus page summaries for links you paste.\n\n" +
                             "1. Get a free API key at https://aistudio.google.com/apikey\n" +
                             "2. Open Settings (gear icon below, or Ctrl+,) and paste it.\n\n" +
                             "Until then, press Ctrl+Enter to search Google in your browser.");
                AnswerStatus.Text = "";
                return;
            }

            var ans = Gemini.Cached(S.Model, query);
            if (ans == null && start)
            {
                _answerCts?.Cancel();
                _answerCts = new CancellationTokenSource();
                ans = Gemini.Ask(S.ApiKey, S.Model, query, _answerCts.Token);
            }
            if (ans == null)
            {
                Unbind();
                bool pending = S.InstantAnswers && Selected?.Kind == ItemKind.Gemini && query == _q && _instantTimer.IsEnabled;
                RenderStatic(pending ? "" : "Press **Enter** to ask Gemini about “" + query + "”.\n\nCtrl+Enter opens Google search in your browser.");
                AnswerStatus.Text = pending ? "Waiting for you to finish typing…" : "";
                return;
            }
            Bind(ans);
        }

        void Bind(GeminiAnswer ans)
        {
            if (_bound != ans)
            {
                Unbind();
                _bound = ans;
                ans.Changed += OnAnswerChanged;
            }
            RenderAnswer();
        }

        void Unbind()
        {
            if (_bound != null) _bound.Changed -= OnAnswerChanged;
            _bound = null;
            SetSpinning(false);
        }

        void OnAnswerChanged()
        {
            if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                if (_bound != null) RenderAnswer();
            }));
        }

        Brush Res(string k) => (Brush)FindResource(k);

        void RenderStatic(string md)
        {
            Markdown.Render(AnswerDoc, md, Res("Fg1"), Res("Fg2"), Res("AccentText"), Res("ChipBg"));
            SourcesPanel.Visibility = Visibility.Collapsed;
            SearchChips.Children.Clear();
            CopyAnswer.Visibility = Visibility.Collapsed;
            SetSpinning(false);
        }

        void RenderAnswer()
        {
            var a = _bound;
            if (a == null) return;
            string text; List<GeminiSource> sources; List<string> queries; List<string> urls;
            lock (a)
            {
                text = a.Text.ToString();
                sources = a.Sources.ToList();
                queries = a.SearchQueries.ToList();
                urls = a.UrlsRead.ToList();
            }
            bool done = a.Done;

            if (a.Error != null && text.Length == 0)
            {
                var err = a.Error;
                string hint = "";
                if (err.Contains("API key") || err.Contains(" 400") || err.Contains(" 403")) hint = "\n\nCheck your API key in Settings (Ctrl+,).";
                else if (err.Contains(" 404")) hint = "\n\nThe model “" + S.Model + "” isn't available for your key. Pick another model in Settings.";
                else if (err.Contains(" 429")) hint = "\n\nYou've hit the Gemini rate limit for now. Try again in a minute, or turn off instant answers in Settings.";
                RenderStatic(err == "Cancelled" ? "Press **Enter** to ask Gemini." : "**Couldn't get an answer.** " + err + hint);
                AnswerStatus.Text = "";
                return;
            }

            Markdown.Render(AnswerDoc, done ? text : text + " ▍", Res("Fg1"), Res("Fg2"), Res("AccentText"), Res("ChipBg"));
            if (!done) AnswerStatus.Text = text.Length == 0 ? (urls.Count > 0 ? "Reading page…" : "Searching Google…") : "Writing…";
            else AnswerStatus.Text = sources.Count > 0 ? "Grounded in Google Search" : (urls.Count > 0 ? "From the page you linked" : "");
            SetSpinning(!done);
            CopyAnswer.Visibility = done && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            SourceChips.Children.Clear();
            foreach (var s in sources.Take(8))
            {
                var b = new Button { Style = (Style)FindResource("ChipButton"), Content = s.Title, ToolTip = s.Title, Focusable = false, Tag = s.Uri };
                b.Click += (o, e) => { Launcher.OpenUrl((string)((Button)o).Tag); AppRef.HideLauncher(); };
                SourceChips.Children.Add(b);
            }
            foreach (var u in urls.Take(3))
            {
                string host = u; try { host = new Uri(u).Host; } catch { }
                var b = new Button { Style = (Style)FindResource("ChipButton"), Content = host, ToolTip = u, Focusable = false, Tag = u };
                b.Click += (o, e) => { Launcher.OpenUrl((string)((Button)o).Tag); AppRef.HideLauncher(); };
                SourceChips.Children.Add(b);
            }
            SourcesPanel.Visibility = SourceChips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Google Search suggestions (required display for grounded results): the queries Gemini ran, linked to Google.
            SearchChips.Children.Clear();
            foreach (var q in queries.Take(2))
            {
                var b = new Button { Style = (Style)FindResource("ChipButton"), Focusable = false, Tag = q, ToolTip = "Search Google for “" + q + "”" };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Shapes.Path { Data = Glyphs.Search, Stroke = Res("Fg2"), StrokeThickness = 1.3, Stretch = Stretch.Uniform, Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0) });
                sp.Children.Add(new TextBlock { Text = q, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis });
                b.Content = sp;
                b.Click += (o, e) => { Launcher.GoogleSearch((string)((Button)o).Tag); AppRef.HideLauncher(); };
                SearchChips.Children.Add(b);
            }
        }

        void SetSpinning(bool on)
        {
            if (on)
            {
                if (SparkleSpin.HasAnimatedProperties) return;
                SparkleSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else SparkleSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        void ShowDetail(ResultItem it)
        {
            Unbind();
            AnswerPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            DetailIcon.Source = it.Icon;
            DetailGlyph.Data = it.Icon == null ? it.Glyph : null;
            if (it.Icon == null && (it.Kind == ItemKind.App || it.Kind == ItemKind.File || it.Kind == ItemKind.Folder))
            {
                System.ComponentModel.PropertyChangedEventHandler h = null;
                h = (s, e) => { if (e.PropertyName == "Icon" && Selected == it) DetailIcon.Source = it.Icon; it.PropertyChanged -= h; };
                it.PropertyChanged += h;
            }
            DetailTitle.Text = it.Kind == ItemKind.Calc ? it.Title : it.Title;
            DetailKind.Text = it.KindLabel;
            string path = null;
            switch (it.Kind)
            {
                case ItemKind.File: case ItemKind.Folder: path = it.Target; break;
                case ItemKind.App: path = it.LnkPath != null ? ShortenPath(it.LnkPath) : (it.Target.Contains("!") ? "Microsoft Store app" : null); break;
                case ItemKind.Url: path = it.Target; break;
                case ItemKind.Calc: path = it.Subtitle?.Split(new[] { "   ·" }, StringSplitOptions.None)[0]; break;
            }
            DetailPath.Text = path ?? "";
            DetailPath.Visibility = path == null ? Visibility.Collapsed : Visibility.Visible;

            DetailActions.Children.Clear();
            foreach (var (label, act) in ActionsFor(it, false))
            {
                if (label == "-") continue;
                var b = new Button { Style = (Style)FindResource("SubtleButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false, Padding = new Thickness(12, 8, 12, 8) };
                b.Content = new TextBlock { Text = label, Foreground = Res("Fg1") };
                var a = act;
                b.Click += (s, e) => a();
                DetailActions.Children.Add(b);
            }
        }

        // ======================= ACTIONS =======================

        List<(string, Action)> ActionsFor(ResultItem it, bool tile)
        {
            var l = new List<(string, Action)>();
            switch (it.Kind)
            {
                case ItemKind.App:
                    l.Add(("Open", () => Execute(it)));
                    if (it.LnkPath != null)
                    {
                        l.Add(("Run as administrator", () => Execute(it, admin: true)));
                        l.Add(("Open file location", () => { Launcher.ShowInFolder(it.LnkPath); AppRef.HideLauncher(); }));
                    }
                    break;
                case ItemKind.File:
                case ItemKind.Folder:
                    l.Add(("Open", () => Execute(it)));
                    if (it.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) l.Add(("Run as administrator", () => Execute(it, admin: true)));
                    l.Add(("Open file location", () => { Launcher.ShowInFolder(it.Target); AppRef.HideLauncher(); }));
                    l.Add(("Copy path", () => { try { Clipboard.SetText(it.Target); } catch { } }));
                    break;
                case ItemKind.Setting:
                    l.Add(("Open", () => Execute(it)));
                    break;
                case ItemKind.Url:
                    l.Add(("Open website", () => Execute(it)));
                    if (!tile) l.Add(("Summarize with Gemini", () => { ShowAnswerFor(it.Target, true); }));
                    l.Add(("Copy link", () => { try { Clipboard.SetText(it.Target); } catch { } }));
                    break;
                case ItemKind.Calc:
                    l.Add(("Copy result", () => Execute(it)));
                    break;
                case ItemKind.Gemini:
                case ItemKind.Suggestion:
                case ItemKind.Recent:
                case ItemKind.Trending:
                    l.Add(("Ask Gemini", () => Execute(it)));
                    l.Add(("Search on Google", () => { Launcher.GoogleSearch(it.Target); S.AddRecentSearch(it.Target); AppRef.HideLauncher(); }));
                    if (it.Kind == ItemKind.Recent)
                        l.Add(("Remove from history", () => { S.RecentSearches.Remove(it.Target); S.Save(); RefreshHome(); }));
                    break;
            }
            if (it.PinKind != null)
            {
                l.Add(("-", null));
                if (it.IsPinned || S.Pins.Any(p => p.Target == it.Target))
                    l.Add(("Unpin from launcher", () => TogglePin(it)));
                else
                    l.Add(("Pin to launcher", () => TogglePin(it)));
                if (tile)
                {
                    l.Add(("Move left", () => MovePin(it, -1)));
                    l.Add(("Move right", () => MovePin(it, +1)));
                }
            }
            return l;
        }

        void ShowMenu(ResultItem it, UIElement target, bool tile)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
            foreach (var (label, act) in ActionsFor(it, tile))
            {
                if (label == "-") { menu.Items.Add(new Separator()); continue; }
                var mi = new MenuItem { Header = label };
                var a = act;
                mi.Click += (s, e) => a();
                menu.Items.Add(mi);
            }
            if (menu.Items.Count > 0) menu.IsOpen = true;
        }

        void TogglePin(ResultItem it)
        {
            var existing = S.Pins.FirstOrDefault(p => string.Equals(p.Target, it.Target, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { S.Pins.Remove(existing); it.IsPinned = false; }
            else
            {
                string name = it.Title;
                if (it.Kind == ItemKind.Url) { try { name = new Uri(it.Target).Host.Replace("www.", ""); } catch { } }
                S.Pins.Add(new Pin { Kind = it.PinKind, Name = name, Target = it.Target });
                it.IsPinned = true;
            }
            S.Save();
            if (HomeView.Visibility == Visibility.Visible) RefreshHome();
            else if (Selected == it) ShowDetail(it);
        }

        void MovePin(ResultItem it, int d)
        {
            int i = S.Pins.FindIndex(p => p.Target == it.Target);
            int j = i + d;
            if (i < 0 || j < 0 || j >= S.Pins.Count) return;
            var p0 = S.Pins[i]; S.Pins[i] = S.Pins[j]; S.Pins[j] = p0;
            S.Save();
            RefreshHome();
        }

        public void Execute(ResultItem it, bool admin = false)
        {
            if (it == null || it.IsHeader) return;
            switch (it.Kind)
            {
                case ItemKind.App:
                case ItemKind.File:
                case ItemKind.Folder:
                case ItemKind.Setting:
                case ItemKind.Url:
                    if (Launcher.Open(it, admin))
                    {
                        S.CountLaunch(it.LaunchKey);
                        AppRef.HideLauncher();
                    }
                    break;
                case ItemKind.Calc:
                    try { Clipboard.SetText(it.Target); } catch { }
                    AppRef.HideLauncher();
                    break;
                case ItemKind.Gemini:
                    {
                        S.AddRecentSearch(it.Target);
                        var a = Gemini.Cached(S.Model, it.Target);
                        if (a != null && a.Done && _bound == a && AnswerPanel.Visibility == Visibility.Visible)
                        {
                            Launcher.GoogleSearch(it.Target); // Enter again on a finished answer -> full Google results
                            AppRef.HideLauncher();
                        }
                        else ShowAnswerFor(it.Target, true);
                        break;
                    }
                case ItemKind.Suggestion:
                case ItemKind.Recent:
                case ItemKind.Trending:
                    SetQueryAndAsk(it.Target);
                    break;
            }
        }

        void SetQueryAndAsk(string text)
        {
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
            FocusSearch();
            var gem = _results.FirstOrDefault(r => r.Kind == ItemKind.Gemini);
            if (gem != null) { ResultsList.SelectedItem = gem; _userNavigated = true; }
            _instantTimer.Stop();
            S.AddRecentSearch(text);
            ShowAnswerFor(text.Trim(), true);
        }

        // ======================= INPUT =======================

        void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            if (SearchBox.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            FocusSearch();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool results = ResultsView.Visibility == Visibility.Visible;
            bool inBox = SearchBox.IsKeyboardFocusWithin;

            switch (key)
            {
                case Key.Escape:
                    if (SearchBox.Text.Length > 0) { SearchBox.Text = ""; FocusSearch(); }
                    else AppRef.HideLauncher();
                    e.Handled = true; return;

                case Key.Down:
                case Key.Up:
                    if (results) { MoveSel(key == Key.Down ? 1 : -1); e.Handled = true; return; }
                    if (inBox && key == Key.Down) { FocusFirstHomeButton(); e.Handled = true; return; }
                    if (!inBox && Keyboard.FocusedElement is UIElement fe)
                    {
                        var before = Keyboard.FocusedElement;
                        fe.MoveFocus(new TraversalRequest(key == Key.Down ? FocusNavigationDirection.Down : FocusNavigationDirection.Up));
                        if (key == Key.Up && Keyboard.FocusedElement == before) FocusSearch();
                        e.Handled = true;
                    }
                    return;

                case Key.Left:
                case Key.Right:
                    if (!inBox && Keyboard.FocusedElement is UIElement fe2)
                    {
                        fe2.MoveFocus(new TraversalRequest(key == Key.Right ? FocusNavigationDirection.Right : FocusNavigationDirection.Left));
                        e.Handled = true; return;
                    }
                    if (key == Key.Right && inBox && SearchBox.CaretIndex == SearchBox.Text.Length && AcceptGhost()) { e.Handled = true; }
                    return;

                case Key.PageDown: if (results) { MoveSel(5); e.Handled = true; } return;
                case Key.PageUp: if (results) { MoveSel(-5); e.Handled = true; } return;

                case Key.Tab:
                    if (mods == ModifierKeys.None && results)
                    {
                        if (!AcceptGhost() && Selected != null && (Selected.Kind == ItemKind.Suggestion))
                        {
                            SearchBox.Text = Selected.Target; SearchBox.CaretIndex = SearchBox.Text.Length;
                        }
                        e.Handled = true;
                    }
                    return;

                case Key.Enter:
                    if (!inBox && Keyboard.FocusedElement is Button b && b.Content is ResultItem hi) { Execute(hi); e.Handled = true; return; }
                    if (!results) { e.Handled = true; return; }
                    if (mods == ModifierKeys.Control) { S.AddRecentSearch(_q); Launcher.GoogleSearch(_q); AppRef.HideLauncher(); }
                    else if (mods == (ModifierKeys.Control | ModifierKeys.Shift)) Execute(Selected, admin: true);
                    else if (mods == ModifierKeys.Alt && Selected != null)
                    {
                        var p = Selected.Kind == ItemKind.App ? Selected.LnkPath : Selected.Target;
                        if (p != null && (Selected.Kind == ItemKind.App || Selected.Kind == ItemKind.File || Selected.Kind == ItemKind.Folder)) { Launcher.ShowInFolder(p); AppRef.HideLauncher(); }
                    }
                    else Execute(Selected);
                    e.Handled = true; return;

                case Key.P:
                    if (mods == ModifierKeys.Control)
                    {
                        var target = results ? Selected : (Keyboard.FocusedElement as Button)?.Content as ResultItem;
                        if (target?.PinKind != null) TogglePin(target);
                        e.Handled = true;
                    }
                    return;

                case Key.OemComma:
                    if (mods == ModifierKeys.Control) { AppRef.OpenSettings(); e.Handled = true; }
                    return;

                case Key.Apps:
                    if (results && Selected != null)
                    {
                        var c = ResultsList.ItemContainerGenerator.ContainerFromItem(Selected) as UIElement;
                        ShowMenu(Selected, c ?? ResultsList, false);
                        e.Handled = true;
                    }
                    return;
            }
        }

        void FocusFirstHomeButton()
        {
            foreach (var ic in new ItemsControl[] { PinsList, RecommendedList, SearchesList })
            {
                if (ic.Items.Count == 0) continue;
                var c = ic.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
                var b = c != null ? FindChild<Button>(c) : null;
                if (b != null) { b.Focus(); return; }
            }
        }

        static T FindChild<T>(DependencyObject d) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is T t) return t;
                var r = FindChild<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        // ======================= MOUSE / BUTTONS =======================

        ResultItem ItemFromEvent(MouseButtonEventArgs e)
        {
            var c = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
            return c?.DataContext as ResultItem;
        }

        void ResultsList_Click(object sender, MouseButtonEventArgs e)
        {
            var it = ItemFromEvent(e);
            if (it == null || it.IsHeader) return;
            ResultsList.SelectedItem = it;
            _userNavigated = true;
            Execute(it);
            FocusSearch();
        }

        void ResultsList_RightClick(object sender, MouseButtonEventArgs e)
        {
            var it = ItemFromEvent(e);
            if (it == null || it.IsHeader) return;
            ResultsList.SelectedItem = it;
            _userNavigated = true;
            ShowMenu(it, ResultsList, false);
            e.Handled = true;
        }

        void HomeItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Content is ResultItem it) Execute(it);
        }

        void HomeItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as Button)?.Content is ResultItem it)) return;
            bool tile = PinsList.Items.Contains(it);
            ShowMenu(it, (UIElement)sender, tile);
            e.Handled = true;
        }

        void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; FocusSearch(); }

        void Settings_Click(object sender, RoutedEventArgs e) => AppRef.OpenSettings();

        void CopyAnswer_Click(object sender, RoutedEventArgs e)
        {
            if (_bound == null) return;
            var txt = Markdown.ToPlain(_bound.Snapshot());
            lock (_bound)
                if (_bound.Sources.Count > 0)
                    txt += "\n\nSources: " + string.Join(", ", _bound.Sources.Select(s => s.Title));
            try { Clipboard.SetText(txt); } catch { }
            AnswerStatus.Text = "Copied";
        }

        void OpenGoogle_Click(object sender, RoutedEventArgs e)
        {
            var q = _bound?.Query ?? _q;
            S.AddRecentSearch(q);
            Launcher.GoogleSearch(q);
            AppRef.HideLauncher();
        }

        void AddPin_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
            var f = new MenuItem { Header = "Browse for an app or file…" };
            f.Click += (s, a) => PinFromFile();
            var d = new MenuItem { Header = "Pin a folder…" };
            d.Click += (s, a) => PinFromFolder();
            var w = new MenuItem { Header = "Pin a website…" };
            w.Click += (s, a) => PinWebsite();
            var tip = new MenuItem { Header = "Tip: right-click any search result → Pin", IsEnabled = false };
            menu.Items.Add(f); menu.Items.Add(d); menu.Items.Add(w); menu.Items.Add(new Separator()); menu.Items.Add(tip);
            menu.IsOpen = true;
        }

        void PinFromFile()
        {
            SuppressHide = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Pin an app or file",
                    Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd;*.ps1;*.appref-ms|All files|*.*",
                    DereferenceLinks = false,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };
                if (dlg.ShowDialog(this) == true)
                {
                    S.Pins.Add(new Pin { Kind = "file", Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Target = dlg.FileName });
                    S.Save();
                }
            }
            finally { SuppressHide = false; Activate(); RefreshHome(); }
        }

        void PinFromFolder()
        {
            SuppressHide = true;
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Pin a folder", ShowNewFolderButton = false })
                {
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
                    {
                        var name = new DirectoryInfo(dlg.SelectedPath).Name;
                        S.Pins.Add(new Pin { Kind = "folder", Name = string.IsNullOrEmpty(name) ? dlg.SelectedPath : name, Target = dlg.SelectedPath });
                        S.Save();
                    }
                }
            }
            finally { SuppressHide = false; Activate(); RefreshHome(); }
        }

        void PinWebsite()
        {
            SuppressHide = true;
            try
            {
                var url = InputDialog.Ask(this, "Pin a website", "Website address", "https://");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    url = url.Trim();
                    if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                    string name = url;
                    try { name = new Uri(url).Host.Replace("www.", ""); } catch { }
                    S.Pins.Add(new Pin { Kind = "url", Name = name, Target = url });
                    S.Save();
                }
            }
            finally { SuppressHide = false; Activate(); FocusSearch(); RefreshHome(); }
        }
    }
}
