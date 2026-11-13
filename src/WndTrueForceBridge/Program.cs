// WndTrueForceBridge — GPL-2.0 helper process that owns the Logitech
// TrueForce stream and HID++ settings interface for FFB Tuner.
//
// Copyright (C) 2026 WnDTech
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; version 2 of the License.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// Usage: WndTrueForceBridge.exe --pipe <name> --parent-pid <pid> [--fake-device]
using System.Diagnostics;
using WndTech.TrueForceBridge.Protocol;
using WndTrueForceBridge.Devices;

namespace WndTrueForceBridge;

internal static class Program
{
    private static int Main(string[] args)
    {
        string? pipe = null;
        int parentPid = 0;
        bool fake = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == BridgeProtocol.PipeArg && i + 1 < args.Length) pipe = args[++i];
            else if (args[i] == BridgeProtocol.ParentPidArg && i + 1 < args.Length) int.TryParse(args[++i], out parentPid);
            else if (args[i] == BridgeProtocol.FakeDeviceArg) fake = true;
        }
        if (string.IsNullOrWhiteSpace(pipe))
        {
            BridgeLog.Write("Missing --pipe argument — exiting");
            return 2;
        }

        BridgeLog.Write($"=== WndTrueForceBridge start: pipe={pipe}, parent={parentPid}, backend={(fake ? "fake" : "logitech")} ===");
        var server = new BridgeServer(pipe, fake ? new FakeBackend() : new LogitechBackend());

        // Crash safety inside the bridge itself.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            BridgeLog.Write($"Unhandled exception: {e.ExceptionObject}");
            server.EmergencyTeardownAndExit();
        };

        // Parent watchdog: if the app process dies without closing the pipe
        // cleanly, tear the TrueForce session down and exit.
        if (parentPid > 0)
        {
            var watch = new Thread(() =>
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    parent.WaitForExit();
                }
                catch (ArgumentException) { /* already gone */ }
                catch (Exception ex) { BridgeLog.Write($"Parent watch failed: {ex.Message}"); return; }
                BridgeLog.Write($"Parent process {parentPid} exited — tearing down");
                server.TeardownAll();
                Environment.Exit(0);
            }) { IsBackground = true, Name = "ParentWatch" };
            watch.Start();
        }

        server.Run(TimeSpan.FromSeconds(30));
        server.TeardownAll();
        BridgeLog.Write("=== WndTrueForceBridge exit ===");
        return 0;
    }
}

internal static class BridgeLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "trueforce_bridge.log");

    public static void Write(string msg)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{Environment.ProcessId}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
