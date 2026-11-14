using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DAsk.Daemon.Automation.Desktop;
using DAsk.Daemon.Automation.Policy;
using Microsoft.Extensions.Logging;

namespace DAsk.Daemon.Automation.Android;

/// <summary>Runs adb with FIXED arguments (never a shell string). Behind an interface so the service can be tested without a phone.</summary>
public interface IAdbRunner
{
    string? AdbPath { get; }
    Task<(int Exit, string Out, string Err)> RunAsync(IReadOnlyList<string> args, int timeoutMs, CancellationToken ct);
}

public sealed class AdbRunner : IAdbRunner
{
    public string? AdbPath { get; }
    public AdbRunner(string? configured)
    {
        var candidates = new List<string?> { configured, Environment.GetEnvironmentVariable("ANDROID_HOME") is { } h ? Path.Combine(h, "platform-tools", "adb.exe") : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(d => d.Length > 0).Select(d => { try { return Path.Combine(d.Trim('"'), "adb.exe"); } catch { return null; } }));
        AdbPath = candidates.FirstOrDefault(p => p != null && File.Exists(p));
    }
    public async Task<(int, string, string)> RunAsync(IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
    {
        if (AdbPath == null) throw new InvalidOperationException("adb is not installed");
        var psi = new ProcessStartInfo(AdbPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException("adb did not answer in time"); }
        return (p.ExitCode, await o, await e);
    }
}

public sealed class AndroidOptions
{
    /// <summary>AgentHost:Android. Off by default.</summary>
    public bool Enabled { get; init; }
    /// <summary>Local folders files may be pushed from / pulled into (the knowledge/file roots). Empty: no file transfer.</summary>
    public IReadOnlyList<string> FileRoots { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Android devices through adb (2026-11-15), in the daemon. Discovery and device information are reads; connecting over the network, file
/// transfers and device commands each need the user's approval in the HUD. Android's own USB-debugging authorization is respected: an
/// "unauthorized" device is reported with what to do on the phone, never worked around. Only fixed adb argument lists are used (no
/// arbitrary shell); transfers are verified by SHA-256 on both sides; remote paths are limited to shared storage (/sdcard).
/// </summary>
public sealed class AndroidService : IDesktopActionRequestService
{
    public const string ListDevices = "android.list_devices", DeviceInfo = "android.device_info", Connect = "android.connect";
    public const string Push = "android.push", Pull = "android.pull", Command = "android.command";
    public static readonly IReadOnlyList<string> All = new[] { ListDevices, DeviceInfo, Connect, Push, Pull, Command };
    public static bool IsUnmetered(string tool) => tool is ListDevices or DeviceInfo;

    /// <summary>The only device commands: fixed argument lists (no text from the request reaches a shell).</summary>
    public static readonly IReadOnlyDictionary<string, (string Label, string[] Args)> Commands = new Dictionary<string, (string, string[])>
    {
        ["home"] = ("Go to the home screen", new[] { "shell", "input", "keyevent", "3" }),
        ["back"] = ("Press Back", new[] { "shell", "input", "keyevent", "4" }),
        ["wake"] = ("Wake the screen", new[] { "shell", "input", "keyevent", "224" }),
        ["lock"] = ("Turn the screen off (lock)", new[] { "shell", "input", "keyevent", "223" }),
        ["volume_up"] = ("Volume up", new[] { "shell", "input", "keyevent", "24" }),
        ["volume_down"] = ("Volume down", new[] { "shell", "input", "keyevent", "25" }),
    };

    private static readonly Regex Serial = new(@"^[A-Za-z0-9._:\-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex RemotePath = new(@"^/(sdcard|storage/emulated/0)(/[^\x00-\x1f""'`$\\;|&<>*?]{1,200})*$", RegexOptions.Compiled);

    private readonly ApprovalGate _gate;
    private readonly IAdbRunner _adb;
    private readonly AndroidOptions _o;
    private readonly ILogger? _log;
    private readonly string[] _roots;

    public AndroidService(ApprovalGate gate, IAdbRunner adb, AndroidOptions options, ILogger? logger = null)
    {
        _gate = gate; _adb = adb; _o = options; _log = logger;
        _roots = options.FileRoots.Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r)).Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToArray();
    }

    public IReadOnlyList<string> Tools => _o.Enabled && _gate.Available ? All : Array.Empty<string>();
    public int MaxCallMs => (int)ActionPolicyEngine.MaxApprovalTtl.TotalMilliseconds + 180_000;

    public sealed record Device(string Serial, string State, string? Model, string? Product, bool Wireless);

    /// <summary>Parses `adb devices -l`.</summary>
    public static List<Device> ParseDevices(string text) => text.Split('\n').Select(l => l.Trim()).SkipWhile(l => !l.StartsWith("List of devices", StringComparison.Ordinal)).Skip(1).Where(l => l.Length > 0 && !l.StartsWith('*'))
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 2)
        .Select(p => new Device(p[0], p[1], p.FirstOrDefault(x => x.StartsWith("model:"))?[6..], p.FirstOrDefault(x => x.StartsWith("product:"))?[8..], p[0].Contains(':'))).ToList();

    private static string? Str(JsonElement a, string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement a, string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
    private static DesktopCallResult Bad(string m) => DesktopCallResult.Fail("invalid_args", m);

    public async Task<DesktopCallResult> ExecuteAsync(string tool, JsonElement a, DesktopActionContext ctx, CancellationToken ct)
    {
        try
        {
            if (!All.Contains(tool)) return DesktopCallResult.Fail("unknown_tool", "This operation is not available.");
            if (!_o.Enabled) return DesktopCallResult.Fail("android_disabled", "Android device control is switched off in this installation (AgentHost:Android). Nothing was done.");
            if (_adb.AdbPath == null) return DesktopCallResult.Fail("adb_missing", "adb (Android SDK Platform-Tools) is not installed. Install it from developer.android.com/tools/releases/platform-tools (you accept Google's license there), then restart D-ASK.");
            if (a.ValueKind != JsonValueKind.Object) return Bad("arguments must be an object");
            return await RunAsync(tool, a, ctx, ct);
        }
        catch (OperationCanceledException) { return DesktopCallResult.Cancelled(); }
        catch (TimeoutException e) { return DesktopCallResult.Fail("timeout", e.Message); }
    }

    private async Task<List<Device>> DevicesAsync(CancellationToken ct) => ParseDevices((await _adb.RunAsync(new[] { "devices", "-l" }, 10_000, ct)).Out);

    /// <summary>The one device a request means: the named serial, or the only connected one. Unauthorized/offline devices are explained.</summary>
    private async Task<(Device? D, DesktopCallResult? Refusal)> TargetAsync(JsonElement a, CancellationToken ct)
    {
        string? serial = Str(a, "serial");
        if (serial != null && !Serial.IsMatch(serial)) return (null, Bad("serial is not a device serial"));
        var all = await DevicesAsync(ct);
        var d = serial != null ? all.FirstOrDefault(x => x.Serial == serial) : all.Count == 1 ? all[0] : null;
        if (d == null) return (null, DesktopCallResult.Fail(all.Count == 0 ? "no_device" : "ambiguous_target", all.Count == 0
            ? "No Android device is connected. Connect it by USB with USB debugging on (Settings > Developer options), or connect over Wi-Fi." : $"{all.Count} devices are connected; say which one (serials: {string.Join(", ", all.Select(x => x.Serial))})."));
        if (d.State == "unauthorized") return (null, DesktopCallResult.Fail("unauthorized", "The phone has not authorized this computer for USB debugging. Unlock the phone and accept the \"Allow USB debugging?\" prompt; D-ASK never bypasses it."));
        if (d.State != "device") return (null, DesktopCallResult.Fail("device_offline", $"The device is {d.State}. Reconnect it and try again."));
        return (d, null);
    }

    private string? InsideRoots(string local)
    {
        if (!Path.IsPathFullyQualified(local) || local.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        string full = Path.GetFullPath(local);
        return _roots.Any(r => full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ? full : null;
    }

    private async Task<string?> RemoteSha(Device d, string remote, CancellationToken ct)
    {
        var r = await _adb.RunAsync(new[] { "-s", d.Serial, "shell", "sha256sum", remote }, 60_000, ct);
        var m = Regex.Match(r.Out, "^([0-9a-fA-F]{64})");
        return r.Exit == 0 && m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }
    private static string LocalSha(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)); }

    private async Task<DesktopCallResult> RunAsync(string tool, JsonElement a, DesktopActionContext ctx, CancellationToken ct)
    {
        switch (tool)
        {
            case ListDevices:
            {
                var all = await DevicesAsync(ct);
                return DesktopCallResult.Ok(new { verified = true, count = all.Count, devices = all.Select(d => new { serial = d.Serial, state = d.State, model = d.Model, wireless = d.Wireless,
                    note = d.State == "unauthorized" ? "accept the USB-debugging prompt on the phone" : null }), observed = all.Count == 0 ? "no Android device is connected" : $"{all.Count} device(s) seen by adb" });
            }
            case DeviceInfo:
            {
                var (d, refusal) = await TargetAsync(a, ct);
                if (refusal != null) return refusal;
                async Task<string> Prop(string p) => (await _adb.RunAsync(new[] { "-s", d!.Serial, "shell", "getprop", p }, 10_000, ct)).Out.Trim();
                var battery = (await _adb.RunAsync(new[] { "-s", d!.Serial, "shell", "dumpsys", "battery" }, 10_000, ct)).Out;
                var storage = (await _adb.RunAsync(new[] { "-s", d.Serial, "shell", "df", "-h", "/sdcard" }, 10_000, ct)).Out.Split('\n').Skip(1).FirstOrDefault()?.Trim();
                int? level = Regex.Match(battery, @"level:\s*(\d+)") is { Success: true } lm ? int.Parse(lm.Groups[1].Value) : null;
                return DesktopCallResult.Ok(new { verified = true, serial = d.Serial, manufacturer = await Prop("ro.product.manufacturer"), model = await Prop("ro.product.model"),
                    android = await Prop("ro.build.version.release"), sdk = await Prop("ro.build.version.sdk"), batteryPercent = level, charging = Regex.IsMatch(battery, @"(AC|USB|Wireless) powered:\s*true"), storage,
                    observed = "read from the device with adb" });
            }
            case Connect:
            {
                string? host = Str(a, "host"); int port = Int(a, "port") ?? 5555;
                if (host == null || !System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return Bad("host must be the phone's IPv4 address");
                var b = ip.GetAddressBytes();
                bool priv = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                if (!priv) return DesktopCallResult.Fail("not_local", "Only phones on your local network (10.x, 172.16-31.x, 192.168.x) can be connected.");
                if (port is < 1024 or > 65535) return Bad("port must be 1024-65535");
                var parameters = new Dictionary<string, string> { ["operation"] = $"Connect to the Android device at {host}:{port} over Wi-Fi (adb)", ["note"] = "The phone must already allow wireless debugging; it may show its own prompt.", ["execution"] = "adb connect once; the device list is then read back." };
                var (req, refusal) = await _gate.RequestAsync(DesktopActionTypes.AndroidConnect, parameters, "adb", $"{host}:{port}", "D-ASK asks to connect to an Android device.", ctx, ct);
                if (refusal != null) return refusal;
                await _adb.RunAsync(new[] { "connect", $"{host}:{port}" }, 15_000, ct);
                var dev = (await DevicesAsync(ct)).FirstOrDefault(x => x.Serial == $"{host}:{port}");
                await _gate.AuditAsync(dev?.State == "device" ? "Verified" : "Unverified", req!, ctx, dev?.State ?? "not listed");
                return dev?.State == "device" ? DesktopCallResult.Ok(new { executed = true, verified = true, serial = dev.Serial, observed = "the device is connected (listed by adb)" })
                    : DesktopCallResult.Fail(dev?.State == "unauthorized" ? "unauthorized" : "execution_unverified", dev?.State == "unauthorized" ? "Connected, but the phone has not authorized this computer: accept the prompt on the phone." : "adb did not list the device as connected.");
            }
            case Push:
            {
                var (d, refusal) = await TargetAsync(a, ct);
                if (refusal != null) return refusal;
                string? local = Str(a, "file") is { } f ? InsideRoots(f) : null;
                if (local == null || !File.Exists(local)) return Bad("file must be an existing file inside the folders D-ASK may use");
                string dest = Str(a, "toFolder") ?? "/sdcard/Download";
                if (!RemotePath.IsMatch(dest)) return Bad("the destination must be a folder under /sdcard");
                string remote = $"{dest.TrimEnd('/')}/{Path.GetFileName(local)}";
                if (!RemotePath.IsMatch(remote)) return Bad("that file name cannot be used on the device");
                string sha = LocalSha(local);
                var parameters = new Dictionary<string, string> { ["operation"] = $"Copy {Path.GetFileName(local)} to {d!.Model ?? d.Serial} at {remote}", ["file"] = local, ["size"] = $"{new FileInfo(local).Length} bytes",
                    ["device"] = $"{d.Model ?? "device"} ({d.Serial})", ["destination"] = remote, ["overwrite"] = "A file with that name on the device is replaced.", ["execution"] = "Copied once; the SHA-256 on the phone is compared with the original." };
                var (req, r2) = await _gate.RequestAsync(DesktopActionTypes.AndroidTransfer, parameters, "adb", d.Serial, "D-ASK asks to copy a file to your phone.", ctx, ct);
                if (r2 != null) return r2;
                var r = await _adb.RunAsync(new[] { "-s", d.Serial, "push", local, remote }, 170_000, ct);
                string? remoteSha = r.Exit == 0 ? await RemoteSha(d, remote, ct) : null;
                bool ok = remoteSha == sha;
                await _gate.AuditAsync(ok ? "Verified" : "Unverified", req!, ctx, $"push exit {r.Exit}");
                return ok ? DesktopCallResult.Ok(new { executed = true, verified = true, remote, sha256 = sha, observed = "the copy on the device has the same SHA-256 as the original" })
                    : DesktopCallResult.Fail("execution_unverified", r.Exit != 0 ? $"adb push failed: {(r.Err + r.Out).Trim().Split('\n').Last()}" : "The file was copied, but its SHA-256 on the device could not be confirmed.");
            }
            case Pull:
            {
                var (d, refusal) = await TargetAsync(a, ct);
                if (refusal != null) return refusal;
                string? remote = Str(a, "remotePath");
                if (remote == null || !RemotePath.IsMatch(remote)) return Bad("remotePath must be a file under /sdcard");
                string? folder = Str(a, "toFolder") is { } tf ? InsideRoots(Path.Combine(tf, "x")) is { } inside ? Path.GetDirectoryName(inside) : null : null;
                if (folder == null || !Directory.Exists(folder)) return Bad("toFolder must be an existing folder inside the folders D-ASK may use");
                string local = Path.Combine(folder, remote.Split('/').Last());
                if (File.Exists(local)) return DesktopCallResult.Fail("exists", $"{local} already exists; D-ASK never overwrites it.");
                var parameters = new Dictionary<string, string> { ["operation"] = $"Copy {remote} from {d!.Model ?? d.Serial} to {local}", ["device"] = $"{d.Model ?? "device"} ({d.Serial})", ["source"] = remote, ["destination"] = local,
                    ["execution"] = "Copied once; the SHA-256 on this computer is compared with the phone's." };
                var (req, r2) = await _gate.RequestAsync(DesktopActionTypes.AndroidTransfer, parameters, "adb", d.Serial, "D-ASK asks to copy a file from your phone.", ctx, ct);
                if (r2 != null) return r2;
                string? remoteSha = await RemoteSha(d, remote, ct);
                var r = await _adb.RunAsync(new[] { "-s", d.Serial, "pull", remote, local }, 170_000, ct);
                bool ok = r.Exit == 0 && File.Exists(local) && remoteSha != null && LocalSha(local) == remoteSha;
                await _gate.AuditAsync(ok ? "Verified" : "Unverified", req!, ctx, $"pull exit {r.Exit}");
                return ok ? DesktopCallResult.Ok(new { executed = true, verified = true, local, sha256 = remoteSha, observed = "the copy on this computer has the same SHA-256 as the file on the phone" })
                    : DesktopCallResult.Fail("execution_unverified", r.Exit != 0 ? $"adb pull failed: {(r.Err + r.Out).Trim().Split('\n').Last()}" : "The file was copied, but its SHA-256 could not be confirmed.");
            }
            case Command:
            {
                var (d, refusal) = await TargetAsync(a, ct);
                if (refusal != null) return refusal;
                string? action = Str(a, "action");
                string? url = Str(a, "url");
                string[] args; string label;
                if (action == "open_url")
                {
                    if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || url.Length > 500 || url.IndexOfAny(new[] { '\'', '"', '`', ' ', ';', '&', '|' }) >= 0) return Bad("url must be a plain https address");
                    args = new[] { "shell", "am", "start", "-a", "android.intent.action.VIEW", "-d", url }; label = $"Open {u.Host} in the phone's browser";
                }
                else if (action != null && Commands.TryGetValue(action, out var c)) { args = c.Args; label = c.Label; }
                else return Bad($"action must be one of: {string.Join(", ", Commands.Keys)}, open_url");
                var parameters = new Dictionary<string, string> { ["operation"] = $"{label} on {d!.Model ?? d.Serial}", ["device"] = $"{d.Model ?? "device"} ({d.Serial})", ["command"] = $"adb {string.Join(' ', args)}", ["execution"] = "Sent once; adb's exit code is reported." };
                var (req, r2) = await _gate.RequestAsync(DesktopActionTypes.AndroidCommand, parameters, "adb", d.Serial, "D-ASK asks to act on your phone.", ctx, ct);
                if (r2 != null) return r2;
                var r = await _adb.RunAsync(new[] { "-s", d.Serial }.Concat(args).ToArray(), 15_000, ct);
                bool ok = r.Exit == 0 && !Regex.IsMatch(r.Out + r.Err, @"Error|Exception|not found", RegexOptions.IgnoreCase);
                await _gate.AuditAsync(ok ? "Executed" : "Failed", req!, ctx, $"exit {r.Exit}");
                // adb reports that the event was delivered; it cannot see the phone's screen, so this is "sent", not "verified on screen".
                return ok ? DesktopCallResult.Ok(new { executed = true, verified = true, action, observed = "adb delivered the command to the device (exit code 0); D-ASK cannot see the phone's screen" })
                    : DesktopCallResult.Fail("execution_failed", $"adb reported a failure: {(r.Err + r.Out).Trim().Split('\n').Last()}");
            }
        }
        return DesktopCallResult.Fail("unknown_tool", "This operation is not available.");
    }
}
