using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Render;
using RateDesk.Weekly.Core.Series;

namespace RateDesk.Weekly
{
    public partial class MainWindow : Window
    {
        public static readonly string AppDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RatesWeekly");
        public static readonly string OutDir = Path.Combine(AppDataDir, "out");

        private bool _updating;
        private EmailSettings _emailSettings = new();
        private bool _settingsLoading;

        /// <summary>Load the tickbox matrices from disk into the UI without firing the save
        /// handler. Called from the ctor; always clickable from the first frame — the settings
        /// gate email COMPOSITION only, never what the runs pull or store.</summary>
        private void LoadEmailSettings()
        {
            _settingsLoading = true;
            _emailSettings = EmailSettings.Load(AppDataDir);
            CbDailyFront.IsChecked = _emailSettings.DailyFrontTable;
            CbDailyRuns.IsChecked = _emailSettings.DailyOisRuns;
            CbDailyInfl.IsChecked = _emailSettings.DailyInflRuns;
            CbDailyXls.IsChecked = _emailSettings.DailyXlsAttachment;
            CbDailyInflXls.IsChecked = _emailSettings.DailyInflXlsAttachment;
            CbWeeklyFront.IsChecked = _emailSettings.WeeklyFrontTable;
            CbWeeklyRuns.IsChecked = _emailSettings.WeeklyOisRuns;
            CbWeeklyGrid.IsChecked = _emailSettings.WeeklyForwardGrid;
            CbWeeklyInfl.IsChecked = _emailSettings.WeeklyInflRuns;
            CbWeeklyDash.IsChecked = _emailSettings.WeeklyDashboardsAttachment;
            _settingsLoading = false;
        }

        private void Settings_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsLoading) return;
            _emailSettings.DailyFrontTable = CbDailyFront.IsChecked == true;
            _emailSettings.DailyOisRuns = CbDailyRuns.IsChecked == true;
            _emailSettings.DailyInflRuns = CbDailyInfl.IsChecked == true;
            _emailSettings.DailyXlsAttachment = CbDailyXls.IsChecked == true;
            _emailSettings.DailyInflXlsAttachment = CbDailyInflXls.IsChecked == true;
            _emailSettings.WeeklyFrontTable = CbWeeklyFront.IsChecked == true;
            _emailSettings.WeeklyOisRuns = CbWeeklyRuns.IsChecked == true;
            _emailSettings.WeeklyForwardGrid = CbWeeklyGrid.IsChecked == true;
            _emailSettings.WeeklyInflRuns = CbWeeklyInfl.IsChecked == true;
            _emailSettings.WeeklyDashboardsAttachment = CbWeeklyDash.IsChecked == true;
            try { _emailSettings.Save(AppDataDir); } catch { /* next change retries */ }
        }

        private WeeklyEmail.EmailParts WeeklyParts() => new(
            _emailSettings.WeeklyFrontTable, _emailSettings.WeeklyOisRuns, _emailSettings.WeeklyForwardGrid);

        private WeeklyEmail.EmailParts DailyParts() => new(
            _emailSettings.DailyFrontTable, _emailSettings.DailyOisRuns, Grid: false);

        public MainWindow()
        {
            InitializeComponent();
            VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(OutDir);
            Loaded += async (_, _) => await SetupSaveDown();
            // one line per button; the first WEEKLY RUN clears these and takes the box over
            LogBox.Text =
                "WEEKLY RUN — pulls Bloomberg, brings the history current, redraws every dashboard and builds the desk email.\r\n" +
                "CREATE EMAIL — opens a ready Outlook draft: body filled in, dashboards file attached.\r\n" +
                "COPY EMAIL — copies the built email to the clipboard, for pasting into an existing draft.\r\n" +
                "OPEN OUTPUT — opens the dashboards in your browser.\r\n" +
                "DAILY RUN — pulls a live snapshot and builds the daily OIS run: chat blast, OIS_Runs\r\n" +
                "  workbook (+ copy to the shared drive when publish.json has dailyDir), daily email.\r\n" +
                "COPY BLAST — copies the built blast text to the clipboard, for the Bloomberg chats.\r\n" +
                "DAILY EMAIL — opens a ready Outlook draft with the workbook attached.\r\n" +
                "\r\n" +
                "Only WEEKLY RUN and DAILY RUN are live until this session has built what the other\r\n" +
                "buttons serve. They unlock on \"WEEKLY COMPLETE\" / \"DAILY COMPLETE\".\r\n";
            LoadEmailSettings();
        }

        private void Log(string s) => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
            LogBox.ScrollToEnd();
        });

        /// <summary>SAVE-DOWN DESTINATION (desk 2026-08-25). On open the system searches the
        /// network drives for one called "salix" and locates Coverage &amp; Counterparties on it;
        /// found → "C+C folder located successfully" on the status line, nothing to click. Not
        /// found → a dialog offers "Locate C+C" (folder picker) or "Save Locally" (Documents,
        /// confirmed with an OK box). Either way the app creates — and afterwards checks for —
        /// the "OIS Runs" and "Inflation Runs" folders that each day's run files land in.</summary>
        private async Task SetupSaveDown()
        {
            try
            {
                // the salix search runs on EVERY open — a desk that chose Save Locally during an
                // outage upgrades back to C+C automatically the day the drive returns
                var detected = await Task.Run(() => RateDesk.Weekly.Core.SaveDown.SaveDownConfig.DetectSalix(Log));
                if (detected != null)
                {
                    RateDesk.Weekly.Core.SaveDown.SaveDownConfig.Save(AppDataDir, new("cc", detected));
                    await Task.Run(() => RateDesk.Weekly.Core.SaveDown.SaveDownConfig.EnsureFolders(detected));
                    StatusText.Text = "C+C folder located successfully.";
                    Log($"save-down: OIS Runs / Inflation Runs ready under {detected}");
                    return;
                }
                var cfg = RateDesk.Weekly.Core.SaveDown.SaveDownConfig.Load(AppDataDir);
                if (cfg != null && await Task.Run(() => Directory.Exists(cfg.Root)))
                {
                    await Task.Run(() => RateDesk.Weekly.Core.SaveDown.SaveDownConfig.EnsureFolders(cfg.Root));
                    StatusText.Text = cfg.Mode == "cc"
                        ? "C+C folder located successfully."
                        : "History saves to your Documents folder (OIS Runs / Inflation Runs).";
                    return;
                }
                AskSaveDownChoice();
            }
            catch (Exception ex) { Log("! save-down setup: " + ex.Message); }
        }

        private void AskSaveDownChoice()
        {
            var dlg = new Window
            {
                Title = "RatesWeekly",
                Width = 420, Height = 150, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                Background = System.Windows.Media.Brushes.White,
            };
            string? choice = null;
            var locate = new System.Windows.Controls.Button { Content = "Locate C+C", Width = 130, Height = 30, Margin = new Thickness(8) };
            var local = new System.Windows.Controls.Button { Content = "Save Locally", Width = 130, Height = 30, Margin = new Thickness(8) };
            locate.Click += (_, _) => { choice = "locate"; dlg.Close(); };
            local.Click += (_, _) => { choice = "local"; dlg.Close(); };
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Coverage and Counterparties not detected",
                FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var buttons = new System.Windows.Controls.StackPanel
                { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            buttons.Children.Add(locate);
            buttons.Children.Add(local);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.ShowDialog();

            if (choice == "locate")
            {
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Locate Coverage and Counterparties" };
                if (picker.ShowDialog(this) == true)
                {
                    RateDesk.Weekly.Core.SaveDown.SaveDownConfig.Save(AppDataDir, new("cc", picker.FolderName));
                    try
                    {
                        RateDesk.Weekly.Core.SaveDown.SaveDownConfig.EnsureFolders(picker.FolderName);
                        StatusText.Text = "C+C folder located successfully.";
                        Log($"save-down: OIS Runs / Inflation Runs ready under {picker.FolderName}");
                        return;
                    }
                    catch (Exception ex) { Log("! save-down: " + ex.Message); }
                }
            }
            // Save Locally — chosen, or the fallback when the picker was cancelled
            var docs = RateDesk.Weekly.Core.SaveDown.SaveDownConfig.LocalRoot();
            RateDesk.Weekly.Core.SaveDown.SaveDownConfig.Save(AppDataDir, new("local", docs));
            RateDesk.Weekly.Core.SaveDown.SaveDownConfig.EnsureFolders(docs);
            MessageBox.Show(this, "History will be saved to your documents folder",
                "RatesWeekly", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "History saves to your Documents folder (OIS Runs / Inflation Runs).";
        }

        /// <summary>The daily email's recipient list — paste addresses (semicolon or line
        /// separated), saved app-side, PRELOADED from the incumbent workbook's VBA list.
        /// They are applied as BCC, always BCC (desk 2026-08-25).</summary>
        private void Recipients_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Window
            {
                Title = "Daily email recipients — always sent as BCC",
                Width = 560, Height = 460, ResizeMode = ResizeMode.CanResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                Background = System.Windows.Media.Brushes.White,
            };
            var box = new System.Windows.Controls.TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12,
                Text = string.Join(Environment.NewLine, Recipients.Load(AppDataDir)),
            };
            var save = new System.Windows.Controls.Button
                { Content = "Save", Width = 110, Height = 28, Margin = new Thickness(0, 8, 8, 0) };
            var cancel = new System.Windows.Controls.Button
                { Content = "Cancel", Width = 110, Height = 28, Margin = new Thickness(0, 8, 0, 0) };
            save.Click += (_, _) =>
            {
                var list = Recipients.Parse(box.Text);
                Recipients.Save(AppDataDir, list);
                StatusText.Text = $"recipients saved — {list.Count} address(es), applied as BCC on DAILY EMAIL.";
                dlg.Close();
            };
            cancel.Click += (_, _) => dlg.Close();
            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            var hint = new System.Windows.Controls.TextBlock
            {
                Text = "One address per line (or semicolon-separated). These go into the daily "
                       + "draft as BCC — never To/Cc.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            };
            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            System.Windows.Controls.Grid.SetRow(hint, 0);
            System.Windows.Controls.Grid.SetRow(box, 1);
            System.Windows.Controls.Grid.SetRow(buttons, 2);
            grid.Children.Add(hint);
            grid.Children.Add(box);
            grid.Children.Add(buttons);
            dlg.Content = grid;
            dlg.ShowDialog();
        }

        /// <summary>Outlier CHECK notes demand eyes before distribution (desk 2026-08-25, the
        /// BOJ Δ1m question) — surfaced as a blocking message box on top of the log lines.</summary>
        private void ShowCheckNotes(IEnumerable<string> notes)
        {
            var checks = notes.Where(n => n.StartsWith(OutlierGuard.Prefix + ":")).ToList();
            if (checks.Count == 0) return;
            MessageBox.Show(this,
                "Outlier check — verify these before distributing:\n\n" + string.Join("\n", checks),
                "RatesWeekly — manual check required", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>The CHECK gate, moved BEFORE anything is written or mirrored (audit
        /// 2026-08-26: the popup used to fire after the blast, workbooks and shared-drive
        /// copies were already published everywhere except the email body). Returns true to
        /// proceed; false aborts the render with nothing on disk changed.</summary>
        private bool ConfirmChecks(IEnumerable<string> notes, string what)
        {
            var checks = notes.Where(n => n.StartsWith(OutlierGuard.Prefix + ":")).ToList();
            if (checks.Count == 0) return true;
            var r = MessageBox.Show(this,
                "Outlier check — verify these before distributing:\n\n" + string.Join("\n", checks) +
                $"\n\nContinue and write/publish the {what}?  (No = nothing is written)",
                "RatesWeekly — manual check required", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return r == MessageBoxResult.Yes;
        }

        /// <summary>SOURCES (trial, desk 2026-08-26): dodgeball's per-run contributor picker as
        /// a dialog — one combo per bank (comp = composite plus the contributor catalog; opening
        /// a list probes which contributors actually price that family, '*>1h' marking a feed
        /// quiet for over an hour). Saved app-side (sources.json) and applied on every run;
        /// mids AND change anchors follow the choice (v0.10.4 made anchors source-coherent).
        /// Picking the config default removes the override.</summary>
        private void Sources_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var overrides = SourceStore.Load(AppDataDir);
                var scheds = MeetingsStore.Schedules
                    .Where(s => string.IsNullOrEmpty(s.Kind) && s.Tickers.Any(t => t.Contains("{N}")))
                    .ToList();
                var dlg = new Window
                {
                    Title = "Pricing sources — per-run contributor (trial)",
                    Owner = this, Width = 420, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.NoResize,
                };
                var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(14) };
                stack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "Contributor per run — comp = Bloomberg composite. Applies from the " +
                           "NEXT daily/weekly run; mids and change anchors follow together. " +
                           "Open a list to probe who prices that family ('*>1h' = quiet feed).",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 11.5,
                });
                RateDesk.Bloomberg.RefDataClient? probe = null;
                RateDesk.Bloomberg.RefDataClient? Probe()
                {
                    try { return probe ??= new RateDesk.Bloomberg.RefDataClient(); }
                    catch { return null; }   // no terminal — static candidate list still works
                }
                var picks = new Dictionary<MeetingScheduleDef, System.Windows.Controls.ComboBox>();
                foreach (var sched in scheds)
                {
                    var dflt = sched.Source ?? "";
                    var cur = overrides.TryGetValue(sched.Name, out var o) ? o : dflt;
                    var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var cmb = new System.Windows.Controls.ComboBox { Width = 120, FontSize = 11 };
                    bool filling = false;
                    void Fill(List<(string Src, double? AgeMinutes)>? probed)
                    {
                        filling = true;
                        var sel = Sel(cmb) ?? cur;
                        cmb.Items.Clear();
                        var opts = probed != null
                            ? probed.Select(p => (p.Src, Label: (p.Src.Length == 0 ? "comp" : p.Src)
                                                               + (p.AgeMinutes is > 60 ? " *>1h" : ""))).ToList()
                            : new[] { "" }.Concat(SourceCatalog.Candidates.Where(c => c.Length > 0))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Select(s => (Src: s, Label: s.Length == 0 ? "comp" : s)).ToList();
                        if (!opts.Any(x => x.Src.Equals(sel, StringComparison.OrdinalIgnoreCase)))
                            opts.Insert(0, (sel, sel.Length == 0 ? "comp" : sel));
                        foreach (var op in opts) cmb.Items.Add(op.Label);
                        cmb.SelectedItem = opts.First(x => x.Src.Equals(sel, StringComparison.OrdinalIgnoreCase)).Label;
                        filling = false;
                    }
                    Fill(null);
                    cmb.DropDownOpened += async (_, _) =>
                    {
                        if (filling || Probe() is not { } rd) return;
                        var root = sched.Tickers.First(t => t.Contains("{N}")).Replace("{N}", "1");
                        try
                        {
                            var probed = await Task.Run(() =>
                                rd.DiscoverSourcesWithAge(root, SourceCatalog.Candidates));
                            if (probed.Count > 0) Fill(probed);
                        }
                        catch { /* probe is best-effort */ }
                    };
                    System.Windows.Controls.DockPanel.SetDock(cmb, System.Windows.Controls.Dock.Right);
                    row.Children.Add(cmb);
                    row.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = $"{sched.Name}   (default {(dflt.Length == 0 ? "comp" : dflt)})",
                        VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
                    });
                    stack.Children.Add(row);
                    picks[sched] = cmb;
                }
                var buttons = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
                };
                var save = new System.Windows.Controls.Button { Content = "Save", Width = 84, Height = 28, IsDefault = true };
                var cancel = new System.Windows.Controls.Button
                    { Content = "Cancel", Width = 84, Height = 28, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
                save.Click += (_, _) =>
                {
                    var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (sched, cmb) in picks)
                    {
                        var src = Sel(cmb) ?? "";
                        if (!src.Equals(sched.Source ?? "", StringComparison.OrdinalIgnoreCase))
                            next[sched.Name] = src;   // default picks store nothing (= default)
                    }
                    SourceStore.Save(AppDataDir, next);
                    StatusText.Text = next.Count == 0
                        ? "sources saved — all runs on their config defaults."
                        : "sources saved — " + string.Join(", ", next.Select(kv =>
                              $"{kv.Key}→{(kv.Value.Length == 0 ? "comp" : kv.Value)}"))
                          + ". Applies from the next run.";
                    dlg.DialogResult = true;
                };
                buttons.Children.Add(save);
                buttons.Children.Add(cancel);
                stack.Children.Add(buttons);
                dlg.Content = stack;
                dlg.Closed += (_, _) => { try { probe?.Dispose(); } catch { } };
                dlg.ShowDialog();

                static string? Sel(System.Windows.Controls.ComboBox c) =>
                    c.SelectedItem is string s ? (s.Split(' ')[0] == "comp" ? "" : s.Split(' ')[0]) : null;
            }
            catch (Exception ex) { StatusText.Text = "sources: " + ex.Message; }
        }

        /// <summary>The output buttons unlock only when what they SERVE was rebuilt by this
        /// session (desk 2026-08-20): the email buttons need a built email, OPEN OUTPUT needs
        /// rendered pages. A failed leg keeps its buttons dark rather than serving stale files.</summary>
        private void SetOutputButtons(bool email, bool output)
        {
            CreateEmailBtn.IsEnabled = email;
            CopyEmailBtn.IsEnabled = email;
            OpenOutBtn.IsEnabled = output;
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            _updating = true;
            UpdateBtn.IsEnabled = false;
            SetOutputButtons(false, false);   // re-lock during the rebuild — mid-update output is mixed-state
            LogBox.Clear();   // the startup instructions give way to the run log
            StatusText.Text = "running weekly...";
            try
            {
                using var store = new HistoryStore(Path.Combine(AppDataDir, "history.db"));
                var (result, pages, weeklyRep, emailErr0) = await Task.Run(() =>
                {
                    var r = UpdateEngine.Run(store, new RatesSnapshot(), Log);
                    // Pulling the data and not redrawing the pages would leave the desk reading
                    // last week's dashboards, so the two are one action.
                    int n = RenderAll(store, Log);
                    // The email is the same click (DESIGN.md §9), persisted to out\ so COPY EMAIL
                    // is instant and restart-safe. It builds AFTER the engine has released its
                    // session, on its own — a failure leaves the dashboards standing.
                    WeeklyReport? rep0 = null;
                    string? eErr = null;
                    try { rep0 = EmailBuilder.Build(Log, store, AppDataDir); }
                    catch (Exception ex) { eErr = ex.Message; Log("! email build failed: " + ex.Message); }
                    return (r, n, rep0, eErr);
                });
                var emailErr = emailErr0;
                var emailNotes = weeklyRep?.Notes.ToList() ?? new List<string>();
                if (weeklyRep != null)
                {
                    // GATE BEFORE PUBLISH (audit 2026-08-26): flagged numbers get eyes before
                    // the email fragments exist on disk
                    if (!ConfirmChecks(weeklyRep.Notes, "weekly email fragments"))
                    {
                        emailErr = "cancelled at the outlier check";
                        Log("WEEKLY EMAIL CANCELLED — CHECK notes declined; fragments not written.");
                    }
                    else
                        await Task.Run(() =>
                        {
                            try
                            {
                                EmailBuilder.Render(weeklyRep, OutDir,
                                    EmailBuilder.LoadSiteBase(AppDataDir), Log, store);
                            }
                            catch (Exception ex)
                            {
                                emailErr = ex.Message;
                                Log("! email render failed: " + ex.Message);
                            }
                        });
                }
                StatusText.Text = $"updated {DateTime.Now:HH:mm:ss} — {result.Tickers} tickers, " +
                                  $"{result.RowsWritten} rows written, {pages} pages rendered, " +
                                  (emailErr == null ? "email ready " : "EMAIL FAILED — see log ") +
                                  $"({result.Elapsed.TotalSeconds:F0}s)" +
                                  (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} warning(s) in log" : "");
                foreach (var w in result.Warnings) Log("! " + w);
                SetOutputButtons(email: emailErr == null, output: pages > 0);
                Log(emailErr == null && pages > 0
                    ? "WEEKLY COMPLETE — email and dashboards rebuilt; all buttons unlocked."
                    : emailErr != null && pages > 0
                        ? "WEEKLY PARTIAL — dashboards rebuilt (OPEN OUTPUT unlocked) but the email " +
                          "FAILED, so the email buttons stay locked. Fix and run WEEKLY again."
                        : "WEEKLY PARTIAL — nothing rendered; buttons stay locked. See the log.");
                // CHECK notes were gated BEFORE the render (audit 2026-08-26) — no second popup
            }
            catch (Exception ex)
            {
                StatusText.Text = "update failed: " + ex.Message;
                Log("! update failed: " + ex.Message);
                Log("WEEKLY FAILED — buttons stay locked (they would serve stale output). Fix and run WEEKLY again.");
            }
            finally
            {
                _updating = false;
                UpdateBtn.IsEnabled = true;
            }
        }

        /// <summary>Redraw every currency page from the store. Returns the number written.</summary>
        private static int RenderAll(HistoryStore store, Action<string> log)
        {
            if (store.LatestDate() is not { } asOf)
            {
                log("! store is empty — nothing to render");
                return 0;
            }
            Directory.CreateDirectory(OutDir);
            var configs = RateDesk.Core.Config.ConfigStore.LoadDefault();
            var svc = new PricingService(configs, new RatesSnapshot());
            int n = 0;
            foreach (var cfg in configs.Enabled)
            {
                if (cfg.Ois == null && cfg.Irs == null && cfg.Ladders.Count == 0) continue;
                try
                {
                    File.WriteAllText(
                        Path.Combine(OutDir, cfg.Ccy.ToLowerInvariant() + ".html"),
                        CurrencyPage.Build(cfg, svc.SourceFor(cfg.Ccy), store, asOf));
                    n++;
                }
                catch (Exception ex) { log($"! {cfg.Ccy}: {ex.Message}"); }
            }
            // the hub last, so it ranks off the same store state the pages were drawn from —
            // then the whole site again as ONE self-contained file, the email's attachment
            try
            {
                var mv = MoverScan.Scan(configs, svc.SourceFor, store, asOf);
                File.WriteAllText(Path.Combine(OutDir, "index.html"), MoversPage.Build(mv));
                File.WriteAllText(Path.Combine(OutDir, "movers.json"), MoverScan.ToJson(mv));
                n++;
                log($"movers hub: {mv.DmRanked.Count} DM / {mv.EmRanked.Count} EM instruments ranked");
                var pack = Path.Combine(OutDir, SiteFile.FileName);
                File.WriteAllText(pack, SiteFile.Build(configs, svc.SourceFor, store, asOf, mv));
                log($"single-file pack: {SiteFile.FileName} ({new FileInfo(pack).Length / 1024.0 / 1024.0:F2} MB)");
            }
            catch (Exception ex) { log("! movers/pack failed: " + ex.Message); }
            log($"rendered {n} page(s) as of {asOf:yyyy-MM-dd}");
            return n;
        }

        /// <summary>One click to a ready Outlook draft: body = the persisted email fragment, the
        /// single-file dashboards pack attached. COM via late binding so the exe stays standalone
        /// and works with whatever Outlook the desk runs. COPY EMAIL remains the fallback — a
        /// clipboard paste physically cannot carry an attachment.</summary>
        /// <summary>Compose the weekly email body from the FROZEN report under the CURRENT
        /// tickboxes. Data is what WEEKLY RUN pulled; selection is what is ticked right now.
        /// Falls back to the persisted fragment for runs made before report persistence.</summary>
        private string? ComposeWeeklyBody()
        {
            if (ReportStore.Load(Path.Combine(OutDir, EmailBuilder.ReportFile)) is { } rep)
            {
                var siteBase = EmailBuilder.LoadSiteBase(AppDataDir);
                Func<string, string?>? href = siteBase == null
                    ? null : ccy => $"{siteBase}/{ccy.ToLowerInvariant()}.html";
                return WeeklyEmail.Html(rep, href, partsOpt: WeeklyParts())
                       + InflFragment(RateDesk.Weekly.Core.Infl.InflEmail.WeeklyHtmlFile,
                           _emailSettings.WeeklyInflRuns);
            }
            var frag = Path.Combine(OutDir, EmailBuilder.FragmentFile);
            return File.Exists(frag) ? File.ReadAllText(frag) : null;
        }

        /// <summary>The frozen inflation section from the last run, when its tickbox is on and
        /// the run actually produced one — never rebuilt at click time.</summary>
        private static string InflFragment(string file, bool ticked)
        {
            var p = Path.Combine(OutDir, file);
            return ticked && File.Exists(p) ? File.ReadAllText(p) : "";
        }

        private void CreateEmail_Click(object sender, RoutedEventArgs e)
        {
            var body = ComposeWeeklyBody();
            if (body == null)
            {
                StatusText.Text = "no email built yet — run WEEKLY RUN first.";
                return;
            }
            try
            {
                var t = Type.GetTypeFromProgID("Outlook.Application")
                    ?? throw new InvalidOperationException("Outlook is not installed on this machine");
                dynamic outlook = Activator.CreateInstance(t)!;
                dynamic mail = outlook.CreateItem(0); // olMailItem
                // subject dated from the REPORT the body was composed from, invariant culture
                // (audit 2026-08-26: a post-midnight send stamped tomorrow over today's numbers)
                var wkAsOf = ReportStore.Load(Path.Combine(OutDir, EmailBuilder.ReportFile))?.AsOf
                             ?? DateTime.Today;
                mail.Subject = "DRAX Swaps — Rates Weekly — " + wkAsOf.ToString("dd MMM yyyy",
                    System.Globalization.CultureInfo.InvariantCulture);
                mail.HTMLBody = "<html><body style=\"margin:14px;background:#ffffff;\">"
                    + body + "</body></html>";
                var pack = Path.Combine(OutDir, SiteFile.FileName);
                bool attached = _emailSettings.WeeklyDashboardsAttachment && File.Exists(pack);
                if (attached) mail.Attachments.Add(pack);
                mail.Display();
                StatusText.Text = "draft opened in Outlook — add recipients and send."
                    + (attached ? " Dashboards pack attached."
                       : _emailSettings.WeeklyDashboardsAttachment
                           ? " (no dashboards pack found — run WEEKLY RUN)"
                           : " Dashboards attachment unticked.");
            }
            catch (Exception ex) { StatusText.Text = "create email failed: " + ex.Message; }
        }

        private void CopyEmail_Click(object sender, RoutedEventArgs e)
        {
            // Composes from the PERSISTED report under the CURRENT tickboxes — the data never
            // rebuilds (what WEEKLY RUN pulled is what pastes, restart-safe); only the section
            // selection is applied at click time (desk 2026-08-21).
            var body = ComposeWeeklyBody();
            if (body == null)
            {
                StatusText.Text = "no email built yet — run WEEKLY RUN first.";
                return;
            }
            try
            {
                string plain = ReportStore.Load(Path.Combine(OutDir, EmailBuilder.ReportFile)) is { } rep
                    ? WeeklyEmail.PlainText(rep, partsOpt: WeeklyParts())
                      + InflFragment(RateDesk.Weekly.Core.Infl.InflEmail.WeeklyTextFile,
                          _emailSettings.WeeklyInflRuns)
                    : File.Exists(Path.Combine(OutDir, EmailBuilder.PlainTextFile))
                        ? File.ReadAllText(Path.Combine(OutDir, EmailBuilder.PlainTextFile)) : "";
                ClipboardHtml.Set(body, plain);
                StatusText.Text = "email copied (current tickboxes applied) — paste into the email body.";
            }
            catch (Exception ex) { StatusText.Text = "copy failed: " + ex.Message; }
        }

        private bool _dailyRunning;

        private async void Daily_Click(object sender, RoutedEventArgs e)
        {
            if (_dailyRunning) return;
            _dailyRunning = true;
            DailyBtn.IsEnabled = false;
            CopyBlastBtn.IsEnabled = false;
            DailyEmailBtn.IsEnabled = false;
            StatusText.Text = "building daily OIS run...";
            try
            {
                using var store = new HistoryStore(Path.Combine(AppDataDir, "history.db"));
                var rep = await Task.Run(() => Core.Daily.DailyBuilder.Build(store, Log, AppDataDir));
                // GATE BEFORE PUBLISH (audit 2026-08-26): flagged numbers must be seen before
                // the blast/workbooks/shared-drive copies exist, not after
                if (!ConfirmChecks(rep.Notes, "daily outputs"))
                {
                    StatusText.Text = "daily CANCELLED at the outlier check — nothing written.";
                    Log("DAILY CANCELLED — CHECK notes declined; no blast/workbook/email was written.");
                    return;
                }
                var output = await Task.Run(() =>
                    Core.Daily.DailyBuilder.Render(rep, store, OutDir, AppDataDir, Log));
                CopyBlastBtn.IsEnabled = true;
                DailyEmailBtn.IsEnabled = true;
                StatusText.Text = $"daily built {DateTime.Now:HH:mm:ss} — blast + workbook" +
                                  (output.DailyDirCopy != null ? " (+ shared drive)" : "") + " + email ready";
                Log("DAILY COMPLETE — blast, workbook and email rebuilt; COPY BLAST / DAILY EMAIL unlocked.");
            }
            catch (Exception ex)
            {
                StatusText.Text = "daily failed: " + ex.Message;
                Log("! daily failed: " + ex.Message);
                Log("DAILY FAILED — COPY BLAST / DAILY EMAIL stay locked (they would serve stale output).");
            }
            finally
            {
                _dailyRunning = false;
                DailyBtn.IsEnabled = true;
            }
        }

        private void CopyBlast_Click(object sender, RoutedEventArgs e)
        {
            var blast = Path.Combine(OutDir, Core.Daily.DailyBuilder.BlastFile);
            if (!File.Exists(blast))
            {
                StatusText.Text = "no blast built yet — run DAILY RUN first.";
                return;
            }
            try
            {
                // CF_HTML so an IB paste renders as a TABLE (desk 2026-08-25), replicating the
                // attached workbook's formatting; the plain text rides along for text targets
                var html = Path.Combine(OutDir, Core.Daily.DailyBuilder.BlastHtmlFile);
                if (File.Exists(html))
                    ClipboardHtml.Set(File.ReadAllText(html), File.ReadAllText(blast));
                else
                    Clipboard.SetText(File.ReadAllText(blast));
                StatusText.Text = $"blast copied as a table (built {File.GetLastWriteTime(blast):HH:mm}) — paste into the Bloomberg chat.";
            }
            catch (Exception ex) { StatusText.Text = "copy failed: " + ex.Message; }
        }

        private void DailyEmail_Click(object sender, RoutedEventArgs e)
        {
            string? body = null;
            var rep = ReportStore.Load(Path.Combine(OutDir, Core.Daily.DailyBuilder.ReportFile));
            if (rep != null)
                body = WeeklyEmail.Html(rep, partsOpt: DailyParts())
                       + InflFragment(RateDesk.Weekly.Core.Infl.InflEmail.DailyHtmlFile,
                           _emailSettings.DailyInflRuns);
            else
            {
                var frag0 = Path.Combine(OutDir, Core.Daily.DailyBuilder.FragmentFile);
                if (File.Exists(frag0)) body = File.ReadAllText(frag0);
            }
            if (body == null)
            {
                StatusText.Text = "no daily email built yet — run DAILY RUN first.";
                return;
            }
            try
            {
                var t = Type.GetTypeFromProgID("Outlook.Application")
                    ?? throw new InvalidOperationException("Outlook is not installed on this machine");
                dynamic outlook = Activator.CreateInstance(t)!;
                dynamic mail = outlook.CreateItem(0); // olMailItem
                // subject dated from the REPORT the body carries, invariant culture (audit
                // 2026-08-26: a post-midnight send stamped tomorrow over today's numbers)
                mail.Subject = "DRAX Swaps Closing Runs - " + (rep?.AsOf ?? DateTime.Today)
                    .ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
                // JBDH banner at the top — small and unintrusive (desk 2026-08-25). Embedded
                // as a hidden CID attachment: base64 images don't render in Outlook desktop.
                string bannerImg = "";
                try
                {
                    var bannerPath = ExtractBanner();
                    dynamic att = mail.Attachments.Add(bannerPath, 1 /*olByValue*/, 0, "jbdh");
                    att.PropertyAccessor.SetProperty(
                        "http://schemas.microsoft.com/mapi/proptag/0x3712001F", "jbdhbanner");
                    bannerImg = "<img src=\"cid:jbdhbanner\" width=\"146\" height=\"30\" " +
                                "style=\"display:block;margin:0 0 10px 0;\" alt=\"JB Drax Honoré\"/>";
                }
                catch { /* no banner beats no email */ }
                mail.HTMLBody = "<html><body style=\"margin:14px;background:#ffffff;\">"
                    + bannerImg + body + "</body></html>";
                // recipients: ALWAYS BCC, never To/Cc — a client list must not leak to clients
                var bcc = Recipients.Bcc(AppDataDir);
                if (bcc.Length > 0) mail.BCC = bcc;
                // attach the workbook the BODY's report wrote — by its exact dated name, never
                // "the newest file matching a glob" (audit 2026-08-26: an out-of-band write
                // could attach a different as-of under today's subject). Glob fallback only for
                // pre-report-persistence runs, and the status line then says which file went.
                string? Newest(string pattern) => Directory.EnumerateFiles(OutDir, pattern)
                    .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                string? Exact(string name, string pattern)
                {
                    var p = Path.Combine(OutDir, name);
                    return File.Exists(p) ? p : Newest(pattern);
                }
                var book = _emailSettings.DailyXlsAttachment
                    ? (rep != null
                        ? Exact(Core.Daily.DailyBook.FileName(rep.AsOf), "DRAX OIS Runs *.xlsx")
                        : Newest("DRAX OIS Runs *.xlsx"))
                    : null;
                var infl = _emailSettings.DailyInflXlsAttachment
                    ? (rep != null
                        ? Exact(RateDesk.Weekly.Core.Infl.InflRunsXlsx.FileName(rep.AsOf), "DRAX Fixing Runs *.xlsx")
                        : Newest("DRAX Fixing Runs *.xlsx"))
                    : null;
                if (book != null) mail.Attachments.Add(book);
                if (infl != null) mail.Attachments.Add(infl);
                mail.Display();
                int attached = (book != null ? 1 : 0) + (infl != null ? 1 : 0);
                StatusText.Text = $"daily draft opened — {Recipients.Load(AppDataDir).Count} recipient(s) in BCC. " +
                    $"{attached} workbook(s) attached" +
                    (_emailSettings.DailyXlsAttachment && book == null ? " (no OIS workbook found)" : "") +
                    (_emailSettings.DailyInflXlsAttachment && infl == null ? " (no inflation workbook found)" : "") + ".";
            }
            catch (Exception ex) { StatusText.Text = "daily email failed: " + ex.Message; }
        }

        /// <summary>Write the embedded JBDH banner to out\ for the email's CID attachment —
        /// resource-embedded so the exe stays standalone.</summary>
        private static string ExtractBanner()
        {
            var path = Path.Combine(OutDir, "jbdh_banner.jpg");
            if (!File.Exists(path))
            {
                var asm = Assembly.GetExecutingAssembly();
                var res = asm.GetManifestResourceNames()
                    .First(n => n.EndsWith("jbdh_banner.jpg", StringComparison.OrdinalIgnoreCase));
                using var s = asm.GetManifestResourceStream(res)!;
                using var f = File.Create(path);
                s.CopyTo(f);
            }
            return path;
        }

        private bool _exporting;

        private async void ExportXls_Click(object sender, RoutedEventArgs e)
        {
            if (_exporting) return;
            _exporting = true;
            ExportXlsBtn.IsEnabled = false;
            StatusText.Text = "exporting workbook from stored data...";
            try
            {
                var path = await Task.Run(() =>
                {
                    using var store = new HistoryStore(Path.Combine(AppDataDir, "history.db"));
                    return Core.Daily.DailyBuilder.ExportBook(store, OutDir, AppDataDir, Log);
                });
                StatusText.Text = $"workbook exported: {Path.GetFileName(path)} — opening.";
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = "export failed: " + ex.Message;
                Log("! export failed: " + ex.Message);
            }
            finally
            {
                _exporting = false;
                ExportXlsBtn.IsEnabled = true;
            }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(OutDir);
            // Open a dashboard if one exists — that is what the button is for. The folder is only
            // interesting when there is nothing to show yet.
            var landing = new[] { "index.html", "usd.html" }
                .Select(f => Path.Combine(OutDir, f)).FirstOrDefault(File.Exists)
                ?? Directory.EnumerateFiles(OutDir, "*.html").FirstOrDefault();
            Process.Start(new ProcessStartInfo(landing ?? OutDir) { UseShellExecute = true });
        }
    }
}
