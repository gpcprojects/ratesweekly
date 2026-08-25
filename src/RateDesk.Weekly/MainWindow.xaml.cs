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
            CbDailyXls.IsChecked = _emailSettings.DailyXlsAttachment;
            CbWeeklyFront.IsChecked = _emailSettings.WeeklyFrontTable;
            CbWeeklyRuns.IsChecked = _emailSettings.WeeklyOisRuns;
            CbWeeklyGrid.IsChecked = _emailSettings.WeeklyForwardGrid;
            CbWeeklyDash.IsChecked = _emailSettings.WeeklyDashboardsAttachment;
            _settingsLoading = false;
        }

        private void Settings_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsLoading) return;
            _emailSettings.DailyFrontTable = CbDailyFront.IsChecked == true;
            _emailSettings.DailyOisRuns = CbDailyRuns.IsChecked == true;
            _emailSettings.DailyXlsAttachment = CbDailyXls.IsChecked == true;
            _emailSettings.WeeklyFrontTable = CbWeeklyFront.IsChecked == true;
            _emailSettings.WeeklyOisRuns = CbWeeklyRuns.IsChecked == true;
            _emailSettings.WeeklyForwardGrid = CbWeeklyGrid.IsChecked == true;
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
                var (result, pages, emailErr) = await Task.Run(() =>
                {
                    using var store = new HistoryStore(Path.Combine(AppDataDir, "history.db"));
                    var r = UpdateEngine.Run(store, new RatesSnapshot(), Log);
                    // Pulling the data and not redrawing the pages would leave the desk reading
                    // last week's dashboards, so the two are one action.
                    int n = RenderAll(store, Log);
                    // The email is the same click (DESIGN.md §9), persisted to out\ so COPY EMAIL
                    // is instant and restart-safe. It builds AFTER the engine has released its
                    // session, on its own — a failure leaves the dashboards standing.
                    string? eErr = null;
                    try
                    {
                        var rep = EmailBuilder.Build(Log);
                        EmailBuilder.Render(rep, OutDir, EmailBuilder.LoadSiteBase(AppDataDir), Log);
                    }
                    catch (Exception ex) { eErr = ex.Message; Log("! email build failed: " + ex.Message); }
                    return (r, n, eErr);
                });
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
                return WeeklyEmail.Html(rep, href, partsOpt: WeeklyParts());
            }
            var frag = Path.Combine(OutDir, EmailBuilder.FragmentFile);
            return File.Exists(frag) ? File.ReadAllText(frag) : null;
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
                mail.Subject = $"DRAX Swaps — Rates Weekly — {DateTime.Today:dd MMM yyyy}";
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
                var output = await Task.Run(() =>
                {
                    using var store = new HistoryStore(Path.Combine(AppDataDir, "history.db"));
                    var rep = Core.Daily.DailyBuilder.Build(store, Log);
                    return Core.Daily.DailyBuilder.Render(rep, store, OutDir, AppDataDir, Log);
                });
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
                Clipboard.SetText(File.ReadAllText(blast));
                StatusText.Text = $"blast copied (built {File.GetLastWriteTime(blast):HH:mm}) — paste into the Bloomberg chat.";
            }
            catch (Exception ex) { StatusText.Text = "copy failed: " + ex.Message; }
        }

        private void DailyEmail_Click(object sender, RoutedEventArgs e)
        {
            string? body = null;
            if (ReportStore.Load(Path.Combine(OutDir, Core.Daily.DailyBuilder.ReportFile)) is { } rep)
                body = WeeklyEmail.Html(rep, partsOpt: DailyParts());
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
                mail.Subject = $"DRAX Swaps — Daily OIS Run — {DateTime.Today:dd MMM yyyy}";
                mail.HTMLBody = "<html><body style=\"margin:14px;background:#ffffff;\">"
                    + body + "</body></html>";
                var book = _emailSettings.DailyXlsAttachment
                    ? Directory.EnumerateFiles(OutDir, "OIS_Runs_*.xlsx")
                        .OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                    : null;
                if (book != null) mail.Attachments.Add(book);
                mail.Display();
                StatusText.Text = "daily draft opened in Outlook — add recipients and send."
                    + (book != null ? " Workbook attached."
                       : _emailSettings.DailyXlsAttachment
                           ? " (no workbook found — run DAILY RUN)" : " Workbook attachment unticked.");
            }
            catch (Exception ex) { StatusText.Text = "daily email failed: " + ex.Message; }
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
