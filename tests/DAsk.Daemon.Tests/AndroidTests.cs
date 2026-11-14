using System.Security.Cryptography;
using System.Text.Json;
using DAsk.Daemon.Automation.Android;
using DAsk.Daemon.Automation.Desktop;
using DAsk.Daemon.Automation.Policy;
using DAsk.Daemon.Research;
using DAsk.Protocol.IPC.Messages.ComputerUse;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DAsk.Daemon.Tests;

/// <summary>
/// Android through adb (2026-11-15). The fast tests use a FAKE adb that simulates a phone (devices list, getprop, a /sdcard file system,
/// sha256sum), so they prove D-ASK's own logic: authorization states, approvals, fixed argument lists, root confinement, SHA-256 verification.
/// They do NOT prove a real phone works: <see cref="RealDevice_ReadOnlyAndRoundTrip"/> needs DASK_LIVE_ANDROID=1, adb and an authorized phone.
/// </summary>
public class AndroidTests
{
    private const string Owner = "hud-owner";
    private static readonly DesktopActionContext Ctx = new("t1", "c1", Owner);

    private sealed class FakeAdb : IAdbRunner
    {
        public string? AdbPath { get; set; } = @"C:\fake\adb.exe";
        public string DevicesOut = "List of devices attached\nR58M123ABC device usb:1-1 product:a52q model:SM_A525F device:a52q transport_id:1\n";
        public readonly Dictionary<string, byte[]> Phone = new();
        public readonly List<string[]> Calls = new();
        public bool CorruptPush;
        public Task<(int, string, string)> RunAsync(IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
        {
            var a = args.ToArray(); lock (Calls) Calls.Add(a);
            if (a[0] == "devices") return R(0, DevicesOut);
            if (a[0] == "connect") { DevicesOut += $"{a[1]} device product:x model:Pixel_8 device:x transport_id:2\n"; return R(0, $"connected to {a[1]}"); }
            Assert.Equal("-s", a[0]);
            var rest = a.Skip(2).ToArray();
            switch (rest[0])
            {
                case "shell" when rest[1] == "getprop":
                    return R(0, rest[2] switch { "ro.product.model" => "SM-A525F\n", "ro.product.manufacturer" => "samsung\n", "ro.build.version.release" => "14\n", "ro.build.version.sdk" => "34\n", _ => "\n" });
                case "shell" when rest[1] == "dumpsys": return R(0, "Current Battery Service state:\n  AC powered: false\n  USB powered: true\n  level: 81\n");
                case "shell" when rest[1] == "df": return R(0, "Filesystem Size Used Avail Use% Mounted on\n/dev/fuse 110G 40G 70G 37% /storage/emulated\n");
                case "shell" when rest[1] == "sha256sum":
                    return Phone.TryGetValue(rest[2], out var b) ? R(0, $"{Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()}  {rest[2]}\n") : R(1, "", "No such file");
                case "shell" when rest[1] == "input" || rest[1] == "am": return R(0, "");
                case "push": { var pb = File.ReadAllBytes(rest[1]); if (CorruptPush) pb = pb.Append((byte)0).ToArray(); Phone[rest[2]] = pb; return R(0, "1 file pushed"); }
                case "pull": if (!Phone.TryGetValue(rest[1], out var p)) return R(1, "", "remote object does not exist"); File.WriteAllBytes(rest[2], p); return R(0, "1 file pulled");
            }
            return R(1, "", "unexpected");
        }
        private static Task<(int, string, string)> R(int e, string o, string err = "") => Task.FromResult((e, o, err));
    }

    private sealed class Rig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), $"dask-adb-{Guid.NewGuid():N}");
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"dask-adb-{Guid.NewGuid():N}.db");
        public readonly SqliteEvidenceRecorder Recorder;
        public readonly ActionPolicyEngine Policy;
        public readonly AndroidService Svc;
        public readonly IAdbRunner Adb;
        public readonly List<ActionApprovalRequest> Cards = new();
        public Func<ActionApprovalRequest, bool> Decide = _ => true;
        public Rig(IAdbRunner? adb = null, bool enabled = true)
        {
            Directory.CreateDirectory(Path.Combine(Root, "files"));
            Recorder = new SqliteEvidenceRecorder(NullLogger<SqliteEvidenceRecorder>.Instance, _db);
            Recorder.InitializeAsync().GetAwaiter().GetResult();
            Policy = new ActionPolicyEngine(NullLogger<ActionPolicyEngine>.Instance, Recorder);
            Policy.SetSessionLivenessGate(s => s == Owner);
            Policy.ApprovalRequested += (_, r) => Task.Run(async () =>
            {
                lock (Cards) Cards.Add(r);
                await Task.Delay(30);
                Policy.SubmitApprovalDecision(new ActionApprovalResponse { ApprovalId = r.ApprovalId, ActionId = r.ActionId, ActionType = r.ActionType, ParametersSha256 = r.ParametersSha256,
                    ActionDigest = r.ActionDigest, Approved = Decide(r), DecisionSource = "UserExplicitConfirmation" }, Owner, () => false, () => 1);
            });
            var gate = new ApprovalGate(Policy, () => false, () => 1, Recorder, s => s == Owner, "Android");
            Adb = adb ?? new FakeAdb();
            Svc = new AndroidService(gate, Adb, new AndroidOptions { Enabled = enabled, FileRoots = new[] { Path.Combine(Root, "files") } });
        }
        public FakeAdb Fake => (FakeAdb)Adb;
        public string Files => Path.Combine(Root, "files");
        public Task<DesktopCallResult> Do(string tool, object args) => Svc.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args), Ctx, CancellationToken.None);
        public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(Root, true); } catch { } try { File.Delete(_db); } catch { } }
    }

    private static JsonElement Res(DesktopCallResult r) => JsonSerializer.SerializeToElement(r.Result);

    [Fact]
    public void ParsesAdbDevicesIncludingUnauthorizedAndWireless()
    {
        var d = AndroidService.ParseDevices("* daemon started successfully\nList of devices attached\nR58M device usb:1 product:a52q model:SM_A525F device:a52q\nZY22 unauthorized usb:2 transport_id:3\n192.168.1.20:5555 device product:p model:Pixel_8 device:x\n\n");
        Assert.Equal(3, d.Count);
        Assert.Equal(("R58M", "device", "SM_A525F", false), (d[0].Serial, d[0].State, d[0].Model, d[0].Wireless));
        Assert.Equal("unauthorized", d[1].State);
        Assert.True(d[2].Wireless);
    }

    [Fact]
    public async Task OffByDefault_AndHonestWhenAdbIsMissing()
    {
        using (var off = new Rig(enabled: false)) { Assert.Empty(off.Svc.Tools); Assert.Equal("android_disabled", (await off.Do(AndroidService.ListDevices, new { })).Error?.Code); }
        using var rig = new Rig(new FakeAdb { AdbPath = null });
        var r = await rig.Do(AndroidService.ListDevices, new { });
        Assert.Equal("adb_missing", r.Error?.Code);
        Assert.Contains("Platform-Tools", r.Error?.Message);
    }

    [Fact]
    public async Task DiscoveryAndInfo_AreReads_WithoutApproval()
    {
        using var rig = new Rig();
        var list = Res(await rig.Do(AndroidService.ListDevices, new { }));
        Assert.Equal(1, list.GetProperty("count").GetInt32());
        var info = await rig.Do(AndroidService.DeviceInfo, new { });
        Assert.True(info.Status == "ok", info.Error?.Message);
        var i = Res(info);
        Assert.Equal("SM-A525F", i.GetProperty("model").GetString());
        Assert.Equal("14", i.GetProperty("android").GetString());
        Assert.Equal(81, i.GetProperty("batteryPercent").GetInt32());
        Assert.True(i.GetProperty("charging").GetBoolean());
        Assert.Empty(rig.Cards);
    }

    [Fact]
    public async Task UnauthorizedPhone_IsExplained_NeverWorkedAround()
    {
        using var rig = new Rig();
        rig.Fake.DevicesOut = "List of devices attached\nZY22 unauthorized usb:2 transport_id:3\n";
        var list = Res(await rig.Do(AndroidService.ListDevices, new { }));
        Assert.Contains("accept the USB-debugging prompt", list.GetProperty("devices")[0].GetProperty("note").GetString());
        foreach (var (tool, args) in new (string, object)[] { (AndroidService.DeviceInfo, new { }), (AndroidService.Command, new { action = "home" }), (AndroidService.Pull, new { remotePath = "/sdcard/a.txt", toFolder = rig.Files }) })
        {
            var r = await rig.Do(tool, args);
            Assert.Equal("unauthorized", r.Error?.Code);
            Assert.Contains("Allow USB debugging", r.Error?.Message);
        }
        Assert.Empty(rig.Cards);
        Assert.DoesNotContain(rig.Fake.Calls, c => c.Contains("shell"));   // nothing was sent to the phone
        // No device / several devices.
        rig.Fake.DevicesOut = "List of devices attached\n";
        Assert.Equal("no_device", (await rig.Do(AndroidService.DeviceInfo, new { })).Error?.Code);
        rig.Fake.DevicesOut = "List of devices attached\nA device\nB device\n";
        Assert.Equal("ambiguous_target", (await rig.Do(AndroidService.DeviceInfo, new { })).Error?.Code);
        Assert.Equal("ok", (await rig.Do(AndroidService.DeviceInfo, new { serial = "B" })).Status);
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.DeviceInfo, new { serial = "B; reboot" })).Error?.Code);
    }

    [Fact]
    public async Task Push_And_Pull_AreApproved_ConfinedAndVerifiedBySha256()
    {
        using var rig = new Rig();
        string local = Path.Combine(rig.Files, "notes.txt");
        File.WriteAllText(local, "hello phone");
        var push = await rig.Do(AndroidService.Push, new { file = local });
        Assert.True(push.Status == "ok", push.Error?.Message);
        Assert.Equal("/sdcard/Download/notes.txt", Res(push).GetProperty("remote").GetString());
        var card = rig.Cards.Single();
        Assert.Equal(DesktopActionTypes.AndroidTransfer, card.ActionType);
        Assert.Equal("/sdcard/Download/notes.txt", card.ActionParameters["destination"]);

        // Pull back into another folder inside the roots: same SHA-256.
        string into = Directory.CreateDirectory(Path.Combine(rig.Files, "back")).FullName;
        var pull = await rig.Do(AndroidService.Pull, new { remotePath = "/sdcard/Download/notes.txt", toFolder = into });
        Assert.True(pull.Status == "ok", pull.Error?.Message);
        Assert.Equal("hello phone", File.ReadAllText(Path.Combine(into, "notes.txt")));
        // Never overwrites a local file.
        Assert.Equal("exists", (await rig.Do(AndroidService.Pull, new { remotePath = "/sdcard/Download/notes.txt", toFolder = into })).Error?.Code);

        // Confinement: outside the local roots, outside /sdcard, path tricks.
        string outside = Path.Combine(rig.Root, "secret.txt"); File.WriteAllText(outside, "x");
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.Push, new { file = outside })).Error?.Code);
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.Push, new { file = Path.Combine(rig.Files, "..", "secret.txt") })).Error?.Code);
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.Push, new { file = local, toFolder = "/data/local/tmp" })).Error?.Code);
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.Pull, new { remotePath = "/data/data/com.bank/db", toFolder = into })).Error?.Code);
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.Pull, new { remotePath = "/sdcard/a;rm -rf /", toFolder = into })).Error?.Code);
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.Pull, new { remotePath = "/sdcard/x.txt", toFolder = rig.Root })).Error?.Code);

        // Declined: nothing copied. Corrupted copy: reported unverified.
        rig.Decide = _ => false;
        File.WriteAllText(Path.Combine(rig.Files, "two.txt"), "2");
        Assert.Equal("approval_denied", (await rig.Do(AndroidService.Push, new { file = Path.Combine(rig.Files, "two.txt") })).Error?.Code);
        Assert.False(rig.Fake.Phone.ContainsKey("/sdcard/Download/two.txt"));
        rig.Decide = _ => true; rig.Fake.CorruptPush = true;
        Assert.Equal("execution_unverified", (await rig.Do(AndroidService.Push, new { file = Path.Combine(rig.Files, "two.txt") })).Error?.Code);
        Assert.True((await rig.Recorder.VerifyAuditChainAsync()).Ok);
    }

    [Fact]
    public async Task Commands_AreFixedArgumentLists_AndApproved()
    {
        using var rig = new Rig();
        var r = await rig.Do(AndroidService.Command, new { action = "home" });
        Assert.True(r.Status == "ok", r.Error?.Message);
        Assert.Contains(rig.Fake.Calls, c => c.SequenceEqual(new[] { "-s", "R58M123ABC", "shell", "input", "keyevent", "3" }));
        Assert.Equal(DesktopActionTypes.AndroidCommand, rig.Cards.Single().ActionType);
        var open = await rig.Do(AndroidService.Command, new { action = "open_url", url = "https://example.com/" });
        Assert.True(open.Status == "ok", open.Error?.Message);
        int cards = rig.Cards.Count;
        foreach (var bad in new object[] { new { action = "reboot" }, new { action = "shell rm -rf /sdcard" }, new { action = "open_url", url = "http://example.com" },
                     new { action = "open_url", url = "https://x.com/';reboot" }, new { action = "open_url", url = "intent://x" } })
            Assert.Equal("invalid_args", (await rig.Do(AndroidService.Command, bad)).Error?.Code);
        Assert.Equal(cards, rig.Cards.Count);   // refused before any card
        rig.Decide = _ => false;
        Assert.Equal("approval_denied", (await rig.Do(AndroidService.Command, new { action = "lock" })).Error?.Code);
        Assert.DoesNotContain(rig.Fake.Calls, c => c.Contains("223"));
    }

    [Fact]
    public async Task WifiConnect_OnlyLocalNetwork_AndApproved()
    {
        using var rig = new Rig();
        Assert.Equal("not_local", (await rig.Do(AndroidService.Connect, new { host = "8.8.8.8" })).Error?.Code);
        Assert.Equal("invalid_args", (await rig.Do(AndroidService.Connect, new { host = "phone.example" })).Error?.Code);
        Assert.Empty(rig.Cards);
        var r = await rig.Do(AndroidService.Connect, new { host = "192.168.1.20", port = 5555 });
        Assert.True(r.Status == "ok", r.Error?.Message);
        Assert.Equal(DesktopActionTypes.AndroidConnect, rig.Cards.Single().ActionType);
    }

    /// <summary>REAL phone: needs DASK_LIVE_ANDROID=1, adb installed, exactly one phone that has authorized this PC. Reads info, then
    /// pushes a small test file to /sdcard/Download, pulls it back (SHA-256 verified) and leaves the phone's copy for the user to delete
    /// (the test never deletes files on the phone). Approvals are given by the test harness, not the user.</summary>
    [Fact]
    public async Task RealDevice_ReadOnlyAndRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("DASK_LIVE_ANDROID") != "1") return;
        using var rig = new Rig(new AdbRunner(Environment.GetEnvironmentVariable("DASK_ADB")));
        Assert.True(rig.Adb.AdbPath != null, "adb is not installed");
        var info = await rig.Do(AndroidService.DeviceInfo, new { });
        Assert.True(info.Status == "ok", info.Error?.Code + ": " + info.Error?.Message);
        Console.WriteLine("device: " + JsonSerializer.Serialize(info.Result));
        string local = Path.Combine(rig.Files, $"dask-adb-test-{DateTime.UtcNow:yyyyMMddHHmmss}.txt");
        File.WriteAllText(local, "D-ASK adb round-trip test " + Guid.NewGuid());
        var push = await rig.Do(AndroidService.Push, new { file = local });
        Assert.True(push.Status == "ok", push.Error?.Message);
        string into = Directory.CreateDirectory(Path.Combine(rig.Files, "back")).FullName;
        var pull = await rig.Do(AndroidService.Pull, new { remotePath = Res(push).GetProperty("remote").GetString(), toFolder = into });
        Assert.True(pull.Status == "ok", pull.Error?.Message);
        Assert.Equal(File.ReadAllText(local), File.ReadAllText(Path.Combine(into, Path.GetFileName(local))));
    }
}
