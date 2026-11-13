# WndTrueForceBridge

GPL-2.0-only helper process used by FFB Tuner (WnDTech) to drive Logitech
direct-drive wheels (RS50, G PRO, G923):

- **TrueForce stream** — writes force to the wheel's TrueForce HID interface
  (usage page 0xFFFD), including the G HUB init replay and the session-end
  handshake.
- **HID++ settings** — FFB strength, rotation range, desktop/onboard profile
  mode, TrueForce/damping read-back and the Dynamic OLED (feature 0x8130).

FFB Tuner starts `WndTrueForceBridge.exe --pipe <name> --parent-pid <pid>` next
to its own exe and talks to it over a per-launch named pipe. The wire contract
(`src/Shared/TrueForceBridgeProtocol.cs`) is MIT-licensed and contains only
message ids/DTOs. When the pipe closes or the parent process exits, the bridge
releases the TrueForce session (session-end handshake) and exits.

This project does not reference, and is not linked into, the FFB Tuner
application.

## Licence

GPL-2.0-only — see [LICENSE](LICENSE).

## Upstream credits

- **Trueforce-For-All** — https://github.com/Mhytee/Trueforce-For-All,
  Copyright (c) Mhytee, GPL-2.0-only: stream pump timing and precision sleep,
  session sequencing, init replay loop.
- **logitech-trueforce-linux-driver** —
  https://github.com/mescon/logitech-trueforce-linux-driver, Copyright (c)
  mescon, GPL-2.0: the 68-packet G HUB init capture (`tf_init_data.h`),
  `patch_range_packet`, and the HID++ protocol reference.

Their copyright notices are retained in `Providers/*.cs`.
