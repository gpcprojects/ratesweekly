using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using RateDesk.Core;
using RateDesk.Core.Market;
using RateDesk.Weekly.Core;
using RateDesk.Weekly.Core.Render;

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
            log($"rendered {n} page(s) as of {asOf:yyyy-MM-dd}");
            return n;
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
