// WndTrueForceBridge — real (Logitech HID) and fake (loopback test) backends.
// Copyright (C) 2026 WnDTech. SPDX-License-Identifier: GPL-2.0-only
using WndTrueForceBridge.Providers;

namespace WndTrueForceBridge.Devices;

public sealed class LogitechBackend : IDeviceBackend
{
    public ITrueForceDevice CreateTrueForce() => new LogitechTrueForceProvider();
    public IHidppDevice CreateHidpp() => new LogitechHidppWheelProvider();
    public void EmergencyTeardown() => LogitechTrueForceProvider.EmergencyTeardown();
}

/// <summary>In-memory device for the client/bridge loopback tests (no HID).
/// Every nonzero torque update counts as a "packet sent" so tests can observe
/// that fire-and-forget torque reached the bridge.</summary>
public sealed class FakeBackend : IDeviceBackend
{
    public ITrueForceDevice CreateTrueForce() => new FakeTrueForce();
    public IHidppDevice CreateHidpp() => new FakeHidpp();
    public void EmergencyTeardown() { }

    private sealed class FakeTrueForce : ITrueForceDevice
    {
        private long _sent;
        public bool IsInitialized { get; private set; }
        public bool IsConnected { get; private set; }
        public string? LastError => null;
        public bool IsPaused { get; private set; }
        public bool IsEngaged { get; private set; }
        public string ProductName => IsConnected ? "Fake TrueForce Wheel" : "";
        public long PacketsSent => Interlocked.Read(ref _sent);
        public long PacketsFailed => 0;
        public bool ForceInvert { get; set; } = true;
        public float ForceScale { get; set; } = 0.8f;
        public float SmoothTimeConstantMs { get; set; } = 1.0f;
        public int RotationDegrees { get; set; } = 1080;
        public bool Initialize() { IsConnected = true; return true; }
        public bool Engage() { if (!IsConnected) return false; IsEngaged = true; IsInitialized = true; return true; }
        public void Disengage() { IsEngaged = false; IsInitialized = false; }
        public void UpdateTorque(float signal) { if (signal != 0f) Interlocked.Increment(ref _sent); }
        public void ZeroTorque() { }
        public void Shutdown() { IsEngaged = false; IsInitialized = false; IsConnected = false; }
        public void Pause() => IsPaused = true;
        public void Resume() => IsPaused = false;
        public void Dispose() => Shutdown();
    }

    private sealed class FakeHidpp : IHidppDevice
    {
        public bool IsConnected { get; private set; }
        public string ProductName => "Fake TrueForce Wheel";
        public string LastError => "";
        public string DiagnosticSummary { get; private set; } = "";
        public byte ProfileMode { get; private set; } = 0xFF;
        public float FfbStrengthNm { get; private set; } = 5f;
        public int RotationDegrees { get; private set; } = 900;
        public int TrueForceLevelPct => 50;
        public int DampingPct => 10;
        public bool LastSettingsReadOk => IsConnected;
        public bool OledAvailable => IsConnected;
        public int OledLayoutCount { get; private set; }
        public bool Connect() { IsConnected = true; return true; }
        public void Disconnect() => IsConnected = false;
        public bool SetFfbStrengthNm(float nm) { FfbStrengthNm = Math.Clamp(nm, 1f, 8f); return IsConnected; }
        public bool SetRotationDegrees(int degrees) { RotationDegrees = Math.Clamp(degrees, 90, 2700); return IsConnected; }
        public bool SetDesktopMode() { ProfileMode = 0; return IsConnected; }
        public bool SetOnboardSlot(byte slot) { ProfileMode = slot; return IsConnected; }
        public void ReadAllSettingsForUi() => DiagnosticSummary = $"{ProductName}: strength={FfbStrengthNm:F1} Nm, rotation={RotationDegrees}°";
        public void ReadProfileMode() { if (ProfileMode == 0xFF) ProfileMode = 0; }
        public bool OledQueryLayouts(int timeoutMs = 1000) { OledLayoutCount = 10; return IsConnected; }
        public bool OledClear() => IsConnected;
        public bool OledWriteFrame(byte layoutIndex, string[] fields, int[] capacities, bool waitForResponse = true, int timeoutMs = 1000)
            => IsConnected && fields.Length == capacities.Length;
        public void Dispose() => Disconnect();
    }
}
