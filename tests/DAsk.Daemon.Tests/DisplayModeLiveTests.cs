using System.Collections.Concurrent;
using System.Text.Json;
using DAsk.Daemon.Automation.Desktop;
using DAsk.Daemon.Automation.Observation;
using DAsk.Daemon.Automation.Policy;
using DAsk.Daemon.Automation.SystemAdmin;
using DAsk.Daemon.Research;
using DAsk.Protocol.IPC.Messages.ComputerUse;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DAsk.Daemon.Tests;

/// <summary>
/// system.set_display_mode on the REAL primary display (2026-11-15; DASK_LIVE_SYSTEM=1). The screen really switches; the original mode is
/// always restored. The TEST plays the HUD. Keep declined / not answered / Emergency Stop -> rolled back; kept -> saved; then restored.
/// </summary>
[Collection("AgentHost")]
public class DisplayModeLiveTests
{
    private readonly ITestOutputHelper _out;
    public DisplayModeLiveTests(ITestOutputHelper o) => _out = o;

    [LiveSystemFact]
    [Trait("Category", "SystemAdminLive")]
    public async Task Live_ResolutionChange_KeepOrRollback()
    {
        string device = Displays.PrimaryDevice()!;
        var before = Displays.Current(device)!;
        var modes = Displays.SupportedModes(device);
        _out.WriteLine($"display {device}: {before}; {modes.Count} supported modes: {string.Join(", ", modes.Take(10))}");
        var target = modes.FirstOrDefault(m => m.Width < before.Width && m.Height < before.Height && m.RefreshHz == before.RefreshHz)
                     ?? modes.First(m => m != before);
        string db = Path.Combine(Path.GetTempPath(), $"dask-display-live-{Guid.NewGuid():N}.db");
        var oldWindow = SystemAdminService.DisplayKeepWindow;
        SystemAdminService.DisplayKeepWindow = TimeSpan.FromSeconds(6);
        try
        {
            var recorder = new SqliteEvidenceRecorder(NullLogger<SqliteEvidenceRecorder>.Instance, db);
            await recorder.InitializeAsync();
            var policy = new ActionPolicyEngine(NullLogger<ActionPolicyEngine>.Instance, recorder, new Win32WindowSource());
            policy.SetSessionLivenessGate(_ => true);
            Func<string, bool?> decide = _ => true;   // null = never answer
            bool stopped = false;
            Action? onKeepCard = null;
            var asked = new ConcurrentQueue<string>();
            policy.ApprovalRequested += (_, r) => Task.Run(async () =>
            {
                asked.Enqueue(r.ActionType);
                if (r.ActionType == DesktopActionTypes.DisplayModeKeep) onKeepCard?.Invoke();
                await Task.Delay(300);
                var d = decide(r.ActionType);
                if (d == null) return;
                policy.SubmitApprovalDecision(new ActionApprovalResponse
                {
                    ApprovalId = r.ApprovalId, ActionId = r.ActionId, ActionType = r.ActionType, ParametersSha256 = r.ParametersSha256,
                    ActionDigest = r.ActionDigest, Approved = d.Value, DecisionSource = "UserExplicitConfirmation",
                }, "live-session", () => false, () => 1);
            });
            var svc = new SystemAdminService(new ApprovalGate(policy, () => stopped, () => 1, recorder, _ => true, "SystemAdmin"), new SystemAdminOptions { Enabled = true });
            var ctx = new DesktopActionContext("live-task", "c1", "live-session");
            async Task<DesktopCallResult> Set(Displays.Mode m)
            {
                var r = await svc.ExecuteAsync(SystemAdminService.SetDisplayMode, JsonSerializer.SerializeToElement(new { width = m.Width, height = m.Height, refreshHz = m.RefreshHz }), ctx, CancellationToken.None);
                _out.WriteLine($"set {m}: {r.Status} {JsonSerializer.Serialize((object?)r.Result ?? r.Error)}");
                return r;
            }
            JsonElement Res(DesktopCallResult r) => JsonSerializer.SerializeToElement(r.Result);

            var status = await svc.ExecuteAsync(SystemAdminService.DisplayStatus, JsonSerializer.SerializeToElement(new { }), ctx, CancellationToken.None);
            Assert.Contains($"{target.Width}x{target.Height}", JsonSerializer.Serialize(status.Result));

            // 1. Keep DECLINED -> rolled back, read back independently.
            decide = t => t != DesktopActionTypes.DisplayModeKeep;
            Displays.Mode? during = null;
            onKeepCard = () => during = Displays.Current(device);
            var r1 = await Set(target);
            Assert.Equal("ok", r1.Status);
            Assert.False(Res(r1).GetProperty("kept").GetBoolean());
            Assert.Equal(target, during);                       // it really switched while the keep card was up
            Assert.Equal(before, Displays.Current(device));      // and really came back

            // 2. Keep NOT ANSWERED (the screen could be unusable) -> rolled back after the keep window.
            decide = t => t == DesktopActionTypes.DisplayModeKeep ? null : true;
            var t0 = DateTime.UtcNow;
            var r2 = await Set(target);
            Assert.Equal("ok", r2.Status);
            Assert.False(Res(r2).GetProperty("kept").GetBoolean());
            Assert.True(DateTime.UtcNow - t0 >= TimeSpan.FromSeconds(6));
            Assert.Equal(before, Displays.Current(device));

            // 3. Emergency Stop while the keep card is up -> rolled back.
            decide = t => t == DesktopActionTypes.DisplayModeKeep ? null : true;
            // What the engine does on Emergency Stop: the latch is set and every pending approval is invalidated.
            onKeepCard = () => { stopped = true; policy.InvalidateAllPendingAndGrantedApprovals("Emergency Stop (live test)"); };
            var r3 = await Set(target);
            stopped = false; onKeepCard = null;
            Assert.Equal(before, Displays.Current(device));
            _out.WriteLine($"emergency stop case: {r3.Status} {r3.Error?.Code}");

            // 4. Kept -> saved, then restore the original (also kept).
            decide = _ => true;
            var r4 = await Set(target);
            Assert.Equal("ok", r4.Status);
            Assert.True(Res(r4).GetProperty("kept").GetBoolean());
            Assert.Equal(target, Displays.Current(device));
            var r5 = await Set(before);
            Assert.Equal("ok", r5.Status);
            Assert.Equal(before, Displays.Current(device));

            // An unsupported mode is refused before anything is asked.
            int n = asked.Count;
            var bad = await svc.ExecuteAsync(SystemAdminService.SetDisplayMode, JsonSerializer.SerializeToElement(new { width = 1234, height = 777 }), ctx, CancellationToken.None);
            Assert.Equal("unsupported_mode", bad.Error?.Code);
            Assert.Equal(n, asked.Count);
            Assert.True((await recorder.VerifyAuditChainAsync()).Ok);
        }
        finally
        {
            SystemAdminService.DisplayKeepWindow = oldWindow;
            if (Displays.Current(device) != before) Displays.Apply(device, before, test: false, persist: true);
            SqliteConnection.ClearAllPools(); try { File.Delete(db); } catch { }
        }
    }
}
