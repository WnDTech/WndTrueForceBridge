// WndTrueForceBridge — named-pipe server: dispatches bridge protocol messages
// to the Logitech TrueForce / HID++ devices.
// Copyright (C) 2026 WnDTech. SPDX-License-Identifier: GPL-2.0-only
using System.Collections.Concurrent;
using System.IO.Pipes;
using WndTech.TrueForceBridge.Protocol;
using WndTrueForceBridge.Devices;

namespace WndTrueForceBridge;

internal sealed class BridgeServer
{
    private readonly string _pipeName;
    private readonly IDeviceBackend _backend;
    private readonly ConcurrentDictionary<int, ITrueForceDevice> _tf = new();
    private readonly ConcurrentDictionary<int, IHidppDevice> _hidpp = new();
    private readonly object _writeLock = new();
    private readonly object _teardownLock = new();
    private bool _tornDown;
    private NamedPipeServerStream? _pipe;

    public BridgeServer(string pipeName, IDeviceBackend backend)
    {
        _pipeName = pipeName;
        _backend = backend;
    }

    /// <summary>Serve exactly one client; returns when it disconnects.</summary>
    public void Run(TimeSpan connectTimeout)
    {
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cts = new CancellationTokenSource(connectTimeout);
        try
        {
            _pipe.WaitForConnectionAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            BridgeLog.Write("No client connected within the timeout — exiting");
            return;
        }
        BridgeLog.Write("Client connected");

        try
        {
            while (true)
            {
                var frame = BridgeCodec.ReadFrame(_pipe);
                if (frame == null) break;
                Dispatch(frame.Value);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            BridgeLog.Write($"Pipe read ended: {ex.GetType().Name}: {ex.Message}");
        }
        BridgeLog.Write("Client disconnected");
    }

    private void Dispatch(BridgeFrame f)
    {
        switch (f.Payload)
        {
            case TfTorqueMessage t:
                if (_tf.TryGetValue(t.Session, out var dev)) dev.UpdateTorque(t.Value);
                break;
            case TfCommandMessage c:
                HandleTfCommand(c);
                break;
            case HelloMessage h:
                BridgeLog.Write($"Hello from pid {h.ClientPid}, protocol v{h.Version} (bridge v{BridgeProtocol.Version})");
                Send(f.RequestId, new HelloAckMessage(BridgeProtocol.Version, Environment.ProcessId));
                break;
            case TfRequestMessage r when r.Op == TfRequestOp.Status:
                Send(f.RequestId, TfStatus(r.Session, true));
                break;
            case TfRequestMessage r:
                // Initialize / Shutdown can block (HID open, pump join) — keep the
                // reader free so torque and other commands keep flowing.
                Task.Run(() => Send(f.RequestId, HandleTfRequest(r)));
                break;
            case HidppRequestMessage h:
                Task.Run(() => Send(f.RequestId, HandleHidpp(h)));
                break;
        }
    }

    private void HandleTfCommand(TfCommandMessage c)
    {
        if (c.Op == TfCommandOp.EmergencyTeardown)
        {
            BridgeLog.Write("EmergencyTeardown requested by client");
            EmergencyTeardownAndExit();
            return;
        }
        if (!_tf.TryGetValue(c.Session, out var d)) return;
        switch (c.Op)
        {
            case TfCommandOp.Engage: d.Engage(); break;
            case TfCommandOp.Disengage: d.Disengage(); break;
            case TfCommandOp.Pause: d.Pause(); break;
            case TfCommandOp.Resume: d.Resume(); break;
            case TfCommandOp.SetForceInvert: d.ForceInvert = c.Flag; break;
            case TfCommandOp.SetForceScale: d.ForceScale = c.Value; break;
            case TfCommandOp.SetSmoothTimeConstantMs: d.SmoothTimeConstantMs = c.Value; break;
            case TfCommandOp.SetRotationDegrees: d.RotationDegrees = c.IntValue; break;
            case TfCommandOp.Zero: d.ZeroTorque(); break;
        }
    }

    private TfStatusMessage HandleTfRequest(TfRequestMessage r)
    {
        try
        {
            switch (r.Op)
            {
                case TfRequestOp.Initialize:
                {
                    var d = _tf.GetOrAdd(r.Session, _ => _backend.CreateTrueForce());
                    bool ok = d.Initialize();
                    return TfStatus(r.Session, ok);
                }
                case TfRequestOp.Shutdown:
                    if (_tf.TryRemove(r.Session, out var old))
                    {
                        old.Shutdown();
                        old.Dispose();
                    }
                    return new TfStatusMessage { Ok = true };
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Write($"TF request {r.Op} threw: {ex.GetType().Name}: {ex.Message}");
            return new TfStatusMessage { Ok = false, LastError = ex.Message };
        }
        return new TfStatusMessage { Ok = false };
    }

    private TfStatusMessage TfStatus(int session, bool ok)
    {
        if (!_tf.TryGetValue(session, out var d)) return new TfStatusMessage { Ok = false };
        return new TfStatusMessage
        {
            Ok = ok,
            IsInitialized = d.IsInitialized,
            IsConnected = d.IsConnected,
            IsPaused = d.IsPaused,
            IsEngaged = d.IsEngaged,
            PacketsSent = d.PacketsSent,
            PacketsFailed = d.PacketsFailed,
            RotationDegrees = d.RotationDegrees,
            ProductName = d.ProductName ?? "",
            LastError = d.LastError,
        };
    }

    private HidppResultMessage HandleHidpp(HidppRequestMessage m)
    {
        bool ok = false;
        try
        {
            IHidppDevice? d;
            if (m.Op == HidppOp.Connect)
            {
                if (_hidpp.TryRemove(m.Session, out var prev)) prev.Dispose();
                d = _backend.CreateHidpp();
                _hidpp[m.Session] = d;
                ok = d.Connect();
                return new HidppResultMessage(ok, State(d));
            }
            if (m.Op == HidppOp.Disconnect)
            {
                if (_hidpp.TryRemove(m.Session, out var gone)) gone.Dispose();
                return new HidppResultMessage(true, new HidppStateDto());
            }
            if (!_hidpp.TryGetValue(m.Session, out d))
                return new HidppResultMessage(false, new HidppStateDto { LastError = "HID++ session not connected" });

            switch (m.Op)
            {
                case HidppOp.SetFfbStrengthNm: ok = d.SetFfbStrengthNm(m.FloatArg); break;
                case HidppOp.SetRotationDegrees: ok = d.SetRotationDegrees(m.IntArg); break;
                case HidppOp.SetDesktopMode: ok = d.SetDesktopMode(); break;
                case HidppOp.SetOnboardSlot: ok = d.SetOnboardSlot((byte)m.IntArg); break;
                case HidppOp.ReadAllSettingsForUi: d.ReadAllSettingsForUi(); ok = true; break;
                case HidppOp.ReadProfileMode: d.ReadProfileMode(); ok = true; break;
                case HidppOp.OledQueryLayouts: ok = d.OledQueryLayouts(m.TimeoutMs); break;
                case HidppOp.OledClear: ok = d.OledClear(); break;
                case HidppOp.OledWriteFrame:
                    ok = d.OledWriteFrame((byte)m.IntArg, m.Fields, m.Capacities, m.WaitForResponse, m.TimeoutMs);
                    break;
                case HidppOp.GetState: ok = true; break;
            }
            return new HidppResultMessage(ok, State(d));
        }
        catch (Exception ex)
        {
            BridgeLog.Write($"HID++ request {m.Op} threw: {ex.GetType().Name}: {ex.Message}");
            return new HidppResultMessage(false, new HidppStateDto { LastError = ex.Message });
        }
    }

    private static HidppStateDto State(IHidppDevice d) => new()
    {
        IsConnected = d.IsConnected,
        ProductName = d.ProductName ?? "",
        LastError = d.LastError ?? "",
        DiagnosticSummary = d.DiagnosticSummary ?? "",
        ProfileMode = d.ProfileMode,
        FfbStrengthNm = d.FfbStrengthNm,
        RotationDegrees = d.RotationDegrees,
        TrueForceLevelPct = d.TrueForceLevelPct,
        DampingPct = d.DampingPct,
        LastSettingsReadOk = d.LastSettingsReadOk,
        OledAvailable = d.OledAvailable,
        OledLayoutCount = d.OledLayoutCount,
    };

    private void Send(int requestId, object payload)
    {
        var pipe = _pipe;
        if (pipe == null) return;
        var bytes = BridgeCodec.Encode(requestId, payload);
        try
        {
            lock (_writeLock)
            {
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Client gone — the read loop ends and Program tears down.
        }
    }

    /// <summary>Crash path: same contract as the in-app EmergencyTeardown —
    /// best-effort session-end handshake on the open stream, then exit.</summary>
    public void EmergencyTeardownAndExit()
    {
        lock (_teardownLock)
        {
            if (!_tornDown)
            {
                _tornDown = true;
                try { _backend.EmergencyTeardown(); } catch { }
                BridgeLog.Write("Emergency teardown done — exiting");
            }
        }
        Environment.Exit(0);
    }

    /// <summary>Normal end (pipe closed / parent exited): release every session
    /// so the pump sends the session-end handshake, bounded so a wedged device
    /// can't keep the bridge alive; falls back to the emergency teardown.</summary>
    public void TeardownAll()
    {
        lock (_teardownLock)
        {
            if (_tornDown) return;
            _tornDown = true;
        }
        var t = new Thread(() =>
        {
            foreach (var kv in _tf)
            {
                try { kv.Value.Shutdown(); kv.Value.Dispose(); } catch { }
            }
            _tf.Clear();
            foreach (var kv in _hidpp)
            {
                try { kv.Value.Dispose(); } catch { }
            }
            _hidpp.Clear();
        }) { IsBackground = true, Name = "BridgeTeardown" };
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(6)))
        {
            BridgeLog.Write("Teardown timed out — running emergency teardown");
            try { _backend.EmergencyTeardown(); } catch { }
        }
        BridgeLog.Write("Teardown complete");
    }
}
