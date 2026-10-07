# Findings

Answers to the open questions in [PLAN.md](../PLAN.md), each with the command that proved it.

Conventions: `<printer-ip>` stands for `$LAYERLAPSE_IP`. The access code is only ever passed as
`$LAYERLAPSE_CODE` and never appears in output (no `curl -v` on authenticated calls without filtering
out the `PASS` line). The printer serial and certificate fingerprint are shortened, because the repository
may become public.

## 2026-10-06: milestone 1 (FTPS spike)

Environment: macOS (Darwin 27), .NET SDK 10.0.401, FluentFTP 55.0.0, BouncyCastle.Cryptography 2.7.0,
curl 8.7.1 (LibreSSL). Linux checks ran in `mcr.microsoft.com/dotnet/sdk:10.0` (OpenSSL 3.0.13) via OrbStack.

### Does FluentFTP work against this printer without workarounds? **No.**

FluentFTP connects and logs in over implicit TLS on port 990, but the first data transfer fails:

```
FluentFTP.Exceptions.FtpCommandException: Code: 522 Message: SSL connection failed: session reuse required
```

The server is vsftpd with `require_ssl_reuse`: every data connection must resume the control
connection's TLS session. .NET's `SslStream`, which FluentFTP uses, does not do this. The same failure
happens on macOS and in a Linux container, so it is not a macOS quirk. Windows was not tested.
FluentFTP has no setting for this. Its `FluentFTP.GnuTLS` add-on can resume sessions, but it ships native
GnuTLS DLLs for Windows only, so macOS and Linux users would need GnuTLS installed separately.

```bash
git checkout f4db283   # last commit with the FluentFTP client
dotnet run --project tools/Layerlapse.Spike -- --library fluentftp <out-dir>   # 522 on LIST
```

**Decision (2026-10-06):** use BouncyCastle. FluentFTP and its client were removed from Core after
commit `f4db283`.

**What works instead:** `BambuFtpsClient` in Core, which uses BouncyCastle's managed TLS (pure C#, no
native code) plus a minimal read-only FTP layer (USER, PASS, PBSZ, PROT P, TYPE I, PASV, LIST, RETR, QUIT).
Each data connection resumes the control session, and the client checks that the resumption really happened.
It works on macOS and Linux:

```bash
dotnet run --project tools/Layerlapse.Spike -- <out-dir>
dotnet test --filter Category=Printer      # 4 printer tests, all pass with LAYERLAPSE_* set
```

The printer also refuses an unencrypted data channel, so falling back to `PROT C` is not an option
(and would send video in clear anyway):

```bash
curl -v -sS -k --ftp-ssl-control -u "bblp:$LAYERLAPSE_CODE" "ftps://<printer-ip>:990/timelapse/" -o /dev/null 2>&1 | grep -vi pass
# > PROT C
# < 200 PROT now Clear.
# > LIST
# < 522 Data connections must be encrypted.
```

### Milestone 1 acceptance: download is byte-identical to curl and plays. **Yes.**

Smallest video, `video_2026-05-13_11-25-16.mp4`, 184,040 bytes:

```bash
dotnet run --project tools/Layerlapse.Spike -- bc/
curl -sS -k -u "bblp:$LAYERLAPSE_CODE" "ftps://<printer-ip>:990/timelapse/video_2026-05-13_11-25-16.mp4" -o curl/video.mp4
shasum -a 256 bc/video_2026-05-13_11-25-16.mp4 curl/video.mp4
# cbcc010b3234e71502ce9870307d1ed42b02e23b1063552a84f540744b7a72c3 (both)
cmp bc/video_2026-05-13_11-25-16.mp4 curl/video.mp4     # no differences
ffprobe ...    # h264 1760x1080, 24 fps, 5 frames, 0.17 s
ffmpeg -v error -i bc/video_2026-05-13_11-25-16.mp4 -f null -   # decodes with no errors
```

The extracted frame shows the print bed and toolhead. The same hash came from the Linux container.
Timing on the LAN: connect plus login about 1.1 s, listing 75 entries about 80 ms, 184 KB download about 220 ms.

### Which exact model and firmware? **X1 Carbon, firmware 01.12.00.00.**

- The TLS certificate's subject CN is the printer serial (`00M…`, 15 characters). It is issued by
  `C=CN, O=BBL Technologies Co., Ltd, CN=BBL CA` and valid from 2025-03-12 to 2035-03-10. Community serial
  tables map the `00M` prefix to the **X1 Carbon**. The UDP announcement confirms it (model code
  `BL-P001`, see below).
- The FTP greeting is just `220 (vsFTPd 3.0.5)`. **It does not contain a model token**, so the
  "model from FTP greeting" fallback in the plan does not work on this firmware.
- Firmware version: not visible over FTPS. It is in the UDP announcement (see below): `01.12.00.00`.

```bash
(sleep 3; printf 'QUIT\r\n') | openssl s_client -connect <printer-ip>:990 -showcerts > out.txt
openssl x509 -in out.txt -noout -subject -issuer -dates -fingerprint -sha256
# subject=CN=00M…   issuer=C=CN, O=BBL Technologies Co., Ltd, CN=BBL CA
# sha256 Fingerprint=14:5C:6B:BE:…:65:56
# Protocol TLSv1.2, cipher ECDHE-RSA-AES256-GCM-SHA384; first line after TLS: 220 (vsFTPd 3.0.5)
```

Implication for the build: the certificate is signed by Bambu's CA, but the chain is not sent and the CA is
not in any OS trust store. Trust-on-first-use pinning by SHA-256 of the leaf certificate is implemented in
`BambuFtpsClient`. A wrong pin throws `PrinterCertificateMismatchException` (covered by a printer test).
The certificate CN also gives the serial without discovery, which is useful for "identify the printer by
serial, not IP".

### What is in `/timelapse/thumbnail/`? **One JPEG per video, same base name.**

74 files named `video_YYYY-MM-DD_HH-MM-SS.jpg`, 11–18 KB each. The names match the 74 videos one to one:

```bash
diff <(list /timelapse/ | *.mp4 minus extension) <(list /timelapse/thumbnail/ | *.jpg minus extension)   # no output
```

### What is in `/ipcam/`? **Continuous camera recordings, plus an index file.**

43 entries: an `index` file (4 bytes) and files named `ipcam-record.YYYY-MM-DD_HH-MM-SS.N.mp4`, about
250 MB each, written roughly every five minutes. Together that is around 10 GB. This could be a later
feature, but the files are large: download them only on request and never by default. Nothing was downloaded.

### Is the filename timestamp the print start and the modified time the end? **Consistent with it, not proven.**

- `video_2026-10-04_09-44-43.mp4` was modified at `Oct 04 23:44`. `video_2026-10-03_17-47-45.mp4`
  (799 KB, a short print) was modified at `Oct 03 23:07`.
- The two clocks seem to differ: the 5-minute ipcam segment `ipcam-record.2026-10-04_15-46-38.3.mp4`
  has a listing time of `20:51`. That is about 5 h after the name plus the 5-minute segment length. So the
  listing time is probably UTC, and the filename is probably the printer's local time (UTC−5, while the Mac
  is on EDT, UTC−4).
- If so, the short print ran from 17:47 local to 18:07 local (≈20 min), which is plausible for 799 KB.
- To confirm, compare one print's start and end against Bambu Studio's or the printer's print history,
  and check the printer's timezone setting. Until then, label durations "approximate" as the plan says.
  Older listing entries show only a date (`Jul 07  2025`), so durations are available only for files from
  the last ~6 months.

### Listing and filename details

- `/` has 180 entries: 3 directories (`certificate`, `ipcam`, `timelapse`), `verify_job` (16 bytes) and
  176 model files (174 `.gcode.3mf`, 2 plain `.3mf`). 9 names are non-ASCII and 6 are truncated with `...`. All 180 parse as UTF-8 without
  replacement characters, matching curl's line count exactly (printer test `Lists_root_with_unicode_names`).
- `/timelapse/` has 74 videos (the plan said about 80), from 184,040 bytes to 56,776,595 bytes (`video_2026-09-12_22-39-03.mp4`).
- The listing format is vsftpd `ls -l`: recent entries show `Mon DD HH:MM`, older ones `Mon DD  YYYY`.
  Owner and group are numeric (`1002`).

## 2026-10-06: network announcements (milestone 3 groundwork)

### Do printers announce themselves over UDP, with model, name and serial? **Yes.**

A passive 30-second listen (bind UDP 2021 and 1990, join 239.255.255.250, send nothing) received six
452-byte SSDP-style `NOTIFY` packets from the printer, **one every 5 seconds, from and to port 2021**.
Nothing arrived on 1990, even though the packet's `Host` header names port 1990.

```
NOTIFY * HTTP/1.1
Host: 239.255.255.250:1990
Server: UPnP/1.0
Location: <printer-ip>
NT: urn:bambulab-com:device:3dprinter:1
NTS: ssdp:alive
USN: 00M…                         (serial; identical to the TLS certificate CN)
Cache-Control: max-age=1800
DevModel.bambu.com: BL-P001       (model code)
DevName.bambu.com: X1 Carbon      (printer name, user-editable in Bambu apps)
DevSignal.bambu.com: -40          (Wi-Fi RSSI; varied -39 to -41, the only field that changed)
DevConnect.bambu.com: cloud
DevBind.bambu.com: occupied
Devseclink.bambu.com: secure
DevInf.bambu.com: wlan0
DevVersion.bambu.com: 01.12.00.00 (firmware)
DevCap.bambu.com: 1
```

```bash
python3 listen.py   # bind ("", 2021) and ("", 1990), IP_ADD_MEMBERSHIP 239.255.255.250, select() for 30 s, print payloads
```

Notes for the discovery module:

- Everything the setup screen needs is in one packet: IP (`Location`), serial (`USN`), model code
  (`DevModel`), name (`DevName`) and firmware (`DevVersion`). No login is needed.
- `DevName` is the printer's name, not its model. It reads "X1 Carbon" here because that is the default
  name, so map the model from `DevModel` instead. `BL-P001` = X1 Carbon matches community tables and the
  `00M` serial prefix. Codes for other models are still unverified.
- Listen on UDP 2021. Re-announcing every 5 s means a 6–10 s listen is enough; `max-age=1800` means
  a printer can be treated as gone after missing announcements for a while.
- The Python process received the packets without a macOS Local Network prompt. A bundled app may still
  trigger one, as the plan warns.
- The USN serial equals the certificate CN, so a printer found by the port 990 fallback can be matched
  to a discovered one by serial.

## 2026-10-06: milestone 2 (connection and credentials)

### What does a wrong access code look like? **`530` at `PASS`.**

```bash
curl -sS -k -u "bblp:00000000" "ftps://<printer-ip>:990/" -o /dev/null   # curl: (67) Access denied: 530
```

`BambuFtpsClient` maps this to `PrinterAuthenticationException`, which tells the user where to find the
code. Printer test `Wrong_access_code_gives_a_clear_error` checks this once per run (one failed login),
and the headless render test shows the message in the real UI.

### Credential stores

| OS | Store | Verified |
| --- | --- | --- |
| macOS | Login keychain, generic password, service `Layerlapse`, account = serial | Round trip test passed with no access prompt (`LAYERLAPSE_CREDENTIAL_TESTS=1 dotnet test --filter Category=CredentialStore`) |
| Linux | Secret Service via libsecret, schema `app.layerlapse.PrinterAccessCode` | Round trip passed in `mcr.microsoft.com/dotnet/sdk:10.0` with `gnome-keyring-daemon` under `dbus-run-session`. With no daemon, libsecret reports "Cannot autolaunch D-Bus without X11 $DISPLAY"; the app then connects and warns that the code will not be remembered |
| Windows | Credential Manager, generic credential `Layerlapse:printer:<serial>` | Compiles only; not run |

### Milestone 2 acceptance: restart connects with no typing. **Yes (macOS).**

The owner entered the IP and code once in `artifacts/macos/Layerlapse.app` (built by
`scripts/make-macos-bundle.sh`) and quit. Afterwards `printers.json` held host, serial, pin and
time only (the code appears 0 times), and `security find-generic-password -s Layerlapse` showed one item
whose account is the serial. Relaunching the same build showed "Connected" with the serial, with no input
and no Keychain prompt; `lastConnected` was updated by the new login. The pinned fingerprint matches the
certificate from milestone 1 (`145C6BBE…`). A wrong code shows "The printer rejected the access code…"
(see above).

macOS caveat: keychain items created by an ad-hoc signed development build trust that exact binary.
After a rebuild, macOS may ask "Layerlapse wants to use your confidential information" once; choose
Always Allow. Signed release builds (milestone 7) will not have this.

## 2026-10-07: milestone 3 (discovery)

### Broadcast, not multicast

The announcements go to **255.255.255.255:2021** (a limited broadcast), so listening needs only a UDP bind
on port 2021 with broadcast enabled, and no multicast group membership:

```bash
python3 dst.py   # bind ("", 2021) with IP_RECVDSTADDR, no IP_ADD_MEMBERSHIP -> destination: 255.255.255.255
```

The listener sets `ReuseAddress` so it can share the port with other listeners. Find printers only listens;
the port 990 scan runs only when the user clicks "Scan the network" after nothing announced itself, so the
app never probes every address on a network (for example a VPN) without being asked. Whether it can run while
Bambu Studio is open was **not tested** (Studio was not running).

### Discovery results against the real printer

| Method | Result | Test |
| --- | --- | --- |
| Announcement (UDP 2021) | Found in under 10 s: model code `BL-P001` → X1 Carbon, firmware 01.12.00.00, serial equal to the one from logging in | `Announcement_matches_the_logged_in_printer` |
| Certificate probe on port 990 (TLS handshake only, no FTP command, no login) | Serial read from the certificate CN, issuer `BBL CA`, model X1 Carbon from the serial prefix | `Port_probe_reads_the_serial_without_logging_in` |
| Subnet scan | 506 addresses (two local /24 networks, one of them OrbStack's) in 4.9 s, exactly one printer | `Subnet_scan_finds_the_printer` |
| Real view | The headless render with real `PrinterDiscovery` shows the printer selected and its address filled in, with nothing typed | `Renders_real_discovery` |

### Live check on macOS (2026-10-07)

After rebuilding the bundle, the first launch asked for Keychain access as predicted for an ad-hoc signed
build; the owner chose Always Allow, and later launches of that build did not ask again. The owner did not
report a Local Network prompt. Without any input, the app reconnected and the background refresh filled
`printers.json` with `model: X1 Carbon`, `modelCode: BL-P001`, `name: X1 Carbon` and
`firmware: 01.12.00.00`, which the Connected screen shows.

### Model table

Only `BL-P001` and the serial prefix `00M` (both X1 Carbon) are verified. The other entries in
`PrinterModels` (X1, X1E, P1P, P1S, A1, A1 mini, H2D codes and prefixes) come from community reports.
Unknown codes or prefixes leave the model empty, and the app asks the user to pick it. The model is
never taken from the printer's name, because the name is user-editable.

## 2026-10-07: milestone 4 (timelapse browser)

### Is the filename the print start and the modification time the end? **Yes, on two different clocks.**

- **Listing and MDTM times are UTC.** `MDTM` and `LIST` agree (curl's `Last-Modified` for
  `video_2025-07-07_07-17-11.mp4` is `06:57:46 GMT`, the same as `MDTM` through `BambuFtpsClient`; printer
  test `Exact_modification_time_matches_curl`). `MDTM` gives seconds for every file, including old ones
  whose listing shows only a date.
- **Filenames use the printer's own clock**, which is UTC−5 on this printer, while the Mac is UTC−4. Camera
  segments show it: `ipcam-record.2026-10-04_19-37-22.2.mp4` (a 5-minute segment) was modified at
  `00:41 UTC` the next day, which is the filename time plus 5 h 04 min.
- **Since about December 2025, every timelapse was modified at least 5.15 h after its filename time**,
  consistent with a start on a UTC−5 clock and an end in UTC.
- **Before December 2025 the printer's clock or time zone was different**: many files were "modified" up
  to 2.8 h *before* their filename time. Durations for those files cannot be worked out.

```bash
# MDTM for every timelapse, then delta = MDTM - filename time
curl -sS -k -I -u "bblp:$LAYERLAPSE_CODE" "ftps://<printer-ip>:990/timelapse/<name>" | grep -i last-modified
```

**What the app does:** it measures the printer's offset from its camera recordings (the smallest
`modified − name`, rounded down to 15 minutes; 5 h here). When there are no recordings, it falls back to this
computer's time zone and says so. Duration = modified (UTC) − (filename time + offset). It is shown only
when it is between 0 and 48 h, and always marked "≈ … approximate". The start time is shown exactly as in the
filename. For the owner's printer, durations are known for 48 of 74 timelapses. A user setting for the
printer's time zone is a possible later addition.

### Speed (acceptance: list in a few seconds on the second launch)

74 timelapses, measured against the real printer (`TimelapseLibraryPrinterTests.Cold_and_warm_timings`):

| | Time |
| --- | --- |
| First launch: list plus MDTM for each file | 2.0 s |
| First launch: all 74 thumbnails (about 15 KB each, one connection) | 9.5 s more, filling in as they arrive |
| Second launch: grid shown from the cache | 11 ms |
| Second launch: background refresh (no MDTM needed for known files) | 1.4 s |

### Playback (acceptance: a video plays with seeking)

`video_2026-09-15_16-12-12.mp4` (5,424,735 bytes, 163 frames, 6.75 s) was fetched through the app's own
path (`TimelapseLibrary` → cache → `DefaultAppVideoPlayer`; `Layerlapse.Spike play`). It was byte-identical to
curl's copy (SHA-256 `CEF5DE73…`), decoded at 3.4 s with `ffmpeg -ss 3.4`, and opened in the default player
(IINA on this Mac). Seeking in the player itself was not exercised by Claude, which cannot click IINA.

Live check (2026-10-07): in the rebuilt app the owner clicked Play on a card, the video opened in IINA,
and seeking worked. Milestone 4's acceptance line holds on macOS.

The built-in player (LibVLCSharp) is not used yet: `LibVLCSharp.Avalonia` 3.10.1 depends on Avalonia
11.3.13 or later, and this app is on Avalonia 12, so it would need testing. Its native packages also add about
100 MB per platform. The plan's fallback, the OS default player, is used instead.

### FTP detail found on the way

After a `550` reply (file not found), the next transfer failed with "did not resume the TLS session". The
client had started the data connection's TLS handshake before reading the reply, and BouncyCastle
invalidates a session when a resumption attempt is abandoned. vsftpd answers `150` before it starts TLS on the
data connection, so the client now reads the reply first. Printer test
`Missing_file_is_an_ftp_reply_and_the_connection_survives` covers it.

## Still open

- What is inside a `.gcode.3mf`: milestone 6.
- Whether listing or downloading during an active print causes slowdown or disconnects: not tested. The
  printer appeared idle during these runs (its last camera recording was from 2026-10-04).
- Whether FluentFTP works on Windows: SChannel may resume TLS sessions there. Not tested, and moot
  unless we want two code paths.
