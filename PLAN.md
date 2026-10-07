# Layerlapse — Build Plan for Claude Code

2026-10-06

## Brief for Claude Code

Build **Layerlapse**, a cross-platform desktop app for macOS, Windows and Linux that finds a Bambu Lab printer on the local network and lets its owner browse, play and download timelapses, then models. Everything below is a requirement unless marked as a suggestion.

**Working rules**

1. Read this whole doc before writing code. Build the milestones in order and stop after each one to report what works and what you could not verify.
2. Test against the real printer. Read its IP and access code from the environment variables `LAYERLAPSE_IP` and `LAYERLAPSE_CODE`. Never commit, log or print the access code.
3. Tag tests that need a printer so they run only when those variables are set.
4. When you answer an open question, record the finding in `docs/FINDINGS.md` with the command or capture that proved it.
5. Stay read-only until the delete milestone. No code path may write to or delete from the printer before then, and never inside `certificate`.
6. Keep FTPS and file-system details out of the UI project. They live in Core.
7. Do not use Bambu Lab logos or trademarks in the app name or icon. The About box states that the app is unofficial.

**Stack (decided)**

.NET (current LTS) with Avalonia UI and MVVM, BouncyCastle.Cryptography (managed TLS 1.2 with session resumption) plus a small read-only FTP client in Core for implicit FTPS, LibVLCSharp for playback (fallback: open the file in the OS default player), xUnit for tests.

**Repository layout**

- `src/Layerlapse.Core`: printer client, discovery, cache, credential store interface. No UI references.
- `src/Layerlapse.App`: Avalonia views, view models, theme resources.
- `tests/Layerlapse.Core.Tests`: unit tests plus the opt-in printer tests.
- `docs/FINDINGS.md`: answers to the open questions.

**Core interfaces (sketch, adjust as needed)**

- `IPrinterDiscovery` streams discovered printers: name, serial and model when known, IP.
- `IPrinterClient` connects, lists a folder, downloads with progress and resume, and returns thumbnail bytes.
- `ICredentialStore` gets, saves and removes the access code per printer, with Keychain, Credential Manager and Secret Service implementations.
- `ICache` keeps listings, thumbnails and downloaded videos on disk, with a size cap.

**Before milestone 1**

Scaffold the solution, the theme resources (see Look and feel) and a CI workflow that builds on macOS, Windows and Linux. Done when an empty themed window opens on all three.

## Goal and scope

Build a small app that finds a Bambu Lab printer on the local network, remembers its access code, and lets the owner browse, preview and download timelapse videos without a phone or Bambu Studio. Models (`.3mf` files) come second.

**In scope**

- Find the printer automatically and show its model, name and IP
- Enter the access code once and have the app remember it
- List timelapses with thumbnails, play them, download one or many
- Later: list and download models from the printer's storage

**Out of scope for v1**

- Starting, stopping or controlling prints (MQTT)
- Cloud login, remote access outside the home network, live camera view
- Anything that writes to the printer other than an explicit, confirmed delete

## Recommended approach

Build a downloadable desktop app, not a hosted web app. Browsers cannot open raw TCP connections, so they cannot speak FTPS to the printer or listen for its network announcements. Any web version needs a local server doing that work anyway.

| Option | Fits the job? | Notes |
| --- | --- | --- |
| Desktop app (C# with Avalonia) | Best | Native discovery, OS credential store, one installer per platform, works offline. Matches a stack already used for cross-platform direct-download apps |
| Local web app (Python or Node server plus browser UI) | Good for a fast prototype | Needs a terminal to start unless packaged; the server must bind to localhost only |
| Hosted web app | No | A cloud server cannot reach a private address like the printer's |

**Suggested stack (desktop)**

- .NET with Avalonia for the UI on macOS, Windows and Linux
- BouncyCastle TLS plus a minimal FTP client for implicit FTPS, with certificate pinning (FluentFTP was tried and cannot reuse TLS sessions, which the printer requires; see docs/FINDINGS.md)
- A discovery module using UDP multicast, with a TCP fallback (see Architecture)
- LibVLCSharp, or the OS default player, for playback
- The OS credential store for the access code

Decided on 2026-10-06: build the desktop app in C# with Avalonia, starting with the FTPS spike in milestone 1. Also decided on 2026-10-06, after the spike: use BouncyCastle instead of FluentFTP. The printer facts and gotchas below apply to any stack.

## Verified printer facts

These were confirmed on the owner's printer on 2026-10-06 using `curl` from a Mac on the same network. Treat them as the ground truth for the first build.

- **Protocol:** implicit FTPS (TLS from the first byte) on port 990. User `bblp`, password is the printer's access code, found on the printer screen under Settings, then LAN Only Mode (the mode does not need to be switched on).
- **Certificate:** self-signed, so the client must skip normal validation.
- **Timelapses:** `/timelapse/` holds files named `video_YYYY-MM-DD_HH-MM-SS.mp4`, plus a `thumbnail` subfolder. Around 80 videos, from about 0.2 MB to 57 MB each.
- **Models:** loose in the storage root, not in a `model` folder. Mostly `*.gcode.3mf` (sliced project files), a few plain `.3mf`.
- **Other root entries:** folders `timelapse`, `ipcam` and `certificate`, and a 16-byte file `verify_job`. The app must never touch `certificate` or `verify_job`.
- **Listing format:** Unix `ls -l` style. Recent files show a time, older files show a year instead.
- **Filenames:** include spaces, commas, `&`, `#`, parentheses, non-ASCII characters (for example Chinese and German accented text) and long names truncated with `...`.

Not yet checked: what is inside `thumbnail/` and `ipcam/`, what is inside a `.gcode.3mf`, and the printer model and firmware version.

## Architecture

The UI talks to four modules and never speaks FTPS itself. The printer client is the only component that touches the printer's files, so a library or firmware change stays contained there.

```
+----------------------------------------------------------+
| App UI: setup, timelapse grid, model grid, video player  |
+----------------------------------------------------------+
     |               |                |               |
+-----------+  +----------------+  +-------------+  +------------------+
| Discovery |  | Printer client |  | Local cache |  | Credential store |
+-----------+  +----------------+  +-------------+  +------------------+
     ^               ^ |
     | announcements | | FTPS on port 990
     | (UDP)         | v
+----------------------------------------------------------+
| The printer: timelapse folder, model files, thumbnails   |
+----------------------------------------------------------+
```

## Look and feel

Match Bambu Studio's flat, dark, green-accented look so the app feels like part of the same toolchain. The green `#00AE42` is the well-known Bambu green; the neutrals below are approximations to be replaced by colours sampled from a real Studio screenshot.

| Token | Dark | Light | Use |
| --- | --- | --- | --- |
| accent | `#00AE42` | `#00AE42` | Primary buttons, selection, progress bars |
| accent-hover | `#1AC25A` | `#009A3A` | Hover state (derived, tune by eye) |
| accent-pressed | `#009A3A` | `#008A34` | Pressed state (derived) |
| background | `#1F1F22` | `#FFFFFF` | Window and content area |
| panel | `#2D2D31` | `#F5F5F5` | Sidebar, toolbars |
| card | `#38383C` | `#FFFFFF` | Thumbnail cards, dialogs |
| border | `#4A4A50` | `#D8D8D8` | 1 px outlines and dividers |
| text | `#F2F2F3` | `#262E30` | Body text |
| text-secondary | `#A8A8AF` | `#6B6B6B` | Dates, sizes, hints |
| danger | `#E5484D` | `#D13438` | Errors and delete confirmation |

**Rules**

- Flat and compact: corner radius 4 or less, 1 px borders, no shadows or gradients, dense list rows.
- Layout: a left sidebar on the panel colour (printer picker, Timelapses, Models), content on the background, a grid of cards with a thumbnail, title and size.
- Green means primary action, selection or progress. Everything else stays neutral.
- Dark by default, with a manual toggle to light. Design dark first.
- Define every colour once, as named brushes in `Theme.axaml`. No hex values anywhere else.
- White text on `#00AE42` is only about 3:1, too low for small text. Use bold labels of 14 px or more on green, or dark text.
- Exception (owner's request, 2026-10-07): an optional **Windows XP** theme, offered on Windows only, recreates the XP look after XP.css (gradients, rounded glossy buttons, blue title bar drawn by the app). Its colours also live in `Theme.axaml`; Dark stays the default everywhere.

## Milestones and acceptance criteria

Build in this order. Each milestone is done only when its acceptance line is true on the real printer.

1. **Spike (no UI).** Connect with the chosen FTPS library, list `/timelapse/`, download one small video. *Accepted when* the file is byte-identical to what `curl` downloads and plays.
2. **Connection and credentials.** Manual IP and access code entry, a Test button, save to the OS credential store, auto-reconnect to the last printer at launch. Pin the printer's certificate fingerprint on first successful connect. *Accepted when* a restart connects with no typing and a wrong code shows a clear error.
3. **Discovery and model detection.** Find printers via network announcements, fall back to a port 990 scan, fall back to manual entry. Show model, name, serial and IP; let the user pick when several are found. *Accepted when* the owner's printer appears without typing its IP, and its model is shown or the user is asked to pick it.
4. **Timelapse browser.** List newest first, with thumbnail, start time parsed from the filename, size and an approximate print duration (modified time minus start time, labelled approximate). Filter by date range. Play by downloading to a cache folder first, then opening in the built-in or default player. *Accepted when* 80 videos list in a few seconds on the second launch and a video plays with seeking.
5. **Download.** Single and multi-select, choose destination, progress, cancel, resume after a dropped connection, skip files that already exist. *Accepted when* an interrupted 50 MB download resumes instead of restarting.
6. **Models.** List root `*.3mf` files with a preview image extracted from each archive, search by name, download. *Accepted when* the grid shows previews for most models and downloads open in Bambu Studio.
7. **Extras.** Confirmed single-file delete, storage usage, auto-download new timelapses to a folder, multiple saved printers, signed installers for each platform.

## Gotchas

Test the riskiest of these in milestone 1, before any UI exists.

- **Implicit, not explicit, FTPS.** Many FTP libraries default to explicit TLS (connect, then upgrade). The printer needs TLS from the first byte. Confirm the library supports implicit mode on port 990.
- **Data connections.** Some stock FTP clients have trouble with TLS on the data channel against this printer, which is why `curl` and `lftp` are the common test tools. If the chosen library fails, try another before blaming the printer.
- **Self-signed certificate.** Accept it only through pinning on first connect, never by silently disabling all checks.
- **Fragile device.** Community reports describe network drops during uploads on some models. Keep to one connection at a time, retry with resume, and avoid heavy transfers during a print.
- **Filename handling.** Treat names as UTF-8, escape every path before use, and never assume a name is unique after truncation (`...`). Do not parse model names; only the timelapse timestamp pattern is reliable.
- **Resume and caching.** Cache directory listings and thumbnails on disk and refresh in the background. Use FTP restart offsets for resumable downloads.
- **Access code and IP drift.** A factory reset changes the access code and may change the IP. Identify the printer by serial number when known, not by IP, and rediscover on failure.
- **Operating system prompts.** macOS asks for Local Network permission the first time the app scans, and Windows may show a firewall prompt. Explain this in the UI before triggering it.
- **Firmware changes.** In January 2025, Bambu firmware began requiring authorization for some local control features, which broke many third-party tools. Read-only file access works on the owner's printer today. Handle auth failures with a clear message, not a crash.
- **Empty storage.** Timelapse files exist only if timelapse recording is on and storage is inserted. Show a helpful empty state.

## Security and safety

The app reads files from a device that holds the owner's only copy of some of them, so default to read-only.

- Store the access code in the OS credential store (Keychain on macOS, Credential Manager on Windows, Secret Service on Linux). Never write it to a plain config file, log it or put it in a URL.
- Never ship a real access code in source, tests or screenshots. Use a placeholder in docs and fixtures.
- Delete is off by default. When enabled: one file at a time, a confirmation naming the file, no bulk delete, and never in `certificate`, `verify_job` or the root folder listing.
- Downloads go to a user-chosen folder; refuse any remote filename containing path separators or `..`.
- Talk only to a private-range address on the local network. If a web UI is used, bind its server to localhost.
- Pin the printer certificate on first use and warn loudly if it later changes.

## Open questions to check on the real printer

Resolve these during milestones 1 to 3, and write the answers back into this doc.

- [x] The printer is an X1 series model. Which exact model (X1, X1 Carbon or X1E) and which firmware version? (Printer screen, or the discovery announcement.) *X1 Carbon (`DevModel: BL-P001`, serial prefix `00M`), firmware 01.12.00.00, both from the UDP announcement (2026-10-06).*
- [x] What is in `/timelapse/thumbnail/`: one image per video, and do the names match the videos? *Yes: one 11–18 KB JPEG per video with the same base name (2026-10-06, see docs/FINDINGS.md).*
- [x] What is in `/ipcam/`? Possibly camera recordings, which could be a later feature. *Yes: ~250 MB five-minute `ipcam-record.*.mp4` segments plus an `index` file, ~10 GB in total (2026-10-06).*
- [x] What is inside a `.gcode.3mf`: where is the preview image, and is the original mesh included? *Preview at `Metadata/plate_1.png` (512×512); no mesh (`3D/3dmodel.model` has no vertices); `slice_info.config` has print time, weight and filaments (2026-10-07, see docs/FINDINGS.md).*
- [x] Do printers announce themselves over UDP multicast, and does the announcement include model code, name and serial? Capture one with a packet tool before writing the discovery module. Forum logs suggest the FTP greeting also carries a model-like token, which would be a fallback. *Yes: an SSDP-style NOTIFY on UDP 2021 every 5 s with IP, serial, model code, name and firmware. The FTP greeting has no model token; the certificate CN (serial) is the fallback (2026-10-06).*
- [x] Is the timestamp in a timelapse filename the print start, and the file's modified time the end? *Yes, but on different clocks: the filename uses the printer's own time zone (UTC−5 here) and the modified time is UTC. The app corrects for the offset, measured from camera recordings. Files from before Dec 2025 used a different printer clock, so their durations are unknown (2026-10-07, see docs/FINDINGS.md).*
- [ ] Does listing or downloading during an active print cause any slowdown or disconnects?
- [x] Does the chosen FTPS library work against this printer without workarounds? *No. FluentFTP fails with `522 session reuse required` on macOS and Linux. A BouncyCastle-based client works (2026-10-06, see docs/FINDINGS.md).*

## Decisions needed from the owner

- [ ] Check that the name Layerlapse is free to use (GitHub, app stores, domains).
- [x] Platforms: macOS, Windows and Linux for version 1.
- [x] Printer: an X1 series model.
- [x] Theme: dark by default, with a manual toggle.
- [ ] Provide a Bambu Studio screenshot in dark and light so exact colours can be sampled.
- [ ] Where should downloads go by default?
- [x] In-app updates, or manual download of each new version? *An update check that links to the release page; it never installs anything. Off until a release feed exists (2026-10-07).*
- [x] Repository public or private, and which licence? *Public at github.com/ziggy46/Layerlapse, MIT licence (2026-10-07).*
- [x] Should the app ever be able to delete files from the printer? *Timelapses only, off by default, one at a time with confirmation (2026-10-07).*
- [x] Printer time zone setting: not now; the offset is measured from camera recordings (2026-10-07).
- [x] Video cache cap: 2 GB, least recently used removed first (2026-10-07).
- [x] Repository layout: exactly as planned, with no extra projects or scripts (2026-10-07).
- [x] Installers: unsigned for now, built by `.github/workflows/release.yml`; signing switches on when certificates are added (2026-10-07).
- [x] Auto-download: while the app is open, off by default (2026-10-07).
- [x] Built-in player: OS-native players (2026-10-07). LibVLC has no Apple Silicon build on NuGet (see FINDINGS). macOS uses AVFoundation, Windows Media Foundation (MFPlay), Linux GStreamer (2026-10-07); each falls back to the default player.
