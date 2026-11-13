# WndTrueForceBridge

Source code of the TrueForce helper program that ships with **FFB Tuner** (WnDTech) and
**WND SimSuite**. It drives Logitech direct-drive wheels (RS50, G PRO, G923):

- **TrueForce stream**: writes force to the wheel's TrueForce HID interface
  (usage page 0xFFFD), including the G HUB init replay and the session-end handshake.
- **HID++ settings**: FFB strength, rotation range, desktop/onboard profile mode,
  TrueForce/damping read-back and the Dynamic OLED (feature 0x8130).

FFB Tuner starts `WndTrueForceBridge.exe --pipe <name> --parent-pid <pid>` next to its own
exe and talks to it over a per-launch named pipe. When the pipe closes or the parent process
exits, the bridge releases the TrueForce session and exits. FFB Tuner itself is separate,
closed-source software. It does not include or link to this code.

## Licence

- The bridge (`src/WndTrueForceBridge`) is **GPL-2.0-only**. See [LICENSE](LICENSE).
- The wire contract (`src/Shared/TrueForceBridgeProtocol.cs`) is **MIT**. It holds only
  message ids and data shapes.

This repository is the source offer referred to in FFB Tuner's THIRD-PARTY-NOTICES.
Each tag matches the bridge shipped in an FFB Tuner release; for example, `v1.0.0` is the
bridge in FFB Tuner 1.29.0-beta.9.

## Build

Requires the .NET 8 SDK on Windows.

```
dotnet build src/WndTrueForceBridge/WndTrueForceBridge.csproj -c Release
```

## Upstream credits

- **Trueforce-For-All**, https://github.com/Mhytee/Trueforce-For-All,
  Copyright (c) Mhytee, GPL-2.0-only: stream pump timing and precision sleep,
  session sequencing, init replay loop.
- **logitech-trueforce-linux-driver**,
  https://github.com/mescon/logitech-trueforce-linux-driver, Copyright (c) mescon, GPL-2.0:
  the 68-packet G HUB init capture (`tf_init_data.h`), `patch_range_packet`, and the
  HID++ protocol reference.

Their copyright notices are retained in `src/WndTrueForceBridge/Providers/*.cs`.
