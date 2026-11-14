using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using DAsk.Daemon.Automation.Chrome;
using DAsk.Daemon.Automation.Desktop;
using DAsk.Daemon.Automation.Observation;
using DAsk.Daemon.Automation.Policy;
using DAsk.Daemon.Research;
using DAsk.Protocol.IPC.Messages.ComputerUse;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DAsk.Daemon.Tests;

/// <summary>Runs only when DASK_LIVE_CHROME=1: it starts the real installed Chrome with a throwaway profile.</summary>
public sealed class LiveChromeFactAttribute : FactAttribute
{
    public LiveChromeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DASK_LIVE_CHROME") != "1")
            Skip = "Real Chrome test: set DASK_LIVE_CHROME=1 (needs scripts\\chrome\\register-native-host.ps1 run once and the DAsk.ChromeHost Release build).";
    }
}

/// <summary>
/// The user's Chrome bridge end to end on the REAL installed Chrome (2026-11-14): extension + native messaging host + daemon broker +
/// ChromeService. Chrome runs with a THROWAWAY profile (the test harness installs the unpacked extension over a CDP pipe because Chrome 137+
/// removed --load-extension; the product never uses CDP). Pages are served by this test on 127.0.0.1. The TEST plays the HUD.
/// </summary>
[Collection("AgentHost")]
public class ChromeLiveTests
{
    private readonly ITestOutputHelper _out;
    public ChromeLiveTests(ITestOutputHelper o) => _out = o;
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    /// <summary>DASK_CHROME_REAL_PROFILE=1: run against the user's normal Chrome profile (extension loaded there once by the user).</summary>
    private static bool RealProfile => Environment.GetEnvironmentVariable("DASK_CHROME_REAL_PROFILE") == "1";
    /// <summary>DASK_CHROME_HOST_EXE: test a packaged relay instead of the development build.</summary>
    private static string? HostOverride => Environment.GetEnvironmentVariable("DASK_CHROME_HOST_EXE");

    private const string Page1 = """
        <!doctype html><html><head><title>D-ASK Test Page 1</title></head><body>
        <h1>Hello from page one</h1>
        <p><a href="/page2">Go to page two</a></p>
        <form action="/submitted" method="get" onsubmit="return false">
          <label for="name">Your name</label> <input id="name" name="name">
          <label for="pw">Password</label> <input id="pw" name="pw" type="password">
        </form>
        <button type="button" onclick="document.getElementById('result').textContent='BOUGHT'">Buy now</button>
        <p id="result">nothing bought</p>
        </body></html>
        """;
    private const string Page2 = "<!doctype html><html><head><title>D-ASK Test Page 2</title></head><body><h1>This is page two</h1></body></html>";

    [LiveChromeFact]
    [Trait("Category", "ChromeLive")]
    public async Task Live_UsersChrome_Navigate_Read_Click_Tabs_Fill_Sensitive_Security_Stop()
    {
        string hostExe = HostOverride ?? Path.Combine(Root, "src", "DAsk.ChromeHost", "bin", "Release", "net8.0", "dask-chrome-host.exe");
        Assert.True(File.Exists(hostExe), "build src/DAsk.ChromeHost in Release first");
        string chromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        string db = Path.Combine(Path.GetTempPath(), $"dask-chrome-live-{Guid.NewGuid():N}.db");
        int port = 18000 + Random.Shared.Next(2000);
        using var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext c;
                try { c = await http.GetContextAsync(); } catch { return; }
                var body = Encoding.UTF8.GetBytes(c.Request.Url!.AbsolutePath == "/page2" ? Page2 : Page1);
                c.Response.ContentType = "text/html; charset=utf-8";
                await c.Response.OutputStream.WriteAsync(body);
                c.Response.Close();
            }
        });
        Process? harness = null;
        var broker = new ChromeBroker(new ChromeBrokerOptions { HostExePath = hostExe, ExtensionId = "lkiheaganheamabgecpnafiefeiadlpi", ChromeExePaths = new[] { chromeExe } });
        try
        {
            var recorder = new SqliteEvidenceRecorder(NullLogger<SqliteEvidenceRecorder>.Instance, db);
            await recorder.InitializeAsync();
            var policy = new ActionPolicyEngine(NullLogger<ActionPolicyEngine>.Instance, recorder, new Win32WindowSource());
            policy.SetSessionLivenessGate(_ => true);
            var asked = new ConcurrentQueue<string>();
            Func<string, bool> decide = _ => true;
            policy.ApprovalRequested += (_, r) => Task.Run(async () =>
            {
                await Task.Delay(100);
                asked.Enqueue(r.ActionType);
                policy.SubmitApprovalDecision(new ActionApprovalResponse
                {
                    ApprovalId = r.ApprovalId, ActionId = r.ActionId, ActionType = r.ActionType, ParametersSha256 = r.ParametersSha256,
                    ActionDigest = r.ActionDigest, Approved = decide(r.ActionType), DecisionSource = "UserExplicitConfirmation",
                }, r.SessionId, () => false, () => 1);   // the owning HUD session decides (two sessions are used below)
            });
            bool stopped = false, online = true;
            broker.Start();
            var svc = new ChromeService(new ApprovalGate(policy, () => stopped, () => 1, recorder, _ => true, "Chrome"), broker,
                new ChromeOptions { Enabled = true, ChromeExePath = chromeExe, WebSearchAllowed = false, IsOnline = () => online });
            var ctx = new DesktopActionContext("live-task", "c1", "live-session");
            async Task<DesktopCallResult> Do(string tool, object args)
            {
                var r = await svc.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args), ctx, CancellationToken.None);
                string s = JsonSerializer.Serialize((object?)r.Result ?? r.Error);
                _out.WriteLine($"{tool} {JsonSerializer.Serialize(args)}: {r.Status} {(s.Length > 500 ? s[..500] + "…" : s)}");
                return r;
            }
            JsonElement Res(DesktopCallResult r) => JsonSerializer.SerializeToElement(r.Result);

            // ── 9. an unauthorized local client cannot use the broker pipe (it is disconnected before anything is read) ──
            using (var rogue = new NamedPipeClientStream(".", broker.PipeName, PipeDirection.InOut))
            {
                await rogue.ConnectAsync(3000);
                var hello = Encoding.UTF8.GetBytes("{\"host\":\"dask-chrome-host\",\"origin\":\"chrome-extension://lkiheaganheamabgecpnafiefeiadlpi/\"}");
                try { await rogue.WriteAsync(BitConverter.GetBytes(hello.Length)); await rogue.WriteAsync(hello); } catch { }
                var buf = new byte[4];
                int got; try { got = await rogue.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); } catch { got = 0; }
                Assert.Equal(0, got);
            }
            for (int i = 0; i < 20 && broker.Rejected == 0; i++) await Task.Delay(100);
            Assert.True(broker.Rejected >= 1, "the rogue client must be rejected");
            Assert.False(broker.Connected);
            Assert.Equal("not_connected", (await Do(ChromeService.ListTabs, new { })).Error?.Code);

            // ── 1. launch the real Chrome with the extension; the extension connects through the native host ──
            // Default: a THROWAWAY profile (harness). DASK_CHROME_REAL_PROFILE=1: the user's normal profile, where the user loaded the extension
            // once; Chrome is opened normally (no harness, no CDP), only local test pages are opened, and only the tabs this test opened are closed.
            if (RealProfile)
            {
                using (Process.Start(new ProcessStartInfo(chromeExe) { UseShellExecute = false })) { }
                _out.WriteLine("real profile: opened Chrome normally; waiting for the installed D-ASK extension");
            }
            else
            {
                harness = Process.Start(new ProcessStartInfo("node", $"\"{Path.Combine(Root, "scripts", "chrome", "test-chrome-with-extension.mjs")}\" \"{chromeExe}\"")
                    { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, WorkingDirectory = Root })!;
                string? line = await harness.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
                _out.WriteLine("harness: " + line);
                Assert.StartsWith("READY", line);
            }
            for (int i = 0; i < 300 && !broker.Connected; i++) await Task.Delay(200);   // the extension retries every 30 s
            Assert.True(broker.Connected, "the extension must connect through the native host");
            _out.WriteLine($"connected: chrome pid {broker.ChromePid} ({broker.ChromePath})");
            Assert.Equal(chromeExe, broker.ChromePath, ignoreCase: true);

            // Without an authorized session the extension refuses commands (even if someone had the pipe).
            var raw = await broker.SendAsync("listTabs", new { }, "0000000000000000000000000000000000000000000000000000000000000000", 5000, CancellationToken.None);
            Assert.False(raw.Ok); Assert.StartsWith("not_authorized", raw.Error);

            // Authorization declined: nothing happens.
            decide = t => t != DesktopActionTypes.ChromeAuthorize;
            Assert.Equal("approval_denied", (await Do(ChromeService.ListTabs, new { })).Error?.Code);
            decide = _ => true;

            // Everyday profile: close stale tabs a previous (failed) run of THIS test left: only its own local test pages.
            if (RealProfile)
            {
                var stale = await Do(ChromeService.ListTabs, new { });
                if (stale.Status == "ok")
                    foreach (var t in Res(stale).GetProperty("tabs").EnumerateArray()
                        .Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.GetProperty("url").GetString() ?? "", @"^http://127\.0\.0\.1:1[89]\d{3}/page[12]$")))
                        await Do(ChromeService.CloseTab, new { tabId = t.GetProperty("tabId").GetInt32() });
            }

            // ── 2/3. navigate and read ──
            var open = await Do(ChromeService.OpenTab, new { url = $"http://127.0.0.1:{port}/page1" });
            Assert.Equal("ok", open.Status);
            Assert.Equal("D-ASK Test Page 1", Res(open).GetProperty("title").GetString());
            int tab1 = Res(open).GetProperty("tabId").GetInt32();
            var read = await Do(ChromeService.ReadPage, new { tabId = tab1 });
            Assert.Equal("ok", read.Status);
            Assert.Contains("Hello from page one", Res(read).GetProperty("page").GetProperty("text").GetString());
            int approvalsAfterAuth = asked.Count;

            // ── 4. click a link (ordinary navigation: no extra approval) ──
            var click = await Do(ChromeService.Click, new { tabId = tab1, text = "Go to page two" });
            Assert.Equal("ok", click.Status);
            Assert.EndsWith("/page2", Res(click).GetProperty("urlAfter").GetString());
            Assert.Equal(approvalsAfterAuth, asked.Count);

            // ── 5. tabs: open another, list, switch back by title ──
            var open2 = await Do(ChromeService.OpenTab, new { url = $"http://127.0.0.1:{port}/page1" });
            int tab2 = Res(open2).GetProperty("tabId").GetInt32();
            var tabs = await Do(ChromeService.ListTabs, new { });
            Assert.True(Res(tabs).GetProperty("count").GetInt32() >= 2);
            // An authorized session belongs to the HUD session that approved it: another client's task must ask for its own authorization.
            int auths = asked.Count(t => t == DesktopActionTypes.ChromeAuthorize);
            var other = await svc.ExecuteAsync(ChromeService.ListTabs, JsonSerializer.SerializeToElement(new { }), new DesktopActionContext("other-task", "c9", "live-session-2"), CancellationToken.None);
            Assert.Equal("ok", other.Status);
            Assert.Equal(auths + 1, asked.Count(t => t == DesktopActionTypes.ChromeAuthorize));
            var sw = await Do(ChromeService.SwitchTab, new { title = "Page 2" });   // the first session must authorize again too
            Assert.Equal(auths + 2, asked.Count(t => t == DesktopActionTypes.ChromeAuthorize));
            Assert.Equal("ok", sw.Status);
            Assert.Equal(tab1, Res(sw).GetProperty("tabId").GetInt32());

            // ── 6/7. harmless form fill, verified by reading the field back; password fields are never filled ──
            var fill = await Do(ChromeService.Fill, new { tabId = tab2, field = "Your name", text = "Test User" });
            Assert.Equal("ok", fill.Status);
            var read2 = await Do(ChromeService.ReadPage, new { tabId = tab2 });
            Assert.Contains("Test User", JsonSerializer.Serialize(Res(read2).GetProperty("page").GetProperty("fields")));
            Assert.Equal("not_filled", (await Do(ChromeService.Fill, new { tabId = tab2, field = "Password", text = "abc12345" })).Error?.Code);
            Assert.Equal("secret_refused", (await Do(ChromeService.Fill, new { tabId = tab2, field = "Your name", text = "password = hunter2hunter2" })).Error?.Code);

            // ── 8. a sensitive click needs its own approval: declined -> not clicked; approved -> clicked and verified ──
            decide = t => t != DesktopActionTypes.ChromeSensitive;
            Assert.Equal("approval_denied", (await Do(ChromeService.Click, new { tabId = tab2, text = "Buy now" })).Error?.Code);
            Assert.Contains("nothing bought", Res(await Do(ChromeService.ReadPage, new { tabId = tab2 })).GetProperty("page").GetProperty("text").GetString());
            decide = _ => true;
            Assert.Equal("ok", (await Do(ChromeService.Click, new { tabId = tab2, text = "Buy now" })).Status);
            Assert.Contains("BOUGHT", Res(await Do(ChromeService.ReadPage, new { tabId = tab2 })).GetProperty("page").GetProperty("text").GetString());
            Assert.Contains(DesktopActionTypes.ChromeSensitive, asked);

            // ── offline guard: web search is off (offline-first) and an offline computer opens no web pages ──
            Assert.Equal("web_search_off", (await Do(ChromeService.Search, new { query = "weather" })).Error?.Code);
            online = false;
            Assert.Equal("offline", (await Do(ChromeService.Navigate, new { tabId = tab2, url = "https://example.com" })).Error?.Code);
            online = true;

            // ── 10. no TCP listener of Chrome, the host or this bridge (no debugging port anywhere) ──
            var pids = Process.GetProcessesByName("chrome").Select(p => p.Id).Concat(Process.GetProcessesByName("dask-chrome-host").Select(p => p.Id)).ToHashSet();
            var listening = Netstat().Where(l => pids.Contains(l.Pid)).ToList();
            _out.WriteLine($"listening sockets owned by chrome/host: {listening.Count} {string.Join(", ", listening.Select(l => l.Local))}");
            Assert.Empty(listening);

            // ── 11. Emergency Stop: the session is revoked in the daemon AND in the extension; nothing more runs ──
            stopped = true;
            for (int i = 0; i < 20 && svc.SessionActive; i++) await Task.Delay(100);
            Assert.False(svc.SessionActive);
            Assert.Equal("emergency_stopped", (await Do(ChromeService.ListTabs, new { })).Error?.Code);
            await Task.Delay(300);
            var afterStop = await broker.SendAsync("listTabs", new { }, null, 5000, CancellationToken.None);
            Assert.False(afterStop.Ok); Assert.StartsWith("not_authorized", afterStop.Error);
            stopped = false;

            // ── 12. reauthorization after Emergency Stop: a new approval is asked, then actions work again ──
            int before = asked.Count(t => t == DesktopActionTypes.ChromeAuthorize);
            Assert.Equal("ok", (await Do(ChromeService.ListTabs, new { })).Status);
            Assert.Equal(before + 1, asked.Count(t => t == DesktopActionTypes.ChromeAuthorize));

            // ── 13. recovery: the relay process dies -> the bridge disconnects, the session ends; the extension reconnects by itself and a
            //        new session must be authorized again ──
            foreach (var h in Process.GetProcessesByName("dask-chrome-host")) { try { h.Kill(); } catch { } }
            for (int i = 0; i < 50 && broker.Connected; i++) await Task.Delay(100);
            Assert.False(broker.Connected);
            Assert.False(svc.SessionActive);
            for (int i = 0; i < 300 && !broker.Connected; i++) await Task.Delay(200);
            Assert.True(broker.Connected, "the extension must reconnect after the relay died");
            Assert.Equal("ok", (await Do(ChromeService.ListTabs, new { })).Status);
            Assert.Equal(before + 2, asked.Count(t => t == DesktopActionTypes.ChromeAuthorize));

            // ── 13b. everyday profile only, online: a REAL site. Read it, extract its text, follow its harmless link, close only that tab. ──
            if (RealProfile && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                var site = await Do(ChromeService.OpenTab, new { url = "https://example.com/" });
                Assert.Equal("ok", site.Status);
                int siteTab = Res(site).GetProperty("tabId").GetInt32();
                var page = await Do(ChromeService.ReadPage, new { tabId = siteTab });
                Assert.Equal("ok", page.Status);
                string text = Res(page).GetProperty("page").GetProperty("text").GetString() ?? "";
                _out.WriteLine("example.com text: " + text.Replace("\n", " / "));
                Assert.Contains("Example Domain", Res(page).GetProperty("page").GetProperty("title").GetString());
                Assert.Contains("documentation examples", text);
                var links = Res(page).GetProperty("page").GetProperty("links").EnumerateArray().ToList();
                Assert.NotEmpty(links);
                string linkText = links[0].GetProperty("text").GetString()!;
                var follow = await Do(ChromeService.Click, new { tabId = siteTab, text = linkText });
                Assert.Equal("ok", follow.Status);
                Assert.NotEqual("https://example.com/", Res(follow).GetProperty("urlAfter").GetString());
                Assert.Equal("ok", (await Do(ChromeService.CloseTab, new { tabId = siteTab })).Status);
            }

            // ── 14. cleanup: close exactly the tabs this test opened, revoke explicitly; in throwaway mode closing Chrome disconnects the bridge ──
            foreach (var t in new[] { tab1, tab2 }) Assert.Equal("ok", (await Do(ChromeService.CloseTab, new { tabId = t })).Status);
            var rev = await Do(ChromeService.Revoke, new { });
            Assert.True(Res(rev).GetProperty("extensionConfirmed").GetBoolean());
            Assert.False(svc.SessionActive);
            if (!RealProfile)
            {
                harness!.StandardInput.Close();
                for (int i = 0; i < 50 && broker.Connected; i++) await Task.Delay(200);
                Assert.False(broker.Connected);
            }
            _out.WriteLine("approvals asked: " + string.Join(",", asked));
            Assert.True((await recorder.VerifyAuditChainAsync()).Ok);
        }
        finally
        {
            try { harness?.StandardInput.Close(); } catch { }
            try { if (harness is { HasExited: false } && !harness.WaitForExit(5000)) harness.Kill(true); } catch { }
            broker.Dispose();
            http.Stop();
            SqliteConnection.ClearAllPools(); try { File.Delete(db); } catch { }
        }
    }

    private static List<(string Local, int Pid)> Netstat()
    {
        var p = Process.Start(new ProcessStartInfo("netstat", "-ano -p TCP") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        var p6 = Process.Start(new ProcessStartInfo("netstat", "-ano -p TCPv6") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        text += p6.StandardOutput.ReadToEnd(); p6.WaitForExit();
        return text.Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(f => f.Length >= 5 && f[3] == "LISTENING" && int.TryParse(f[4], out _))
            .Select(f => (f[1], int.Parse(f[4]))).ToList();
    }
}
