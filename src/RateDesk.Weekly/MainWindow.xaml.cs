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

        public MainWindow()
        {
            InitializeComponent();
            VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
            Directory.CreateDirectory(AppDataDir);
            Directory.CreateDirectory(OutDir);
            // one line per button; the first UPDATE clears these and takes the box over
            LogBox.Text =
                "UPDATE — pulls Bloomberg, brings the history current, redraws every dashboard and builds the desk email.\r\n" +
                "CREATE EMAIL — opens a ready Outlook draft: body filled in, dashboards file attached.\r\n" +
                "COPY EMAIL — copies the built email to the clipboard, for pasting into an existing draft.\r\n" +
                "OPEN OUTPUT — opens the dashboards in your browser.\r\n";
        }

        private void Log(string s) => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
            LogBox.ScrollToEnd();
        });

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            _updating = true;
            UpdateBtn.IsEnabled = false;
            LogBox.Clear();   // the startup instructions give way to the run log
            StatusText.Text = "updating...";
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
            }
            catch (Exception ex)
            {
                StatusText.Text = "update failed: " + ex.Message;
                Log("! update failed: " + ex.Message);
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
        private void CreateEmail_Click(object sender, RoutedEventArgs e)
        {
            var frag = Path.Combine(OutDir, EmailBuilder.FragmentFile);
            if (!File.Exists(frag))
            {
                StatusText.Text = "no email built yet — run UPDATE first.";
                return;
            }
            try
            {
                var t = Type.GetTypeFromProgID("Outlook.Application")
                    ?? throw new InvalidOperationException("Outlook is not installed on this machine");
                dynamic outlook = Activator.CreateInstance(t)!;
                dynamic mail = outlook.CreateItem(0); // olMailItem
                mail.Subject = $"DRAX Swaps — Rates Weekly — {File.GetLastWriteTime(frag):dd MMM yyyy}";
                mail.HTMLBody = "<html><body style=\"margin:14px;background:#ffffff;\">"
                    + File.ReadAllText(frag) + "</body></html>";
                var pack = Path.Combine(OutDir, SiteFile.FileName);
                bool attached = File.Exists(pack);
                if (attached) mail.Attachments.Add(pack);
                mail.Display();
                StatusText.Text = "draft opened in Outlook — add recipients and send."
                    + (attached ? " Dashboards pack attached." : " (no dashboards pack found — run UPDATE)");
            }
            catch (Exception ex) { StatusText.Text = "create email failed: " + ex.Message; }
        }

        private void CopyEmail_Click(object sender, RoutedEventArgs e)
        {
            // Copies the PERSISTED fragment — never rebuilds. What UPDATE wrote is what pastes,
            // and it still pastes after an app restart.
            var frag = Path.Combine(OutDir, EmailBuilder.FragmentFile);
            var txt = Path.Combine(OutDir, EmailBuilder.PlainTextFile);
            if (!File.Exists(frag))
            {
                StatusText.Text = "no email built yet — run UPDATE first.";
                return;
            }
            try
            {
                ClipboardHtml.Set(File.ReadAllText(frag), File.Exists(txt) ? File.ReadAllText(txt) : "");
                StatusText.Text = $"email copied (built {File.GetLastWriteTime(frag):ddd HH:mm}) — paste into the email body.";
            }
            catch (Exception ex) { StatusText.Text = "copy failed: " + ex.Message; }
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
