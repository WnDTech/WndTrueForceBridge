// TrueForceBridgeProtocol.cs — wire contract between FFB Tuner and
// WndTrueForceBridge.exe (named pipe IPC).
//
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 WnDTech
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// This file contains ONLY message ids and DTOs. It is compiled into both the
// closed-source app (AcEvoFfbTuner.Core) and the GPL bridge — keep any device
// logic out of it.
//
// Framing (little-endian):
//   int32  bodyLength  (bytes after this field)
//   byte   messageType (BridgeMessageType)
//   int32  requestId   (0 = fire-and-forget; responses echo the request's id)
//   ...    payload     (per message, see Write/Read below)
using System.IO;
using System.Text;

namespace WndTech.TrueForceBridge.Protocol;

internal static class BridgeProtocol
{
    /// <summary>Bumped on any incompatible wire change. Hello/HelloAck carry it.</summary>
    public const int Version = 1;

    public const string PipeArg = "--pipe";
    public const string ParentPidArg = "--parent-pid";
    /// <summary>Test-only: run the bridge against an in-memory fake device.</summary>
    public const string FakeDeviceArg = "--fake-device";

    public const string BridgeExeName = "WndTrueForceBridge.exe";

    /// <summary>Upper bound on a frame body — guards against a corrupt length.</summary>
    public const int MaxBodyLength = 64 * 1024;
}

internal enum BridgeMessageType : byte
{
    Hello = 1,          // client → bridge (request)
    HelloAck = 2,       // bridge → client (response)
    TfTorque = 3,       // client → bridge (fire-and-forget, ~200/s)
    TfCommand = 4,      // client → bridge (fire-and-forget)
    TfRequest = 5,      // client → bridge (request)
    TfStatus = 6,       // bridge → client (response to TfRequest)
    HidppRequest = 7,   // client → bridge (request)
    HidppResult = 8,    // bridge → client (response to HidppRequest)
}

/// <summary>Fire-and-forget TrueForce commands.</summary>
internal enum TfCommandOp : byte
{
    Engage = 1,
    Disengage = 2,
    Pause = 3,
    Resume = 4,
    SetForceInvert = 5,      // Flag
    SetForceScale = 6,       // Value
    SetSmoothTimeConstantMs = 7, // Value
    SetRotationDegrees = 8,  // IntValue
    Zero = 9,
    /// <summary>Crash path: run the emergency session-end handshake on every
    /// TrueForce session, then the bridge exits.</summary>
    EmergencyTeardown = 10,
}

/// <summary>TrueForce requests answered with <see cref="TfStatusMessage"/>.</summary>
internal enum TfRequestOp : byte
{
    Initialize = 1,
    Shutdown = 2,
    Status = 3,
}

internal enum HidppOp : byte
{
    Connect = 1,
    Disconnect = 2,
    SetFfbStrengthNm = 3,    // FloatArg
    SetRotationDegrees = 4,  // IntArg
    SetDesktopMode = 5,
    SetOnboardSlot = 6,      // IntArg (slot)
    ReadAllSettingsForUi = 7,
    ReadProfileMode = 8,
    OledQueryLayouts = 9,    // TimeoutMs
    OledClear = 10,
    OledWriteFrame = 11,     // IntArg (layout), Fields, Capacities, WaitForResponse, TimeoutMs
    GetState = 12,
}

internal sealed record HelloMessage(int Version, int ClientPid);

internal sealed record HelloAckMessage(int Version, int BridgePid);

/// <summary>Torque target for one TrueForce session (normalized −1..1).</summary>
internal readonly record struct TfTorqueMessage(int Session, float Value);

internal sealed record TfCommandMessage(int Session, TfCommandOp Op, float Value = 0f, int IntValue = 0, bool Flag = false);

internal sealed record TfRequestMessage(int Session, TfRequestOp Op);

internal sealed record TfStatusMessage
{
    public bool Ok { get; init; }
    public bool IsInitialized { get; init; }
    public bool IsConnected { get; init; }
    public bool IsPaused { get; init; }
    public bool IsEngaged { get; init; }
    public long PacketsSent { get; init; }
    public long PacketsFailed { get; init; }
    public int RotationDegrees { get; init; }
    public string ProductName { get; init; } = "";
    public string? LastError { get; init; }
}

internal sealed record HidppRequestMessage
{
    public int Session { get; init; }
    public HidppOp Op { get; init; }
    public int IntArg { get; init; }
    public float FloatArg { get; init; }
    public bool WaitForResponse { get; init; } = true;
    public int TimeoutMs { get; init; } = 1000;
    public string[] Fields { get; init; } = [];
    public int[] Capacities { get; init; } = [];
}

/// <summary>Snapshot of the HID++ settings provider after an operation.</summary>
internal sealed record HidppStateDto
{
    public bool IsConnected { get; init; }
    public string ProductName { get; init; } = "";
    public string LastError { get; init; } = "";
    public string DiagnosticSummary { get; init; } = "";
    public byte ProfileMode { get; init; } = 0xFF;
    public float FfbStrengthNm { get; init; }
    public int RotationDegrees { get; init; }
    public int TrueForceLevelPct { get; init; }
    public int DampingPct { get; init; }
    public bool LastSettingsReadOk { get; init; } = true;
    public bool OledAvailable { get; init; }
    public int OledLayoutCount { get; init; }
}

internal sealed record HidppResultMessage(bool Ok, HidppStateDto State);

/// <summary>A decoded frame: type, request id and the typed payload.</summary>
internal readonly record struct BridgeFrame(BridgeMessageType Type, int RequestId, object Payload);

internal static class BridgeCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>Encode a full frame (length prefix included).</summary>
    public static byte[] Encode(int requestId, object payload)
    {
        using var ms = new MemoryStream(32);
        using var w = new BinaryWriter(ms, Utf8);
        w.Write(0); // length placeholder
        switch (payload)
        {
            case HelloMessage m:
                w.Write((byte)BridgeMessageType.Hello); w.Write(requestId);
                w.Write(m.Version); w.Write(m.ClientPid);
                break;
            case HelloAckMessage m:
                w.Write((byte)BridgeMessageType.HelloAck); w.Write(requestId);
                w.Write(m.Version); w.Write(m.BridgePid);
                break;
            case TfTorqueMessage m:
                w.Write((byte)BridgeMessageType.TfTorque); w.Write(requestId);
                w.Write(m.Session); w.Write(m.Value);
                break;
            case TfCommandMessage m:
                w.Write((byte)BridgeMessageType.TfCommand); w.Write(requestId);
                w.Write(m.Session); w.Write((byte)m.Op); w.Write(m.Value); w.Write(m.IntValue); w.Write(m.Flag);
                break;
            case TfRequestMessage m:
                w.Write((byte)BridgeMessageType.TfRequest); w.Write(requestId);
                w.Write(m.Session); w.Write((byte)m.Op);
                break;
            case TfStatusMessage m:
                w.Write((byte)BridgeMessageType.TfStatus); w.Write(requestId);
                w.Write(m.Ok); w.Write(m.IsInitialized); w.Write(m.IsConnected); w.Write(m.IsPaused); w.Write(m.IsEngaged);
                w.Write(m.PacketsSent); w.Write(m.PacketsFailed); w.Write(m.RotationDegrees);
                w.Write(m.ProductName ?? ""); WriteNullable(w, m.LastError);
                break;
            case HidppRequestMessage m:
                w.Write((byte)BridgeMessageType.HidppRequest); w.Write(requestId);
                w.Write(m.Session); w.Write((byte)m.Op); w.Write(m.IntArg); w.Write(m.FloatArg);
                w.Write(m.WaitForResponse); w.Write(m.TimeoutMs);
                w.Write(m.Fields.Length); foreach (var f in m.Fields) w.Write(f ?? "");
                w.Write(m.Capacities.Length); foreach (var c in m.Capacities) w.Write(c);
                break;
            case HidppResultMessage m:
                w.Write((byte)BridgeMessageType.HidppResult); w.Write(requestId);
                w.Write(m.Ok);
                var s = m.State;
                w.Write(s.IsConnected); w.Write(s.ProductName ?? ""); w.Write(s.LastError ?? "");
                w.Write(s.DiagnosticSummary ?? ""); w.Write(s.ProfileMode); w.Write(s.FfbStrengthNm);
                w.Write(s.RotationDegrees); w.Write(s.TrueForceLevelPct); w.Write(s.DampingPct);
                w.Write(s.LastSettingsReadOk); w.Write(s.OledAvailable); w.Write(s.OledLayoutCount);
                break;
            default:
                throw new ArgumentException($"Unknown bridge payload {payload?.GetType().Name}");
        }
        w.Flush();
        var buf = ms.ToArray();
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), buf.Length - 4);
        return buf;
    }

    /// <summary>Decode a frame body (everything after the length prefix).</summary>
    public static BridgeFrame Decode(byte[] body)
    {
        using var r = new BinaryReader(new MemoryStream(body), Utf8);
        var type = (BridgeMessageType)r.ReadByte();
        int id = r.ReadInt32();
        object payload = type switch
        {
            BridgeMessageType.Hello => new HelloMessage(r.ReadInt32(), r.ReadInt32()),
            BridgeMessageType.HelloAck => new HelloAckMessage(r.ReadInt32(), r.ReadInt32()),
            BridgeMessageType.TfTorque => new TfTorqueMessage(r.ReadInt32(), r.ReadSingle()),
            BridgeMessageType.TfCommand => new TfCommandMessage(r.ReadInt32(), (TfCommandOp)r.ReadByte(), r.ReadSingle(), r.ReadInt32(), r.ReadBoolean()),
            BridgeMessageType.TfRequest => new TfRequestMessage(r.ReadInt32(), (TfRequestOp)r.ReadByte()),
            BridgeMessageType.TfStatus => new TfStatusMessage
            {
                Ok = r.ReadBoolean(), IsInitialized = r.ReadBoolean(), IsConnected = r.ReadBoolean(),
                IsPaused = r.ReadBoolean(), IsEngaged = r.ReadBoolean(),
                PacketsSent = r.ReadInt64(), PacketsFailed = r.ReadInt64(), RotationDegrees = r.ReadInt32(),
                ProductName = r.ReadString(), LastError = ReadNullable(r),
            },
            BridgeMessageType.HidppRequest => ReadHidppRequest(r),
            BridgeMessageType.HidppResult => new HidppResultMessage(r.ReadBoolean(), new HidppStateDto
            {
                IsConnected = r.ReadBoolean(), ProductName = r.ReadString(), LastError = r.ReadString(),
                DiagnosticSummary = r.ReadString(), ProfileMode = r.ReadByte(), FfbStrengthNm = r.ReadSingle(),
                RotationDegrees = r.ReadInt32(), TrueForceLevelPct = r.ReadInt32(), DampingPct = r.ReadInt32(),
                LastSettingsReadOk = r.ReadBoolean(), OledAvailable = r.ReadBoolean(), OledLayoutCount = r.ReadInt32(),
            }),
            _ => throw new InvalidDataException($"Unknown bridge message type {(byte)type}"),
        };
        return new BridgeFrame(type, id, payload);
    }

    /// <summary>Read one frame from a stream; null on clean end-of-stream.</summary>
    public static BridgeFrame? ReadFrame(Stream s)
    {
        var lenBuf = new byte[4];
        if (!ReadExactly(s, lenBuf)) return null;
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len < 5 || len > BridgeProtocol.MaxBodyLength)
            throw new InvalidDataException($"Bad bridge frame length {len}");
        var body = new byte[len];
        if (!ReadExactly(s, body)) return null;
        return Decode(body);
    }

    private static HidppRequestMessage ReadHidppRequest(BinaryReader r)
    {
        int session = r.ReadInt32();
        var op = (HidppOp)r.ReadByte();
        int intArg = r.ReadInt32();
        float floatArg = r.ReadSingle();
        bool wait = r.ReadBoolean();
        int timeout = r.ReadInt32();
        int nf = r.ReadInt32();
        if (nf < 0 || nf > 64) throw new InvalidDataException("Bad field count");
        var fields = new string[nf];
        for (int i = 0; i < nf; i++) fields[i] = r.ReadString();
        int nc = r.ReadInt32();
        if (nc < 0 || nc > 64) throw new InvalidDataException("Bad capacity count");
        var caps = new int[nc];
        for (int i = 0; i < nc; i++) caps[i] = r.ReadInt32();
        return new HidppRequestMessage
        {
            Session = session, Op = op, IntArg = intArg, FloatArg = floatArg,
            WaitForResponse = wait, TimeoutMs = timeout, Fields = fields, Capacities = caps,
        };
    }

    private static void WriteNullable(BinaryWriter w, string? s)
    {
        w.Write(s != null);
        if (s != null) w.Write(s);
    }

    private static string? ReadNullable(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static bool ReadExactly(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = s.Read(buf, off, buf.Length - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
