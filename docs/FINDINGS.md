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
dotnet run --project tools/Layerlapse.Spike -- --library fluentftp <out-dir>   # 522 on LIST
```

**What works instead:** `BambuFtpsClient` in Core, which uses BouncyCastle's managed TLS (pure C#, no
native code) plus a minimal read-only FTP layer (USER, PASS, PBSZ, PROT P, TYPE I, PASV, LIST, RETR, QUIT).
Each data connection resumes the control session, and the client checks that the resumption really happened.
It works on macOS and Linux:

```bash
dotnet run --project tools/Layerlapse.Spike -- --library bc <out-dir>
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
dotnet run --project tools/Layerlapse.Spike -- --library bc bc/
curl -sS -k -u "bblp:$LAYERLAPSE_CODE" "ftps://<printer-ip>:990/timelapse/video_2026-05-13_11-25-16.mp4" -o curl/video.mp4
shasum -a 256 bc/video_2026-05-13_11-25-16.mp4 curl/video.mp4
# cbcc010b3234e71502ce9870307d1ed42b02e23b1063552a84f540744b7a72c3 (both)
cmp bc/video_2026-05-13_11-25-16.mp4 curl/video.mp4     # no differences
ffprobe ...    # h264 1760x1080, 24 fps, 5 frames, 0.17 s
ffmpeg -v error -i bc/video_2026-05-13_11-25-16.mp4 -f null -   # decodes with no errors
```

The extracted frame shows the print bed and toolhead. The same hash came from the Linux container.
Timing on the LAN: connect plus login about 1.1 s, listing 75 entries about 80 ms, 184 KB download about 220 ms.

### Which exact model and firmware? **Partly answered.**

- The TLS certificate's subject CN is the printer serial (`00M…`, 15 characters). It is issued by
  `C=CN, O=BBL Technologies Co., Ltd, CN=BBL CA` and valid from 2025-03-12 to 2035-03-10. Community serial
  tables map the `00M` prefix to the **X1 Carbon**. That is a hint, not proof: confirm it on the printer
  screen or from the discovery announcement in milestone 3.
- The FTP greeting is just `220 (vsFTPd 3.0.5)`. **It does not contain a model token**, so the
  "model from FTP greeting" fallback in the plan does not work on this firmware.
- Firmware version: not visible over FTPS. Still open.

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

## Still open

- Exact model (confirm the X1 Carbon hint) and firmware version: milestone 3, or the printer screen.
- UDP multicast announcement contents: milestone 3.
- What is inside a `.gcode.3mf`: milestone 6.
- Whether listing or downloading during an active print causes slowdown or disconnects: not tested. The
  printer's state during these runs is unknown.
- Whether FluentFTP works on Windows: SChannel may resume TLS sessions there. Not tested, and moot
  unless we want two code paths.
