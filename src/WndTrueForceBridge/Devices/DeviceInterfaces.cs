// WndTrueForceBridge — device abstractions served over the bridge pipe.
// Copyright (C) 2026 WnDTech. SPDX-License-Identifier: GPL-2.0-only
namespace WndTrueForceBridge.Devices;

/// <summary>Haptic sample (the TrueForce stream does not map it yet).</summary>
public sealed class HapticData
{
    public float VibrationIntensity { get; set; }
    public int VibrationFrequencyHz { get; set; } = 80;
    public float AbsRumble { get; set; }
    public float TcRumble { get; set; }
    public float RpmFrequency { get; set; }
}

/// <summary>TrueForce stream session (implemented by LogitechTrueForceProvider
/// and by the in-memory fake used for loopback tests).</summary>
public interface ITrueForceDevice : IDisposable
{
    bool IsInitialized { get; }
    bool IsConnected { get; }
    string? LastError { get; }
    bool IsPaused { get; }
    bool IsEngaged { get; }
    string ProductName { get; }
    long PacketsSent { get; }
    long PacketsFailed { get; }
    bool ForceInvert { get; set; }
    float ForceScale { get; set; }
    float SmoothTimeConstantMs { get; set; }
    int RotationDegrees { get; set; }

    bool Initialize();
    bool Engage();
    void Disengage();
    void UpdateTorque(float signal);
    void ZeroTorque();
    void Shutdown();
    void Pause();
    void Resume();
}

/// <summary>HID++ settings surface (implemented by LogitechHidppWheelProvider
/// and by the in-memory fake used for loopback tests).</summary>
public interface IHidppDevice : IDisposable
{
    bool IsConnected { get; }
    string ProductName { get; }
    string LastError { get; }
    string DiagnosticSummary { get; }
    byte ProfileMode { get; }
    float FfbStrengthNm { get; }
    int RotationDegrees { get; }
    int TrueForceLevelPct { get; }
    int DampingPct { get; }
    bool LastSettingsReadOk { get; }
    bool OledAvailable { get; }
    int OledLayoutCount { get; }

    bool Connect();
    void Disconnect();
    bool SetFfbStrengthNm(float nm);
    bool SetRotationDegrees(int degrees);
    bool SetDesktopMode();
    bool SetOnboardSlot(byte slot);
    void ReadAllSettingsForUi();
    void ReadProfileMode();
    bool OledQueryLayouts(int timeoutMs = 1000);
    bool OledClear();
    bool OledWriteFrame(byte layoutIndex, string[] fields, int[] capacities, bool waitForResponse = true, int timeoutMs = 1000);
}

/// <summary>Creates device instances; swapped for fakes with --fake-device.</summary>
public interface IDeviceBackend
{
    ITrueForceDevice CreateTrueForce();
    IHidppDevice CreateHidpp();
    /// <summary>Crash-path session-end handshake on whatever stream is open.</summary>
    void EmergencyTeardown();
}
