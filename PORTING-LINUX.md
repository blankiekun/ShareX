# ShareX Linux port: what needs porting

Target: KDE Plasma 6 on Wayland. Inventory taken 2026-11-03 at `8ed375c7b`, on Plasma 6.8.1, .NET SDK 10.0.113, Avalonia 12.1.4.

**Where it stands:** the UI is already Avalonia, so this is not a UI rewrite. A temporary patched build reached the full
main window on this machine, using four packages, two stubbed WinRT classes and a null hotkey host (patches reverted).
Nothing else works yet: capture, recording, OCR and global hotkeys all need real work. The hard part is at runtime, and
most of it won't show up as compile errors.

## Labels

| Label | Meaning | What to do |
|---|---|---|
| `READY` | No OS coupling found, or run and confirmed | Keep. Still blocked by the build work in section 2 |
| `UNVERIFIED` | Should work, has not been run | Run it, then mark `READY` or relabel |
| `SWAP` | Same feature, different API | Replace the API |
| `REWRITE` | Must be re-implemented | Build it again for Linux |
| `LIMITED` | Wayland can't fully match Windows; the row says what is lost | Port what's possible, and accept or redesign the rest |
| `DROP` | Doesn't apply on Linux | Exclude or delete |

User-facing features in the checklist (section 5.1), by label:

| Label | Rows |
|---|---|
| `READY` | 43 |
| `UNVERIFIED` | 97 |
| `SWAP` | 0 |
| `REWRITE` | 0 |
| `LIMITED` | 35 |
| `DROP` | 8 |
| **Total** | **183** |

The other tables overlap with the checklist, so their rows aren't added to these totals.

To recount, where a `LIMITED` cell also says what is lost: `awk -F'|' '/^### 5\.1 /{on=1} /^### 5\.2 /{on=0} on && /^\|/ && $5 !~ /Label|---/ {split($5,a,":"); gsub(/ /,"",a[1]); n[a[1]]++} END {for (l in n) print l, n[l]}' PORTING-LINUX.md`

## 1. Facts that shape the port

- **ShareX runs through XWayland by default.** Both apps call `UsePlatformDetect`, and `Avalonia.Desktop` 12.1.4 depends
  on `Avalonia.X11`. The build ran with no `libwayland` loaded. A separate `Avalonia.Wayland` package exists and is described
  as experimental. Reading its source, window move, always-on-top and transparency are no-ops, so XWayland is the practical
  start. Keep the window code isolated in case native Wayland matures.
- **An XWayland app can't see the real screen or the global cursor**, so capture must go through KWin or the portals.
- **Window handles are a trap.** Under X11 the handle is a non-zero XID, so `handle != IntPtr.Zero` guards pass and the
  code reaches `user32`, which should throw `DllNotFoundException`. Windows version checks had a similar problem: fixed in M2,
  every `Helpers.IsWindows*` is now false off Windows.
- **The default hotkeys clashed with KDE.** Print and Shift+Print belong to Spectacle on this machine. M5 moved those two defaults; a saved hotkey on either shows as failed.

## 2. Fix these first

| # | Item | Where | Why |
|---|---|---|---|
| 1 | Done in M2: secrets could be written in plaintext | `ShareX.HelpersLib/Settings/EncryptedStringValueProvider.cs`, `ShareX.HelpersLib/Cryptographic/SecretServiceCrypto.cs` | `$ShareXEncrypted$` + AES-GCM under one master key in the Secret Service. If a value can't be encrypted the save aborts and the file on disk stays as it was. All three config files now encrypt, including AI API keys |
| 2 | Done in M1: props files weren't imported on Linux | `Directory.Build.props`, `Directory.Build.targets` | MSBuild looks for `Directory.Build.*` and Linux is case-sensitive, so the build got version 1.0.0. Renamed, with the RID set to `linux-x64` |
| 3 | Done in M1: resource path casing | `ShareX.UploadersLib/Properties/Resources.resx:17` | Pointed to `..\resources\oauthcallbackpage.html`; the file is `Resources/OAuthCallbackPage.html` |
| 4 | Done in M1: Windows-only targets | All projects now target `net10.0`; `Microsoft.WindowsDesktop.App` removed from `ShareX.HelpersLib.csproj` | Was `NETSDK1100` on Linux |
| 5 | Done in M1: hotkey host crashed start-up | `ShareX/Forms/MainForm.cs:47` | M5 replaced the host that registered nothing with `KGlobalAccelHotkeyHost` |
| 6 | Done in M2: portable mode collided with the binary | `ShareX/Infrastructure/AppPaths.cs:42` | The folder is now `ShareXData`. `-portable` starts, with no "Avalonia is already initialized" crash |

## 3. Suggested order

1. **Build:** items 2 to 5 above, plus stubs for `OCRHelper.cs`, `GraphicsCapture.cs` and the WinRT part of `GraphicsDevice.cs`.
   Remove `ShareX.Steam` and `ShareX.Setup`. Add the 4 compile-time packages
   (`System.Drawing.Common`, `System.Security.Cryptography.ProtectedData`, `System.Windows.Extensions`, `System.Management`).
   Done in M1: both builds give 0 errors and an ELF apphost, and the main window opens with the title "ShareX 22.1 Dev".
2. **Safety:** done in M2: the plaintext secrets fix, the version and handle guards, the portable folder.
3. **Cheap shared fixes:** the `.exe` check in `FFmpegCLIManager.cs:89`, folder opening in `FileHelpers.cs:294,337`, path
   and separator handling. Together these unlock about a dozen tools. Delete dead code too: 77 of the 159 `NativeMethods.cs`
   P/Invokes have no callers. Done in M3: `.exe` checks, folder opening, path handling and 66 uncalled P/Invokes. The other 92
   remain, and some are reached only through uncalled wrappers in `NativeMethods_Helpers.cs`.
4. **Capture:** screenshots through KWin `ScreenShot2` or `spectacle`, one overlay window per screen over a frozen
   screenshot, and a KWin script for window lists and snapping. This is the core of the app and the biggest design risk.
5. **Desktop integration:** KGlobalAccel hotkeys, tray, single instance, `.desktop` file, icons, MIME types, autostart,
   service menu, then an AppImage.
6. **Recording:** M6 drives the host's `gpu-screen-recorder` as a process; nothing has run on real captures yet. Window recording records the window's rectangle, so it stays LIMITED; following the window itself needs the portal later.
7. **Remaining tools:** Tesseract OCR, ONNX on the CPU, `libmpv` video player, printing, notifications.
8. **Migration from Windows:** settings import, path rewriting, history. In scope: the owner has an install to move.

## 4. Design notes and open decisions

**Region overlay.** Built in M4 (route A: `spectacle` plus a KWin script, owner decision): one window per output, each showing a
crop of the frozen screenshot, sharing a selection in image pixels. Run in a virtual session only. Layer-shell was not needed, but `Topmost` alone was not enough: KWin moved the plain window out from under a top panel's reserved strip, so the live panel stayed visible with the frozen one just below it (the owner's real run showed this first). Each window is now fullscreen, which held on both outputs in the virtual session, with focus on either one. The fix has not been seen on a real desktop yet. Window
snapping uses a KWin script that reads `workspace.stackingOrder` and geometry before the overlay appears.

Three coordinate spaces (`DesktopCoordinates`), measured in the M4 spike and checked again through ShareX's menu:

| Space | Value | Used by |
|---|---|---|
| KWin logical | As KWin reports it | KWin scripts, window data |
| X11 | logical × largest output scale, or logical × 1 with `[KScreen] XwaylandClientsScale=false` in `kdeglobals` | ShareX's own windows, Avalonia `Screens`, `DesktopScreen`, public rectangles |
| Image | (logical − origin) × pixels per logical, measured from the image | The frozen screenshot and the selection |

ScreenShot2's `CaptureWorkspace` in mixed scale is logical × the largest scale on every monitor, so a low-DPI monitor is upscaled and can't be
captured natively that way (`CaptureActiveScreen` is native). ShareX crops it as it is, so a selection on that monitor comes out upscaled.
`CaptureScreen` per monitor could fix this later. `spectacle -f`, now only the fallback, rounds that scale up to a whole number: 2 with scales 1 and 1.5. `xdotool` positions reach KWin as logical at every scale, unlike what an X11 client reads back.

**Hotkeys.** Use KGlobalAccel on KDE: it supports press-any-key capture and per-key register and unregister. The
GlobalShortcuts portal can only hint a trigger and has the user confirm in a dialog; it's the fallback for Flatpak. Don't
assume a requested key became active.

What M5 found on KWin and KGlobalAccel 6.8, all in a virtual session:
- **The read-back can't detect a clash.** `setShortcutKeys` stores and returns the requested key even when another action holds it; both then list the key, and the first one goes on firing. So `KGlobalAccelHotkeyHost` asks `globalShortcutsByKey` first, takes nothing that's held, then checks the read-back and that it's the only holder.
- **Action names last one run.** Each start removes the component's actions and registers from ShareX's settings, so a rebind in System Settings doesn't survive a restart. While running, the rebind turns the hotkey Failed, and opening ShareX's hotkey settings puts ShareX's key back.
- **On exit, normal or SIGTERM, actions go inactive.** They stay in `kglobalshortcutsrc` and hold their keys against other programs, but don't fire. After SIGKILL they stay active, so the key is taken and fires at nothing until the next ShareX start.
- **Shifted digits and punctuation register as the typed character.** KWin fired `Ctrl+!` for Ctrl+Shift+1 and never `Ctrl+Shift+1`. The table assumes a US layout, so on another layout such a hotkey shows as registered and doesn't fire.
- **`invokeShortcut` ignores inactive state,** so it can't show that an inactive action is quiet; use a key press. `xdotool` through XWayland with `XwaylandEisNoPrompt` does reach KWin's global shortcuts and is what the tests used. It doesn't pass through the kernel, so a physical press is still the owner's check, Alt+Print above all: the kernel handles Alt+SysRq itself.
- **`--no-global-shortcuts` leaves kglobalaccel on the bus.** Registering works in such a session but keys don't fire.

**Recording.** Owner decision (2026-11-03): build on `gpu-screen-recorder` (gsr) as a host dependency, accepting its `cap_sys_admin` helper and capture with no consent prompt. `ScreenRecorder` keeps its public surface and spawns gsr with an argument list, clearing `LD_LIBRARY_PATH`. It checks the helper with `getcap` first and treats `no drm found` or `drmfb handle is NULL` on stderr as failure even with exit 0, because that case is a valid all-black MP4. It controls gsr through `-ipc` and uses the socket plus `-write-first-frame-ts` as the started signal. gsr writes to a hidden temporary name, which then moves over the empty `CreateNew` reservation without clobbering anything else.
- **Spanning regions are refused, not clipped.** gsr gives wrong pixels for them, and clipping would silently drop part of the selection. The refusal explains why before anything starts. Monitor bounds come from `kscreen-doctor -j`.
- **Two audio tracks when both sources are on.** A silent microphone can only be detected on its own track (ffprobe bytes per packet, logged through `Diagnostic`). The cost: players that only play the first track won't play the microphone, where the Windows mixer made one track.
- **GIF:** `RecordNativeAsync` records a constant-frame-rate MP4, then calls `FFmpegEncodeAsGIF` with a crop, because gsr rounds odd sizes up to even. The crop runs in RGB: on 4:2:0 video `crop` silently rounds odd sizes and offsets down (run). `GifWriter` is deleted, so GIF quality now starts from 4:2:0 H.264 (colored text edges are the likely loss, not viewed yet).
- **Frame rate:** MP4 stays variable, because the man page warns `-fm cfr` can collapse to 1 to 2 fps under heavy GPU load.

**Video playback.** Owner decision (2026-11-04): the host's `libmpv.so.2` (package `mpv`) by direct P/Invoke, like `ffmpeg` and gsr. `MpvPlayer` hosts a child X11 window through `NativeControlHost` and passes it as `wid`, so mpv draws straight into it (`vo=gpu`, then `vo=x11`; `gpu-context=x11egl`, then GLX, forced because `WAYLAND_DISPLAY` would otherwise pick a Wayland context). In the virtual session EGL drew; GLX drew with EGL hidden, and `vo=x11` when forced once in a temporary build. The log line `[MpvPlayer] vo=...` says which one ran. This is about 600 lines and no GL: the render API would need Avalonia's OpenGL control, a render context with its own teardown order on the GL thread, and mpv drawing on the UI thread. It also keeps the UI as it was, a native popup over a native child. The cost is that it needs XWayland, which all of ShareX already does.
- **Input:** mpv takes no input in its window (`input-cursor=no`), so X11 passes clicks and motion up to Avalonia. Keyboard focus never leaves Avalonia.
- **Lifetime:** one handle per loaded file. A thread reads events and is the only place `mpv_terminate_destroy` runs, after the UI thread's last call. Closing waits for it, so mpv is gone before its parent window.
- **X11 errors:** when a video output closes, mpv calls `XSetErrorHandler(NULL)` (0.41, seen in the disassembly). That restores Xlib's default, which exits the process, and the handler is global. `MpvPlayer` puts Avalonia's back after each close. Tested both ways: destroying the holder window behind Avalonia after a close ended ShareX with exit code 1 and "X Error of failed request" without the fix, and did nothing with it. A gap remains while a failed output probe waits for the next one (milliseconds); an X error from Avalonia in exactly that gap would still exit.
- **Off:** `config=no`, `load-scripts=no`, `ytdl=no`, `terminal=no`, the built-in scripts, and hardware decoding. libmpv's default of software decoding is kept on purpose: that is what ran, and a driver crash here would take ShareX with it. `hwdec=auto-safe` is the one-line change if large files turn out too slow.
- **Leaks, measured:** 16 open and close cycles: file descriptors flat at 257, threads back to about 20, sockets flat. RSS rose from 469 to 581 MiB, under 2 MiB per cycle over the last 6; a tool window without mpv also grows at first. A 5-cycle run closing during playback: 496, 524, 550, 581, 591 MiB.

**Where reports disagreed:** subsystem tables win over the checklist in 5.1, whose rows are best guesses. Specifically:
wallpaper is read-only, so nothing sets a wallpaper (the checklist's "Plasma wallpaper API" means reading it). OCR is a `SWAP`
to Tesseract. Foreign-window tools are `LIMITED` through KWin scripts, not `DROP`. One item stays open: updates (`DROP` if packages handle them, `REWRITE` otherwise).

**Owner decisions (2026-11-03):**
- Windows is dropped: retarget to `net10.0` and delete Windows code as it gets replaced. Any `multi-target` wording below is superseded.
- Secrets: Secret Service (`org.freedesktop.secrets`, served here by `ksecretd`). Fail closed when it is unavailable.
- Packaging: AppImage, so no sandbox and KGlobalAccel stays. PKGBUILD and Flatpak rows below are not planned.
- Migrating an existing Windows install is in scope. DPAPI blobs can't be decrypted on Linux and don't cross: import and load clear them, and import names the destinations to sign in to again (M8). The route that keeps logins is a `.sxb` exported on Windows, which holds them in plain text.

**Still open:** how to sync from upstream. The fork has no upstream remote, and dropping Windows makes merges harder.

**Not testable on the current PC:** HDR (off on both outputs), mixed DPI (both at scale 1), webcam (no `/dev/video*`),
real printing (CUPS inactive). `~/.local/share/fonts/ms-fonts` makes Segoe and Consolas resolve here, so hide it when testing font
and emoji fallbacks.

**How far to trust this.** Only the build, editor launch and main window were really run, along with some host checks such as NVENC
encoding and Tesseract. No real capture, portal session, KWin script or global shortcut was exercised, so every proposed replacement
is a design choice until someone runs it. Lead-checked against the code or package metadata: items 1, 3, 5 and 6
above, the Avalonia dependency, `IsWindows7OrGreater`, and the backup import skipping `History.db`. The rest are agent findings: treat them as leads.

## 5. Inventory

File:line references were accurate at the commit above and will drift.

### 5.1 Feature checklist, uploaders, history

Paths: `TH` = `ShareX/TaskHelpers.cs`, `UM` = `ShareX/UploadManager.cs`, `WT` = `ShareX/WorkerTask.cs`. (V) = seen on this host, never exercised; (A) = assumed. "Owner capture" and similar mean the details are in that subsystem's table below.


#### UploadersLib and HistoryLib

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| Secrets | ShareX.HelpersLib/Settings/EncryptedStringValueProvider.cs; ShareX.HelpersLib/Cryptographic/SecretServiceCrypto.cs | Replaced in M2. A failed save aborts; a failed load keeps the raw tagged value and logs it. `$DPAPIEncrypted$` values load as empty and the log names the property (M8). Encrypted: every `[JsonEncrypt]` property, which M8 extended to the proxy password, the ImageShack token and the Zero Width Shortener token after a scan of an imported config found them in the clear. Not encrypted, on Windows or here: custom uploader headers and arguments, which are dictionaries the attribute can't reach, and OAuth 1 `ConsumerKey` and `AuthToken`, which aren't secrets (one identifies the app, the other goes through the browser). OAuth 1 `ConsumerSecret`, `ConsumerPrivateKey` and `UserToken` were added here, not on Windows: plaintext values load as they are and are encrypted on the next save, and until then a locked wallet makes that save fail with the file left alone. So `SettingsBase` writes the config files, their `.temp` and their backups as 0600 and makes `Backup/` 0700, which also closes off older weekly copies; a file from before this tightens on its next save. `History.db`, its `-wal`, `-shm` and `-journal` files, logs, the network monitor log and the history backups are 0600 and existing ones tighten when opened; `Logs/` is 0700, which covers older months' logs too. Checked with `stat` in a throwaway home, with the real app and the real import. The personal folder isn't restricted because `Screenshots` is inside it by default | READY | Secret Service. Run with a real `ksecretd` and a throwaway wallet in a virtual session; not run against the owner's wallet or with a person answering a wallet prompt | M | High |
| OAuth, browser | ShareX.UploadersLib/OAuth/OAuthListener.cs:70,84; ShareX.HelpersLib/Helpers/URLHelpers.cs `OpenURL` | Managed HttpListener on loopback. A `BrowserPath` that can't start falls back to the default browser, see OpenURL | UNVERIFIED | xdg-open. In a virtual session Google Drive's Connect account sent the authorization URL to a logging stand-in for xdg-open, with a `127.0.0.1` redirect, and the listener answered a wrong `state` with its callback page and exchanged nothing. No real browser, sign-in or token | S | Med |
| FTP/FTPS, SFTP | ShareX.UploadersLib/FileUploaders/FTP.cs:143,148; ShareX.UploadersLib/FileUploaders/SFTP.cs:213 | Packages have portable TFMs (V); Windows key paths; FTPS accepts invalid certificates on every OS | UNVERIFIED | User re-selects files | S | Med |
| Shared Folder | ShareX.UploadersLib/FileUploaders/LocalhostAccount.cs:75,190 | UNC root becomes a literal relative filename | LIMITED: UNC paths lost | mount.cifs (V), kio-fuse (A) | S | High |
| `.sxcu`, `.sxie` association | `IntegrationHelpers.Install` | Done in M5 by `ShareX -install`; no longer registered at every start | UNVERIFIED | MIME package and one hidden `.desktop` per type (virtual session only) | S | High |
| Microsoft.Data.Sqlite | ShareX.HistoryLib/Managers/HistoryManagerSQLite.cs:48 | Forced RID only; linux-x64 `libe_sqlite3.so` cached (V) | UNVERIFIED | Bundled native | S | Med |
| HistoryLib System.Drawing | ShareX.HistoryLib/Models/ImageHistorySettings.cs:27; ShareX.HistoryLib/Models/ImageHistoryModels.cs:40 | Primitives only; `DrawingBitmap` aliases SKBitmap | READY | None | S | High |
| History thumbnails | ShareX.HistoryLib/Models/ImageHistoryModels.cs; ShareX.HistoryLib/Views/HistoryWindow.axaml.cs; ShareX.HelpersLib/Media/VideoThumbnailHelper.cs | Shell COM call deleted in M7 | UNVERIFIED | Skia and Avalonia decode for images. Videos: one frame from the host's `ffmpeg`, at most 2 at a time across the app, 15 s limit, cancelled when the item leaves the view. Image History's grid and the History window's preview ran again in a virtual session with a synthetic clip, at most 2 `ffmpeg` at once. Without `ffmpeg`, or for a damaged file, the placeholder stays | S | High |
| Serialized paths | ShareX.HelpersLib/Settings/BackupMigration.cs; ShareX.HelpersLib/Helpers/FileHelpers.cs `ExpandFolderVariables`; ShareX/Infrastructure/AppPaths.cs `ScreenshotsParentFolder` | Done in M8 for an imported backup: a synthetic `.sxb` had its `C:\` screenshots folder mapped into Documents, its browser path reset, and a capture landed in the mapped folder (virtual session). `%MyDocuments%\ShareX\x` expands with `/` separators and a capture landed there. A settings folder copied by hand has no import: a `C:\` screenshots folder used to make a folder named `C:\...` next to the program, and now falls back to the default folder, logged only. Other Windows paths in such a folder aren't rewritten | UNVERIFIED | Owner's real export is the check | M | High |
| Open folder | ShareX.HelpersLib/Helpers/FileHelpers.cs:294-297 | Appends `\` to the path. M3: `FileManager1.ShowFolders` and the `xdg-open` fallback ran in a virtual KWin session | READY | FileManager1 (V) | S | High |
| History file paths | ShareX.HistoryLib/Views/ImageHistoryWindow.axaml.cs:298; ShareX.HistoryLib/Views/HistoryWindow.axaml.cs:999 | Stored `C:\` paths stay as written (owner decision, M8: a record, not a setting). Every open action checks `File.Exists` | LIMITED: old local files can't be opened; the URLs still work | None | S | High |
| Settings migration | ShareX/SettingManager.cs `Import`; ShareX.HelpersLib/Settings/BackupMigration.cs; ShareX/BackupImportReport.cs | M8. Blobs cleared and named by destination, plaintext secrets saved encrypted and never written to disk in the clear, Windows paths mapped or reset and listed, programs chosen by the backup turned off, unknown `$type`s dropped, a report dialog at the end. Fails closed when the backup holds secrets and the wallet is unavailable. A settings folder copied by hand instead: blobs clear on load, `FileHelpers.CreateDirectory` refuses Windows paths and a Windows screenshots folder falls back to the default one, but paths aren't rewritten and nothing is reported. Run on a synthetic `.sxb` only, never a real Windows export | UNVERIFIED | Owner's real export is the check | M | High |


#### Capture types (8)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| Fullscreen | ShareX/CaptureHelpers/CaptureFullscreen.cs:35 | Done in M4 | READY | KWin ScreenShot2 `CaptureWorkspace`, `spectacle -f` as the fallback; see ScreenShot2 in 5.2. ShareX's own windows stay in the image. Pixels on the outputs match `spectacle -f` at scale 1 and 2 (virtual session). The owner's check on the real desktop, 2026-11-03, was on spectacle | M | Med | M | Med |
| Monitor | ShareX/Presentation/MainWindow/MainMenuBuilder.cs:185 | Done in M4 | UNVERIFIED | All monitors, cropped, as for Fullscreen. ScreenShot2 could name a monitor; not used yet (virtual session only) | M | Med |
| ActiveMonitor | ShareX/CaptureHelpers/CaptureActiveMonitor.cs:35 | Done in M4; new item in the Capture menu | READY | ScreenShot2 `CaptureActiveScreen`, the call `spectacle -m` makes, with `-m` as the fallback. It follows the active window's output, not the cursor (M4; owner confirmed on the real desktop, 2026-11-03, on spectacle). From a hotkey that is the monitor in use. ShareX's own menu first activates the window last used that isn't ShareX's, so it is that window's monitor, or ShareX's when there is no other window (virtual session only; the owner has not run the menu route) | M | Med |
| Window | ShareX/CaptureHelpers/CaptureWindow.cs:47; ShareX/Presentation/MainWindow/MainMenuBuilder.cs:156 | Done in M4; no activation since ScreenShot2 | LIMITED: minimized windows aren't listed, and entries have no icon. The window isn't activated or raised, and comes out whole when covered: KWin saw no activation in 20 captures of a covered window, and none from the main window's menu, which no longer hides ShareX first. Only the spectacle fallback still activates it | KWin script list, ScreenShot2 `CaptureWindow`, `spectacle -a` as the fallback. 2 to 3 ms a shot against 270 to 340 ms on spectacle (virtual session only) | L | Med |
| ActiveWindow | ShareX/CaptureHelpers/CaptureActiveWindow.cs:33 | Done in M4; new item in the Capture menu | UNVERIFIED | ScreenShot2 `CaptureActiveWindow`, with the options `spectacle -a`, `-a -S` or `-a -e -S` ask for by setting, and those as the fallback; same pixels in all three. A hotkey captures the window in use, since nothing of ShareX takes focus first. ShareX's own menu first activates the window last used that isn't ShareX's, read from KWin's stack as scripts can't read the focus chain; hiding ShareX alone stayed on ShareX's monitor. A minimized window is skipped, a kept-above one only counts if there is nothing else, and with no other window it says so and captures nothing (virtual session only) | M | Med |
| Region | ShareX/CaptureHelpers/CaptureRegion.cs:46 | Done in M4 | LIMITED: annotations are per monitor; cursor is drawn into the frozen image | One overlay per output over a frozen all-monitors image, as for Fullscreen. Owner confirmed on two real monitors, 2026-11-04, on spectacle; the active monitor stays undimmed, which is intended; see 5.4. From the main window's menu it waits 530 ms first, so KWin's fade of the closed menu isn't captured: spectacle's start used to cover part of that, and 400 and 470 now left a faint menu (virtual session, default animation speed; a slower Plasma animation setting would need longer, and hotkeys don't wait) | L | Med |
| CustomRegion | TH:136 | Done in M4: same code as LastRegion, not run itself | UNVERIFIED | `spectacle -f`, cropped | S | Med |
| LastRegion | TH:142 | Done in M4 | UNVERIFIED | `spectacle -f`, cropped (M4, virtual session only) | S | Med |


#### HotkeyType: Upload (9)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| FileUpload | TH:88; UM:48 | None in path | UNVERIFIED | Avalonia picker, portal FileChooser (V) | S | Med |
| FolderUpload | TH:98; UM:108 | None in path | UNVERIFIED | Same as FileUpload | S | Med |
| ClipboardUpload | TH:101; UM:187 | Owner clipboard: Avalonia clipboard | UNVERIFIED | wl-copy, wl-paste as fallback (V) | S | Med |
| ClipboardUploadWithContentViewer | TH:104; UM:237 | Owner clipboard | UNVERIFIED | Same as ClipboardUpload | S | Med |
| UploadText | TH:107; UM:259 | None found | READY | None | S | Med |
| UploadURL | TH:110; UM:289 | Clipboard prefill only | UNVERIFIED | None | S | Med |
| DragDropUpload | TH:113; ShareX/Presentation/DragDropUpload/DragDropUploadWindow.axaml.cs:107 | Sets Position, Topmost | UNVERIFIED | X11 backend on XWayland (A) | S | Low |
| ShortenURL | TH:116; UM:310 | None found | READY | None | S | Med |
| StopUploads | TH:119; ShareX/TaskManager.cs:119 | None found | READY | None | S | High |


#### HotkeyType: Screen capture (11)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| PrintScreen | TH:123 | Done in M4 and M5 | UNVERIFIED | As Fullscreen. Pressed as a real key through KWin's global shortcuts (`xdotool`, Ctrl+Meta+key on a private bus, virtual session with two outputs): the whole 2560x720 desktop saved. The owner's real-desktop hotkey check covered Meta+` and the hotkey route in general, not this job | M | Med |
| ActiveWindow | TH:126 | Done in M4 and M5 | UNVERIFIED | As ActiveWindow type. Pressed as a real key through KWin's global shortcuts (`xdotool`, Ctrl+Meta+key on a private bus, virtual session with two outputs): saved the window in use, with ShareX's main window on the other output. The owner's real-desktop hotkey check covered Meta+` and the hotkey route in general, not this job, and its default Alt+Print still needs a physical press, as the kernel handles Alt+SysRq itself | M | Med |
| CustomWindow | TH:138; ShareX/CaptureHelpers/CaptureCustomWindow.cs:43 | Done in M4: title search over the KWin script list | LIMITED: as Window | KWin script, ScreenShot2 `CaptureWindow`. Run from its command with a covered window: saved that window, activated nothing (virtual session only) | L | Med |
| ActiveMonitor | TH:129 | Done in M4 and M5 | UNVERIFIED | As ActiveMonitor type. Pressed as a real key through KWin's global shortcuts (`xdotool`, Ctrl+Meta+key on a private bus, virtual session with two outputs): saved the output of the window in use, not ShareX's. The owner's real-desktop hotkey check covered Meta+` and the hotkey route in general, not this job | M | Med |
| RectangleRegion | TH:132 | Owner capture | LIMITED: as Region | As Region | L | Med |
| CustomRegion | TH:135 | Done in M4 and M5 | UNVERIFIED | As CustomRegion type. Pressed as a real key through KWin's global shortcuts (`xdotool`, Ctrl+Meta+key on a private bus, virtual session with two outputs): saved the rectangle from the settings, at its size and over the window expected. The rectangle loads from the `x, y, w, h` text a config holds. The owner's real-desktop hotkey check covered Meta+` and the hotkey route in general, not this job | S | Med |
| LastRegion | TH:141 | Done in M4 and M5 | UNVERIFIED | As LastRegion type. Pressed as a real key through KWin's global shortcuts (`xdotool`, Ctrl+Meta+key on a private bus, virtual session with two outputs): after a region capture it opened no overlay and saved the same area again. The owner's real-desktop hotkey check covered Meta+` and the hotkey route in general, not this job | S | Med |
| ScrollingCapture | TH:144; ShareX.ScreenCaptureLib/ScrollingCaptureManager.cs:1 | Owner capture: scroll injection, window access | LIMITED: consent required; target-window automation weaker | Portal RemoteDesktop (V) | L | Low |
| AutoCapture | TH:147 | Owner capture | LIMITED: 150 ms or more per shot only on the spectacle fallback | ScreenShot2 `CaptureWorkspace`, cropped, on the UI thread between ticks, as before. See StartAutoCapture for the timer; the window's own controls not run | M | Med |
| StartAutoCapture | TH:150 | Owner capture | LIMITED: as AutoCapture | As AutoCapture. Pressed as a real key through KWin's global shortcuts and stopped the same way, every 250 ms on a 300x200 rectangle: 33 and 34 files in 10 s, against 17 on spectacle. A rectangle takes 15 to 20 ms on two 1280x720 outputs and about 85 ms on two 4K ones, against 200 to 260 and about 500. The crop no longer leaves about 18 MB per shot to the finalizer (virtual session only) | M | Med |
| StopAutoCapture | TH:153 | Owner capture: timer | READY | None | S | Med |


#### HotkeyType: Screen record (11)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| ScreenRecorder | TH:157; `ShareX/ScreenRecordManager.cs:495` | Owner recording, now host `gpu-screen-recorder` | READY | Done in M6; owner confirmed a real recording with sound on the real desktop, 2026-11-04; see 5.5 | S | Med |
| ScreenRecorderActiveWindow | TH:160; ShareX/ScreenRecordManager.cs `GetActiveWindowRegion` | GetForegroundWindow deleted | LIMITED: records the window's rectangle, as gsr has no window capture on Wayland. What overlaps the window shows up, and a window that moves isn't followed. Across two monitors it's refused with a message; what hangs off the desktop is cut off | KWin's active window, or the last used one when ShareX has the focus, whose frame leaves out a drawn shadow, then `-w WxH+X+Y`. Virtual session, fake recorder only: exact arguments for odd sizes, each edge, maximized, full screen and a client-side decorated window, with and without the setup bar. Portal ScreenCast later would lift the limits (V) | L | Med |
| ScreenRecorderCustomRegion | TH:163 | Owner recording | UNVERIFIED | Done in M6: `-w WxH+X+Y`, must fit one monitor | S | Med |
| StartScreenRecorder | TH:166 | Owner recording | UNVERIFIED | As ScreenRecorder | S | Med |
| ScreenRecorderGIF | TH:169 | Owner recording, `FFmpegEncodeAsGIF` | READY | Done in M6: record MP4, then FFmpeg; owner confirmed on the real desktop, 2026-11-03 (right area, plays with motion) | S | Med |
| ScreenRecorderGIFActiveWindow | TH:172 | Owner recording | LIMITED: as active window | As ScreenRecorderActiveWindow. An odd-sized window records one pixel more and the GIF is cut back to it (ran on the fake recorder). Portal ScreenCast later (V) | L | Med |
| ScreenRecorderGIFCustomRegion | TH:175 | Owner recording | UNVERIFIED | As GIF recorder | S | Med |
| StartScreenRecorderGIF | TH:178 | Owner recording | UNVERIFIED | As GIF recorder | S | Med |
| StopScreenRecording | TH:181; TH:821 | Owner recording | READY | Done in M6: IPC `stop`, then SIGINT. Owner stopped a real recording from the bar on the real desktop, 2026-11-04. A stop during a GIF encode keeps the MP4 and discards the partial GIF; run through SIGTERM. The stop hotkey wasn't confirmed separately | S | Med |
| PauseScreenRecording | TH:184; TH:826 | Owner recording | UNVERIFIED | Done in M6: IPC `set-paused` | S | Med |
| AbortScreenRecording | TH:187; TH:831 | Owner recording | UNVERIFIED | Done in M6: stops gsr and discards the file | S | Med |


#### HotkeyType: Tools (43)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| ColorPicker | TH:191; TH:924 | Owner tools: dialog only | UNVERIFIED | None | S | Med |
| ScreenColorPicker | TH:194; ShareX.Avalonia/Windows/ScreenColorPickerWindow.axaml.cs | Done in M7; user32 and gdi32 deleted | LIMITED: colours come from a screenshot taken as the picker opens, so anything that changes while picking reads as it was | `spectacle` screenshot through a hook the app sets at startup, as ShareX.Avalonia can't reach the capture code. One window spans all monitors, kept above panels and moved back over their strips after mapping. Magnifier, hover preview and position kept. Picks matched the screen in a panel strip and on both outputs (M7, virtual session only); opening it from the colour dialog or the image editor not run; mixed scale not run | M | Med |
| Ruler | TH:197; ShareX.Tools/Tools/Ruler/ScreenPixelBuffer.cs:1 | Done in M7; BitBlt deleted | LIMITED: frozen image rather than live screen | `spectacle` screenshot taken before the window opens. One window spans all monitors, kept above panels and moved back over their strips after mapping, where KWin had placed it at (1280,30). A measurement that starts in a panel strip ran, on two outputs under real plasmashell top panels (M7, virtual session only); mixed scale not run | M | Med |
| MouseHighlighter | TH:200; ShareX.Tools/Tools/MouseHighlighter/MouseHighlighterManager.cs:60 | Done in M7. One long-lived KWin script and a small click-through overlay, only while the tool is on | LIMITED | Lost: click ripple, per-button colours, release crosshairs and fade, because Wayland shows no clicks without the `input` group. What is left is a circle that follows the pointer, with colour, radius and start with ShareX. Run in a virtual session only, see 5.6 | M | Med |
| PinToScreen | TH:203; TH:2028 | Owner tools: overlay window | LIMITED: stacking depends on the compositor; no click-through | XWayland `Topmost` holds above other windows and panels; drag, right click and placement by options ran. A pin from a region is placed by a KWin script, because KWin keeps a window that moves itself inside the work area, which shifted the image by up to the panel's height. Lost: arrow keys and zoom can't hold it in a panel's strip, as KWin pushes it out on those requests. Click-through not attempted. Run with a real plasmashell top panel (M7, virtual session only); mixed scale not run | M | Med |
| PinToScreenFromScreen | TH:213; TH:2097 | Owner tools plus capture | LIMITED: as PinToScreen and Region | As above; a region starting in a panel strip stays where it was captured, including after losing focus | M | Med |
| PinToScreenFromClipboard | TH:216; TH:2108 | Owner tools plus clipboard | LIMITED: as PinToScreen | As above; ran | S | Med |
| PinToScreenFromFile | TH:219; TH:2122 | Owner tools | LIMITED: as PinToScreen | As above; not run | S | Med |
| PinToScreenCloseAll | TH:229; TH:2132 | None found | READY | None | S | Med |
| ImageEditor | TH:232; ShareX.ImageEditor/Integration/LinuxDesktopWallpaperService.cs | Windows wallpaper reader deleted. Emoji and cursor renderers replaced in M7. Nothing in the editor sets a wallpaper, it only reads one as a background | UNVERIFIED | See Emoji, Cursor annotation and Wallpaper background in 5.10. Emoji and both kinds of cursor ran again in a virtual session | M | Med |
| PhotoEditor | TH:242; ShareX.PhotoEditor/Core/ImageIO.cs | M7: TIFF, HEIC and AVIF through host `magick` | UNVERIFIED | See 5.10 | M | Med |
| ImageBeautifier | TH:245; TH:1533 | Owner tools: Skia | UNVERIFIED | None | S | Med |
| ImageEffects | TH:255; TH:1589 | Owner editors: Skia | UNVERIFIED | None | S | Med |
| ImageViewer | TH:265; TH:1653 | Owner tools. Files with more than one frame now play and loop in the viewer (frame delays honoured, 10 ms or less shown as 100 ms); zoom survives frame changes. A double-click opens one viewer that stays open: the second click no longer closes it, and it can't open another. The cost is that a click in the first 500 ms after opening does nothing. Run in a virtual KWin session only, not yet seen by the owner | UNVERIFIED | None | S | Low |
| BackgroundRemover | TH:275; ShareX.Tools/Tools/BackgroundRemover/BackgroundRemovalService.cs | Done: DirectML and DXGI removed, CPU only, no device choice | UNVERIFIED | Bundled `Microsoft.ML.OnnxRuntime` 1.23.2, linux-x64 only (22 MB). The user puts any `.onnx` model in `Documents/ShareX/Models`; nothing is downloaded, and the window says so while there is none. Virtual session with tiny hand-written models: the mask reaches the saved file's alpha; missing, damaged and wrong-shape models give a message and no crash. A real background removal model's speed and memory haven't been measured | M | Med |
| ImageComparer | TH:278; TH:1089 | None found | READY | None | S | Med |
| IconConverter | TH:281; TH:1094 | None found | READY | None | S | Med |
| ImageCombiner | TH:284; TH:1038 | None found | READY | None | S | Med |
| ImageSplitter | TH:287; TH:1121 | None found | READY | None | S | Med |
| ImageResizer | TH:290; TH:1138 | None found | READY | None | S | Med |
| ImageConverter | TH:293; TH:1143 | None found | READY | None | S | Med |
| ImageWatermark | TH:296; TH:1148 | None found | READY | None | S | Med |
| ImageThumbnailer | TH:299; TH:1153 | None found | READY | None | S | Med |
| VideoConverter | TH:302; TH:1204; ShareX.HelpersLib/Media/FFmpegCLIManager.cs:89 | Owner tools: `.exe` guard makes Run return false. M3: guard removed; ran end to end in a virtual KWin session. GIF now makes its palette in a separate FFmpeg run (peak RSS 311 MB to 105 MB on 640x360, 200 frames); that path ran through the view model and real handler with host FFmpeg, not through the window | READY | /usr/bin/ffmpeg (V) | S | High |
| VideoTrimmer | TH:312; ShareX.Tools/Controls/Media/MpvPlayer.cs; ShareX.Tools/Tools/VideoTrimmer/VideoTrimmerWindow.axaml.cs:40 | Done in M7: host libmpv, filmstrip from FFmpeg; virtual session only, see 5.9 | UNVERIFIED | libmpv (A); ffmpeg (V) | L | High |
| AnimatedGifTrimmer | TH:315; TH:1131 | Owner tools: only grep hit is portable `ICommand` | UNVERIFIED | None | S | Med |
| VideoThumbnailer | TH:318; ShareX.Tools/Tools/VideoThumbnailer/VideoThumbnailer.cs:53 | Owner tools: `.exe` guard. M3: ran end to end in a virtual KWin session | READY | /usr/bin/ffmpeg (V) | S | High |
| AnalyzeImage | TH:321; TH:1872 | Owner tools: HTTP AI; `[JsonEncrypt]` options at ShareX.Tools/Tools/AnalyzeImage/AIOptions.cs:44 | UNVERIFIED | None | S | Med |
| OCR | TH:324; ShareX.Tools/Tools/OCR/OCRHelper.cs | Done in M7 | UNVERIFIED | Host `tesseract`, image on stdin, as an argument list. Languages come from `tesseract --list-langs`, shown as Tesseract's codes such as `eng`, without `osd`. A missing tool or language says which package to install (M7, virtual session with the real tesseract 5.5.3 and `eng` only; other languages not run) | M | High |
| QRCode | TH:334; TH:1730 | Owner tools: ZXing, Skia | UNVERIFIED | None needed. Generating from the clipboard and scanning an image file ran: zbarimg reads the generated code back as the same text. Save, copy and upload not run (M7, virtual session only) | S | Med |
| QRCodeDecodeFromScreen | TH:344; TH:1749 | Done in M4 and M7 | UNVERIFIED | `spectacle -f` through `Screenshot`. A known code in another window decodes. Off the UI thread since M7; a missing spectacle shows an error box. Scanned text only fills the text box and is never opened as a URL (M7, virtual session only) | S | Med |
| QRCodeScanRegion | TH:347; TH:1754 | Owner tools plus capture | LIMITED: as Region | As Region. A dragged selection around a known code decodes (M7, virtual session only) | S | Med |
| HashCheck | TH:350; TH:960 | None found | READY | None | S | Med |
| Metadata | TH:353; TH:984 | None found | READY | None | S | Med |
| StripMetadata | TH:356; TH:1002 | None found | READY | None | S | Med |
| IndexFolder | TH:366; UM:538 | None found | READY | None | S | Med |
| ClipboardViewer | TH:369; TH:1332 | Owner clipboard | UNVERIFIED | wl-paste as fallback (V) | S | Med |
| BorderlessWindow | TH:372; ShareX.Tools/Tools/BorderlessWindow/BorderlessWindowManager.cs:1 | Done in M7; Win32 styles deleted | LIMITED: clients cannot edit foreign windows | KWin script. Filling the output uses KWin's full screen, because KWin pushes a plain window out of a panel's strip as soon as it loses focus; Keep taskbar visible uses `noBorder` and `frameGeometry` on the area below the panels. Lost: the program can tell it's full screen, and some change their layout for it; under Keep taskbar visible, a window that draws its own title bar is refused with a message, as KWin has nothing to remove. Survives ShareX quitting, because KWin holds it; ends when the window closes, and a window rule would be needed to outlast that. After a restart of ShareX, toggling restores a full screen window's size, but a working-area window gets its frame back at its large size. Ran on Qt Wayland, XWayland and GTK windows with a real plasmashell top panel (M7, virtual session only) | M | Med |
| ActiveWindowBorderless | TH:375; TH:1290 | Done in M7; GetForegroundWindow deleted | LIMITED: as BorderlessWindow | KWin's active window, or the last used one when ShareX has the focus. Ran through the command line, not a physical hotkey | M | Med |
| ActiveWindowTopMost | TH:378; TH:1311 | Done in M7; GetForegroundWindow deleted | LIMITED: as BorderlessWindow | KWin `keepAbove`, same choice of window. Turning it on ran through the command line. Turning it off wasn't run there, because the second process raises ShareX; the choice of window is unit tested | M | Med |
| InspectWindow | TH:381; ShareX.Tools/Tools/InspectWindow/InspectWindowService.cs:1 | Done in M7; Win32 handles deleted | LIMITED: compositor-level metadata only | Pick uses KWin's own `queryWindowInfo` click, which replaces ShareX's overlay; properties come from a KWin script. Listed: id, title, class, resource name, role, desktop file, process, geometry, output, type, state. Always on top and opacity write to KWin. Hidden as gone: child controls and Pick control, Win32 styles, handle, window icons; there is no X11 id either, as KWin 6.8 scripts don't expose `windowId`. ShareX's own windows are left out of the list. Ran on Qt Wayland and XWayland windows (M7, virtual session only) | M | Med |
| NetworkMonitor | TH:384; ShareX.Tools/Tools/NetworkMonitor/NetworkMonitorProbe.cs:83 | Owner tools: Ping | UNVERIFIED | None; ICMP permissions not tested | S | Med |
| MonitorTest | TH:387; ShareX.Tools/Tools/MonitorTest/MonitorTestWindow.axaml.cs:53 | Owner tools: GetCursorPos. M3: uses `Screens.ScreenFromVisual`; not run | UNVERIFIED | Avalonia screen selection (A) | S | High |


#### HotkeyType: Other (8)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| DisableHotkeys | TH:391; TH:2174 | Done in M5 | UNVERIFIED | KGlobalAccel: disabling releases every key but its own, enabling takes them back (virtual session) | S | Med |
| OpenMainWindow | TH:394; `MainWindow.axaml.cs:138` | Window activation | LIMITED: not run on the real desktop. In a virtual KWin session KWin refused Avalonia's `Activate` and only asked for attention: main window behind or minimized at the default level after an X11 app was used, every state with any app from the High level up. ShareX then falls back to `KWinDesktop.ActivateWindow`, and ends up active and on top by tray, hotkey and second process, including from hidden. If KWin can't be reached it stays a request for attention | KWin script (A) | S | Low |
| OpenScreenshotsFolder | TH:397; TH:864; ShareX.HelpersLib/Helpers/FileHelpers.cs:294 | Trailing-backslash bug. M3: `ShowFolders` ran in a virtual KWin session | READY | xdg-open (V) | S | High |
| OpenHistory | TH:400; TH:878 | Owner this report: HistoryLib | UNVERIFIED | None | S | Med |
| OpenImageHistory | TH:403; TH:894 | Thumbnail COM fallback deleted; videos through the host's `ffmpeg` | UNVERIFIED | See Part 2 | S | Med |
| ToggleActionsToolbar | TH:406; TH:2629 | Toolbar position, cursor | LIMITED: see Actions toolbar in the Presentation scan | X11 backend on XWayland; the hotkey ran through KWin with injected keys in a virtual session | M | Low |
| ToggleTrayMenu | TH:409; TH:2773 | Owner shell: Windows tray | LIMITED: programmatic tray-menu popup not expected | Avalonia TrayIcon path (A) | M | Low |
| ExitShareX | TH:412 | None found | UNVERIFIED | While recording it asks first, and Yes stops and saves the recording instead of discarding it. Run on the stub through the main window's close, which reaches the same code. The tray menu click itself wasn't run. SIGTERM, SIGHUP and SIGINT during a recording also stop and save it before exit, waiting up to 60 s (run on the stub in a virtual session) | S | Med |


#### AfterCaptureTasks (21)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| ShowQuickTaskMenu | UM:343; ShareX/QuickTaskMenu.cs:94 | KWin cursor via `CaptureHelpers.cs:92` | LIMITED: not run on the real desktop. In a virtual KWin session with two monitors the menu opened with its first row under the pointer on either monitor and stayed on screen at each corner; it stops above the panel | None needed | S | High |
| ShowAfterCaptureWindow | TH:667 | Topmost window | UNVERIFIED | None | S | Med |
| BeautifyImage | WT:568 | Skia | UNVERIFIED | None | S | Med |
| AddImageEffects | WT:578; TH:706 | Skia | UNVERIFIED | None | S | Med |
| AnnotateImage | WT:589 | Owner editors | UNVERIFIED | See ImageEditor. The after-capture task opened the editor on a capture taken with `-PrintScreen` (virtual session) | M | Med |
| CopyImageToClipboard | WT:599 | Owner clipboard; DIB branch Windows-gated | UNVERIFIED | wl-copy fallback (V) | S | Med |
| PinToScreen | WT:605 | Owner tools | LIMITED: as PinToScreen hotkey | As the PinToScreen hotkey; this after-capture route not run | M | Med |
| SendImageToPrinter | WT:611; TH:687; ShareX.HelpersLib/Printer/PrintHelper.cs | Done in M7 | UNVERIFIED | Portal Print; see 5.8 | M | High |
| SaveImageToFile | WT:625 | Serialized path risk, see Part 2 | UNVERIFIED | None | S | Med |
| SaveImageToFileWithDialog | WT:638 | Avalonia picker | UNVERIFIED | Portal FileChooser (V) | S | Med |
| SaveThumbnailImageToFile | WT:657; TH:454 | Skia | READY | None | S | Med |
| PerformActions | WT:688; ShareX.HelpersLib/ExternalProgram.cs:67 | `.exe` presets; requires full path | UNVERIFIED | Reconfigure with Linux paths (A) | S | Med |
| CopyFileToClipboard | WT:736 | Owner clipboard | UNVERIFIED | wl-copy fallback (V) | S | Med |
| CopyFilePathToClipboard | WT:740 | Owner clipboard | UNVERIFIED | None | S | High |
| CopyFolderPathToClipboard | WT:744 | Owner clipboard | UNVERIFIED | None | S | High |
| ShowInExplorer | WT:749; ShareX.HelpersLib/Native/NativeMethods_Helpers.cs:482 | SHOpenFolderAndSelectItems. M3: `ShowItems` reached Dolphin with `--select` and the `xdg-open` fallback ran, both in a virtual KWin session | READY | org.freedesktop.FileManager1 (V) | S | High |
| AnalyzeImage | WT:754 | HTTP AI | UNVERIFIED | None | S | Med |
| ScanQRCode | WT:759 | ZXing | UNVERIFIED | None | S | Med |
| DoOCR | WT:548 | Done in M7 through the OCR tool's helper; this after-capture route not run | UNVERIFIED | Host `tesseract` | M | High |
| ShowBeforeUploadWindow | WT:395 | Topmost window | UNVERIFIED | None | S | Med |
| UploadImageToHost | WT:618; ShareX/SettingManager.cs:246 | Secrets dependency, see Part 2 | UNVERIFIED | None | S | Med |
| DeleteFile | WT:339 | None found | READY | None | S | High |


#### AfterUploadTasks (6)

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| ShowAfterUploadWindow | ShareX/TaskManager.cs:284 | None found | UNVERIFIED | None | S | Med |
| UseURLShortener | WT:803 | None; HTTP | READY | None | S | High |
| ShareURL | WT:815 | None; HTTP, SMTP or browser | UNVERIFIED | xdg-open (V) | S | High |
| CopyURLToClipboard | WT:830 | Owner clipboard | UNVERIFIED | None | S | High |
| OpenURL | WT:849; ShareX.HelpersLib/Helpers/URLHelpers.cs `OpenURL` | A `BrowserPath` runs with the URL as its one argument. One that can't start, such as `chrome.exe` from a copied config, falls back to the default browser and is logged; before this the path itself went to xdg-open and the URL was dropped (run on a build without the fix) | UNVERIFIED | xdg-open. A real upload to a local server in a virtual session, with a logging stand-in for xdg-open and a fake browser: Windows path, non-executable file, no path, working browser. No real browser ran | S | Med |
| ShowQRCode | WT:865 | ZXing | UNVERIFIED | None | S | Med |


#### Other tools and README features

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| Animated GIF maker | TH:1126 | None found | READY | None | S | Med |
| Remote storage browser | TH:1684 | None found; uses FTP, SFTP, S3 providers | READY | None | S | Med |
| Video player | TH:1232; ShareX.Tools/Controls/Media/MpvPlayer.cs | Done in M7: host libmpv; virtual session only, see 5.9 | UNVERIFIED | libmpv (A) | L | High |
| Region annotation tools | ShareX.ScreenCaptureLib/Presentation/RegionCapture/RegionCaptureWindow.axaml.cs:1 | Owner capture | LIMITED: as Region | As Region | L | Med |
| Blur, pixelate, smart eraser | README.md:49 | Owner capture and editors | UNVERIFIED | None | M | Low |
| Image uploaders (8) | ShareX.UploadersLib/Enums.cs:32 | None; HTTP | READY | None | S | High |
| Text uploaders (12) | ShareX.UploadersLib/Enums.cs:51 | None; HTTP | READY | None | S | High |
| File uploaders (31) | ShareX.UploadersLib/Enums.cs:78 | FTP, SFTP and Shared Folder exceptions in Part 2 | UNVERIFIED | See Part 2 | S | Med |
| URL shorteners (14) | ShareX.UploadersLib/Enums.cs:142 | None; HTTP | READY | None | S | High |
| URL sharing services (13) | ShareX.UploadersLib/Enums.cs:174 | None; browser or SMTP | UNVERIFIED | xdg-open (V) | S | High |
| Email uploader and sharing | ShareX.UploadersLib/FileUploaders/Email.cs:114 | None; SmtpClient | UNVERIFIED | None | S | Med |
| Custom uploaders, import | TH `ImportCustomUploader`; ShareX/ShareXCLIManager.cs:103 | None; association done in M5 | UNVERIFIED | `ShareX -install`, then `gio open` on a `.sxcu`, path with a space: the running ShareX asked, and the uploader showed in Custom uploader settings (virtual session). Double-clicking in Dolphin not run | S | High |
| Image effect import | TH `ImportImageEffect`; ShareX/ShareXCLIManager.cs:118 | None; association done in M5 | UNVERIFIED | `ShareX -install`, then `gio open` on a synthetic `.sxie`: its image unpacked to `ImageEffects`, and the preset with its effect showed in the effects window (virtual session). No `.sxie` from Windows tried | S | High |
| Destination settings | ShareX.UploadersLib/Presentation/DestinationSettings/DestinationSettingsAccounts.cs:427 | Browser and secrets | UNVERIFIED | See Part 2 | S | Med |
| Task and hotkey workflows | ShareX/CaptureHelpers/CaptureBase.cs:138; TH:77 | Hotkeys done in M5 | READY | KGlobalAccel; owner confirmed on the real desktop, 2026-11-04 | M | Med |
| Actions presets | TH `AddDefaultExternalPrograms` | Registry search deleted | UNVERIFIED | PATH lookup for KolourPaint, Krita, GIMP, Pinta and Inkscape. Only a file on PATH counts, because an action needs a real file to run, so Flatpak apps aren't found; `.desktop` lookup would add those but means parsing `Exec` lines. In a virtual session the Actions page listed fake programs, saved their full paths, and a seeded action ran on a capture | S | High |
| Command line | ShareX/ShareXCLIManager.cs:58; ShareX.HelpersLib/CLI/CLIManager.cs:63; ShareX/Infrastructure/ApplicationCommandLine.cs:53 | `-` switches, safe for Unix paths | READY | None. `--help` and `-h` print usage and exit before any startup (run in a virtual session) | S | High |
| Watch folders | ShareX/WatchFolder.cs:58 | FileSystemWatcher | UNVERIFIED | None | S | Med |
| Download and upload URL | UM:518 | None found | READY | None | S | Med |
| History window, stats | TH:878; TH:910; ShareX.HistoryLib/Integration/HistoryIntegration.cs:39,53 | None found | UNVERIFIED | None | S | Med |
| Debug log | TH:916 | None found | READY | None | S | Med |
| Backup export and import | ShareX/SettingManager.cs `Export`, `Import`; ShareX.HelpersLib/Zip/ZipManager.cs `ReadEntries`; ShareX.HistoryLib/Managers/HistoryManagerSQLite.cs `MergeFrom` | M8. Export warns first and writes 0600: built one with synthetic secrets, and they are in plain text, because `SaveToMemoryStream(false)` skips the provider. Import takes only the five known root names with per-entry caps (64 MB configs, 256 MB history), reads configs in memory, and merges History.db after backing the old one up to `Backup/` as 0600. Same schema as Windows by the code; no duplicates on a second import | UNVERIFIED | Owner's real export is the check | S | High |
| Portable and sandbox modes | ShareX/Infrastructure/PersonalPathManager.cs:58,64 | None found. `-portable` started in M2; sandbox not run | UNVERIFIED | None | S | Med |
| Policy options | `ShareX/SystemOptions.cs` | Registry deleted | UNVERIFIED | `/etc/ShareX/SystemOptions.json`, see 5.7 | S | High |
| Start on login | `ShareX/StartupManager.cs` | Done in M5 | UNVERIFIED | XDG autostart entry; `systemd-xdg-autostart-generator` accepted it and its line ran (virtual session only). A real login is untested | S | High |
| Shell context menu | `IntegrationHelpers.SetUploadMenu` | Done in M5 | UNVERIFIED | KIO service menu; clicked in Dolphin in a virtual session. Dolphin/KIO only | S | High |
| Edit with ShareX | `IntegrationHelpers.SetEditMenu` | Done in M5 | UNVERIFIED | KIO service menu; shown in Dolphin, its `Exec` line ran against a running ShareX (virtual session only) | S | High |
| Send To | Toggle removed in M5 | Explorer Send To | DROP | "Upload with ShareX" covers it | S | Med |
| Browser extension hosts | `ShareX/IntegrationHelpers.cs`; `ShareX.NativeMessagingHost/Program.cs` | Done in M5, see 5.7. Registry code deleted | UNVERIFIED | Per-browser manifest folders. No real browser or extension has run it | M | High |
| Tray icon | ShareX/Presentation/MainWindow/TrayIconService.cs | Windows path deleted in M5 | READY | Avalonia TrayIcon on SNI; owner confirmed the right-click menu on the real desktop, 2026-11-04, see 5.6 | S | Med |
| Toast notifications, 12 click actions | ShareX/Enums.cs:373; ShareX/Presentation/Notification/NotificationWindow.cs:564 | Custom positioned window | LIMITED: see Presentation scan | notify-send (V); notification portal (V) | M | Med |
| Thumbnail click actions (7) | ShareX/Enums.cs:389 | Open-folder and thumbnail dependencies | UNVERIFIED | See Part 2 | S | Med |
| File exists prompt (4 actions) | ShareX/Enums.cs:400; TH:782 | None found | UNVERIFIED | None | S | Med |
| Notification sounds | TH:2220; ShareX.HelpersLib/Helpers/HostSoundPlayer.cs | Done in M7; SoundPlayer deleted | READY | Owner confirmed the sound after a capture on the real desktop, 2026-11-04. Host `pw-play`, then `paplay` if it's missing or fails, as an argument list. An embedded sound goes to a mode 600 temp file, removed after playing and when ShareX quits. As with Windows' PlaySound, a new sound stops the last. No player: logged once and silent. Ran in a virtual session with fake players only; nobody has heard it yet | S | High |
| Taskbar progress | WT:391; `ShareX.HelpersLib/Native/TaskbarManager.cs` | Done in M5 | UNVERIFIED | Unity LauncherEntry, seen on a real plasmashell in a virtual session | S | Medium |
| Hide desktop icons | ShareX/CaptureHelpers/CaptureBase.cs:86; ShareX.HelpersLib/DesktopIconManager.cs:30 | Explorer desktop | DROP | None | S | High |
| Hide taskbar during capture | ShareX/Presentation/TaskSettings/TaskSettingsPageBuilder.cs:458 | Windows taskbar | DROP | None | S | High |
| Restart after Windows update | Deleted in M5 | RegisterApplicationRestart | DROP | None | S | High |
| Auto-update, dev builds | ShareX.HelpersLib/UpdateChecker/GitHubUpdateChecker.cs:174; TH:2634 | Setup `.exe` assets | DROP | Distribution packaging (A) | S | High |
| Steam build | ShareX/Presentation/ApplicationSettings/ApplicationSettingsViewModel.cs:162 | Windows launcher | DROP | None | S | Med |
| Microsoft Store build | ShareX/Enums.cs:30-31 | Windows.ApplicationModel | DROP | None | S | High |
| Installer | `ShareX.Setup` removed in M1 | Windows setup | DROP | Distribution packaging (A) | M | High |
| Translations (29 languages incl. Automatic) | ShareX/Enums.cs:52; ShareX/ShareX.csproj | NLS flag has no Linux effect (A) | UNVERIFIED | libicu present (V) | S | Med |
| Clicker easter egg | ShareX/EasterEggs/ShareXClicker/ShareXClickerControl.cs:188 | None since M7 | UNVERIFIED | As sounds; not run | S | Med |


#### Presentation windows scan

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort (S/M/L) | Confidence |
|---|---|---|---|---|---|---|
| Recorder bar window | ShareX/Presentation/ScreenRecording/ScreenRecorderBarWindow.axaml.cs | M2 removed the `user32` calls so it opens under X11. The setup outline now takes no input: in a virtual session a click inside it reached both an X11 and a Wayland window under it, with the outline still drawn, the bar still open and its Record button working. The recording frame already did this, see Recording controls | LIMITED: tool-window style. The bar and outline stay off the taskbar and out of Alt+Tab, but the outline isn't kept behind the bar | `_KDE_NET_WM_STATE_SKIP_SWITCHER` set before each window opens, as Avalonia's `ShowInTaskbar` only sets skip-taskbar. Virtual session: KWin's `skipSwitcher` went from false to true on the bar, the outline and the recording frame, and a real Alt+Tab walk no longer lands on them. The bar still takes focus and clicks, and the outline is gone when the recorder starts (fake recorder) | M | High |
| Main window thumbnails | ShareX/Presentation/MainWindow/ThumbnailItemViewModel.cs | Shell COM call deleted in M7 | UNVERIFIED | Videos get one frame from the host's `ffmpeg` through `VideoThumbnailHelper`, cancelled when the task is removed. Ran again in a virtual session with synthetic clips and damaged files, at most 2 `ffmpeg` at once. Damaged videos and images that can't be decoded show the placeholder icon, and the failure is logged | S | High |
| Main window menus and position | ShareX/Presentation/MainWindow/MainWindow.axaml.cs:249,2133 | KWin cursor; Position | LIMITED: not run on the real desktop. In a virtual KWin session with two monitors the Toggle tray menu hotkey opened the menu at the pointer on either monitor and on screen at each corner; it stops above the panel. Escape, the arrow keys and the hotkey all work in it. With `TrayAutoExpandCaptureMenu` on, one hotkey press on the second monitor didn't close it, with or without the Escape fix (not looked into) | None needed | S | Med |
| Actions toolbar | ShareX/Presentation/ActionsToolbar/ActionsToolbarWindow.axaml.cs:54,266,271 | Topmost, Position, GetCursorPos | LIMITED: a panel on an edge between two monitors isn't in Avalonia's work area, so the first open or a drag can sit over it. A saved place of exactly 0,0 counts as none | KWin honours the moves, so no KWin rule or script. A real plasmashell panel on each edge and two outputs (virtual session): the first open lands at the bottom right of the pointer's output, and saved places on both outputs, a drag across them and staying on top all hold. Not on real monitors or at a scale other than 1 | M | Med |
| Notification window | ShareX/Presentation/Notification/NotificationWindow.cs:322,335,371,439 | KWin cursor; Position; `IsPointerOver` | LIMITED: not run on the real desktop with two monitors. In a virtual KWin session it opened on the pointer's monitor; hovering held it, also during the fade, and it closed after the pointer left. It also holds when it opens under a still pointer: no event reports that pointer leaving in one jump, so while it stays still a KWin script looks again once per toast duration, and the toast can stay up to one duration longer after such a jump. A monitor with no panel still leaves the panel's height free at the bottom, in menus too: Avalonia intersects each output with X11's one `_NET_WORKAREA` rectangle, which KWin works out for the whole desktop. KWin's per-output `MaximizeArea` and a real maximize are correct there, so ShareX's own sums aren't involved | None needed | M | Med |
| Quick task menu | ShareX/QuickTaskMenu.cs:94,107 | KWin cursor; Topmost | LIMITED: not run on the real desktop. Virtual session results are under ShowQuickTaskMenu; Escape closes it | None needed | S | High |
| Topmost dialogs | ShareX/Presentation/AfterCapture/AfterCaptureWindow.axaml:13; ShareX/Presentation/BeforeUpload/BeforeUploadWindow.axaml:13; ShareX/Presentation/FileExist/FileExistWindow.axaml:11; ShareX/Presentation/UploadConfirmation/LargeFileUploadWarningWindow.axaml:12; ShareX/Presentation/UploadConfirmation/MultiUploadConfirmationWindow.axaml:12 | Topmost | UNVERIFIED | X11 backend on XWayland (A) | S | Low |
| Application settings integrations | ShareX/Presentation/ApplicationSettings/ApplicationSettingsWindow.axaml:256-296; ShareX/IntegrationHelpers.cs; ShareX/StartupManager.cs | None left on the page: Dolphin wording since M5, Windows print text gone, Steam section hidden. Only some member names still say Windows | UNVERIFIED | In a virtual session each box on the Integration page wrote its file: autostart entry, both service menus, Chrome and Firefox manifests. A real login, Dolphin and a real browser extension weren't part of this; see their own rows | M | High |
| Failed-save message | ShareX/SettingManager.cs `Settings_SettingsSaveFailed`; ShareX/ImageData.cs `Write` | Anti-virus text removed | UNVERIFIED | The system's own reason, which names the path. Into a read-only folder in a virtual session: settings gave a toast and a capture a dialog, both with `Access to the path ... is denied` | S | High |


### 5.2 Build and packaging

The only table whose rows were really run; see its evidence column.

| Item | Evidence | Label | KDE Wayland/Linux replacement | Effort | Confidence |
|---|---|---|---|---|---|
| Props filename casing | Renamed to `Directory.Build.*`. Evaluated `Version=22.1.0`; window title "ShareX 22.1 Dev" (`VersionText` drops a zero build number) | READY | None needed | S | High |
| RID mapping | `Directory.Build.props:13-20`: `Platform=x64` gives `linux-x64` and an ELF apphost (built and run). `ARM64` evaluates to `linux-arm64`; not built | READY | None needed | S | High |
| ShareX.ImageEditor.App | 0 errors with no RID override (only because of the casing bug); window "ShareX - Image Editor - 800x600"; `WinExe` and `app.manifest` are harmless | READY | None needed | S | High |
| Windowing backend | `libX11`, `libXi`, `libXrandr`, `libGLX_nvidia` mapped, no `libwayland`; only `avalonia.x11` restored | LIMITED | XWayland only: no native Wayland window. Effects on capture and scaling not tested | L | High |
| Resx casing | `ShareX.UploadersLib/Properties/Resources.resx:17`: path corrected, `MSB3103` gone in the build | READY | None needed | S | High |
| Windows TFMs | All projects on `net10.0`; `ShareX.sln` builds in Debug and Release with 0 errors, 156 warnings (all CA1416) | READY | None needed | M | High |
| HelpersLib | `WindowsDesktop.App` removed; `System.Drawing.Common`, `System.Management` added at 10.0.13 (`System.Windows.Extensions` removed in M7 with SoundPlayer); 0 errors. Compile only. `ProtectedData` removed in M2, `Tmds.DBus.Protocol` 0.94.1 added (same version Avalonia.FreeDesktop uses) | SWAP | Keep the packages to compile only; replace Registry and printing behind interfaces | M | High |
| HistoryLib, ImageEffectsLib, UploadersLib, ScreenCaptureLib | 0 errors in the M1 build; `History.db` is created at start-up | UNVERIFIED | Compile only; CA1416 misses P/Invoke, so behaviour is untested | M | Medium |
| SQLite | `libe_sqlite3.so` shipped, `History.db` created; `Directory.Packages.props:7` is an 11.0 RC | READY | Consider a stable version | S | High |
| ScreenRecordingLib | 0 errors. M6 replaced the D3D and Media Foundation pipeline with a `gpu-screen-recorder` wrapper; no package or project references remain | UNVERIFIED | Confirm on real captures | S | Medium |
| ShareX.Tools OCR | 0 errors. M7: `OCRHelper.cs` runs the host `tesseract` | SWAP | Tesseract (M7, virtual session) | M | High |
| ONNX provider | `BackgroundRemovalService.cs` uses the default CPU provider; the bundled `libonnxruntime.so` needs only glibc and libstdc++ | UNVERIFIED | `Microsoft.ML.OnnxRuntime`, CPU only. No GPU provider | S | Medium |
| Vortice.Direct2D1 | Deleted from the editor in M7; nothing uses Vortice any more | DROP | Skia with Noto Color Emoji | S | Medium |
| PhotoEditor, NativeMessagingHost | 0 errors; CA1416 6 (`ImageIO.cs`) and 0; host manifests are generated with an absolute path, see 5.7 | SWAP | Linux manifest with an absolute path, installed in browser native-messaging folders | S | Medium |
| ShareX project | 0 errors; main window ran with a throwaway home and no session bus; `Windows.ApplicationModel` is behind `#if MicrosoftStore` | READY | None needed to build and open; tray not exercised | S | High |
| `UseNls` | `ShareX.csproj:59`; present in the runtime config and the app still ran | READY | Remove on Linux for tidiness | S | Medium |
| Global hotkeys | `KGlobalAccelHotkeyHost` since M5; ran in a virtual session, a physical key press is the owner's check | UNVERIFIED | KGlobalAccel; no free key grabbing: a key another program holds shows as failed | L | High |
| Registry | Logged `PlatformNotSupportedException` from `RegistryHelpers.cs:81`, not fatal | SWAP | Config files | M | High |
| Portable mode | `AppPaths.cs:42`: folder renamed `ShareXData` in M2. `-portable` run in a virtual session: starts, uses that folder, no "Avalonia is already initialized" | READY | None. An unwritable app folder was still running after 12 s with no crash text; by the code it falls back to `~/Documents/ShareX` | S | High |
| Tray icon | `TrayIconService.cs:77` uses Avalonia `TrayIcon` | READY | Status Notifier; owner confirmed the right-click menu on the real desktop, 2026-11-04 | S | Medium |
| ShareX.Setup | Removed in M1 | DROP | Packaging script | S | High |
| ShareX.Steam | Removed in M1 | DROP | None needed | S | High |
| Steam, MicrosoftStore configs | `Directory.Build.props:21-33`; the `.sln` maps them for all projects | DROP | Leave out of the Linux build matrix | S | High |
| CI | `pr.yml` is on `ubuntu-latest`, Release and Debug on x64; `build.yml` removed. Same build commands ran locally, not yet in Actions | UNVERIFIED | M5 adds the AppImage pipeline | M | High |
| `Scripts/` | PowerShell only, not called from any csproj; `pwsh` not installed here | UNVERIFIED | Optional developer tools; not needed to build | S | Medium |
| FFmpeg | `VideoTrimmerWindow.axaml.cs:40`, `FFmpegDownloader.cs:61` hardcode `ffmpeg.exe`. M3: `ffmpeg` resolved from PATH; downloaders deleted | READY | Use system `ffmpeg` as a package dependency | S | High |
| PKGBUILD/AUR | Not written. Depends: `dotnet-runtime`, `ffmpeg`, `mpv` (for `libmpv.so.2`), `libx11`, `libxcursor`, `libxi`, `libxrandr`, `libice`, `libsm`, `fontconfig` (from `ldd` and mapped libraries); optional for OCR: `tesseract` with a `tesseract-data-*` package; optional for sounds: `pipewire` (`pw-play`) or `libpulse` (`paplay`) | UNVERIFIED | Framework-dependent `-r linux-x64` | M | Medium |
| AppImage | Not built. The self-contained editor publish ran: 114 MB against 737 MB for the unpublished Debug output of all RIDs. **Must declare host dependencies for recording:** `gpu-screen-recorder` (the capped `gsr-kms-server` can't run from the AppImage's `nosuid` mount, so neither is bundled), plus `ffmpeg` with `ffprobe`, `kscreen-doctor`, `pactl` and `getcap`. **For OCR:** `tesseract` with a language package such as `tesseract-data-eng`, not bundled, as language packs are the user's choice **For the video player and trimmer:** `mpv`, for `libmpv.so.2`; ShareX says so when it is missing. **For sounds:** `pw-play` or `paplay` from the host, not bundled; its environment isn't cleaned of the AppImage's variables as Spectacle's is, because no AppImage has been built to test | UNVERIFIED | Use that publish as the payload; document the host packages | M | Medium |
| Flatpak | Not built | UNVERIFIED | X11 socket, portals, .NET extension | L | Low |
| `.desktop`, icons | `ShareX -install` writes them from the build output. `ShareX/Resources/hicolor` holds the frames of the two `.ico` files (16, 32, 48, 256, none upscaled). `StartupWMClass=ShareX` matches the real `WM_CLASS`, and KWin resolved the window to `ShareX.desktop` (virtual session only). It must stay on that one entry: with it on the hidden file-type entries too, KWin chose one of those | UNVERIFIED | Confirm on the real desktop; a package would ship the same files system-wide | S | High |


### 5.3 HelpersLib platform core

[V] = binary or D-Bus interface confirmed here, not exercised; [A] = assumed.

| Item | Evidence (file:line) | Windows dependency | Label | KDE Wayland/Linux replacement | Effort | Confidence |
|---|---|---|---|---|---|---|
| Project target | ShareX.HelpersLib/ShareX.HelpersLib.csproj:3 | `net10.0-windows`, WindowsDesktop framework (absent here [V]) | SWAP | `net10.0`; isolate Windows code | M | High |
| Primitives (49 files) | ShareX.HelpersLib/Helpers/CaptureHelpers.cs:27 | None from these types; assembly present [V] | READY | Keep | S | High |
| Icon (4 files) | ShareX.HelpersLib/Native/WindowInfo.cs:167 | Common absent [V]; only `WindowInfo.Icon` live | SWAP | Delete dead code; return SKBitmap | S | High |
| WindowInfo | ShareX.HelpersLib/Native/WindowInfo.cs:34 | HWND model | LIMITED | KWin `getWindowInfo`, scripting [V]. Lost: portable enumeration, styles | L | Medium |
| CaptureHelpers | ShareX.HelpersLib/Helpers/CaptureHelpers.cs:91 | Cursor and active window moved to KWin in M4; `GetPixelColor` still GDI | LIMITED | KWin script, in X11 coordinates; `PickColor` portal [V]. Lost: global cursor warp | M | Medium |
| WindowsImageInterop | ShareX.HelpersLib/Helpers/WindowsImageInterop.cs:78 | GDI capture | REWRITE | KWin `ScreenShot2` (restricted; Spectacle's desktop file declares it [V]) or Screenshot portal [V] | M | Medium |
| CursorData | Deleted in M4 | None | LIMITED | `spectacle -p`. Lost: separate cursor bitmap | M | Medium |
| WindowsNativeWindow | ShareX.HelpersLib/Native/WindowsNativeWindow.cs:48 | Layered Win32 overlays; throws off Windows | LIMITED | Avalonia transparent window [A]. Lost: absolute placement, z-order | L | Medium |
| DWMManager | ShareX.HelpersLib/DWMManager.cs:30 | dwmapi; 0 callers. M3: deleted | DROP | Delete | S | High |
| OS version gates | ShareX.HelpersLib/Helpers/Helpers.cs:64 | Fixed in M2: `OSVersion` is 0.0 off Windows, so all 10 `IsWindows*` return false (run on kernel 7.2). `CaptureHelpers.cs:304` still reaches `IsZoomed`, because it tests `!IsWindows10OrGreater()` | READY | None | S | High |
| Helpers misc | ShareX.HelpersLib/Helpers/Helpers.cs:565 | WindowsIdentity, registry; `IsTabletMode`, wallpaper path have 0 callers | SWAP | `Environment.IsPrivilegedProcess`, `/etc/os-release` [A] | S | Medium |
| Cursor lock, window move | ShareX.HelpersLib/Helpers/Helpers.cs:919 | Win32; no-op elsewhere | LIMITED | Pointer constraints [A]. Lost: arbitrary positioning | M | Medium |
| Authenticode, updater | ShareX.HelpersLib/Helpers/AuthenticodeSignatureVerifier.cs:65 | wintrust, setup.exe, `runas` | DROP | Package updates; if kept, checksum or gpg [V] | S | High |
| FileHelpers open/reveal | ShareX.HelpersLib/Helpers/FileHelpers.cs:337 | shell32; `UseShellExecute` is portable. M3: replaced; `ShowItems`, `ShowFolders` and fallback ran in a virtual KWin session | READY | `FileManager1.ShowItems` [V], `xdg-open` [V] | S | High |
| FileHelpers paths | ShareX.HelpersLib/Helpers/FileHelpers.cs:412 | Empty SpecialFolder likely aborts `Replace` [A]; `:\` root at :211; locks advisory at :470. M3: fixed and checked with a scratch program; locks still advisory | READY | Guard empties, XDG paths | S | Medium |
| RegistryHelpers | ShareX.HelpersLib/Helpers/RegistryHelpers.cs:41 | Registry stub [A]; callers IntegrationHelpers, ScreenRecordingOptions, SystemOptions, Setup, others | REWRITE | `.desktop`, `xdg-mime`, `kbuildsycoca6` [V]; JSON | M | High |
| DPAPI | ShareX.HelpersLib/Settings/EncryptedStringValueProvider.cs | `DPAPI.cs` and `ProtectedData` deleted in M2 | READY | Secret Service through `SecretServiceCrypto`; see Secrets in 5.1 | M | High |
| WshShell, ShortcutHelpers | Deleted in M5; `ShareX.HelpersLib/Helpers/DesktopEntryHelpers.cs` writes `.desktop` files instead | None | READY | None | S | High |
| MutexManager | ShareX.HelpersLib/MutexManager.cs:43 | None visible; .NET named mutex | UNVERIFIED | Keep; test cross-process | S | Medium |
| SingleInstanceManager | ShareX.HelpersLib/SingleInstanceManager.cs:96 | Pipe ACL; failure stops the listener (:136) | SWAP | `PipeOptions.CurrentUserOnly` [A] | S | Medium |
| DesktopIconManager | ShareX.HelpersLib/DesktopIconManager.cs:55 | Explorer windows; caller `ShareX/CaptureHelpers/CaptureBase.cs:86` | DROP | None known. Lost: auto-hide icons | S | Medium |
| FontSafe, ImageFont | ShareX.HelpersLib/ImageFont.cs:64 | None; saved Windows fonts may substitute | UNVERIFIED | Skia with fontconfig [A] | S | High |
| InputManager | ShareX.HelpersLib/Input/InputManager.cs:49 | SendInput; scrolling capture | LIMITED | RemoteDesktop portal [A]; `ydotool` active here [V]. Lost: silent injection | M | Medium |
| FFmpegCLIManager | ShareX.HelpersLib/Media/FFmpegCLIManager.cs:89 | Rejects non-`.exe`, so ffmpeg never runs. M3: check removed; ran `/usr/bin/ffmpeg` | READY | Remove check; `ffmpeg` [V] | S | High |
| TaskbarManager | ShareX.HelpersLib/Native/TaskbarManager.cs | Done in M5: COM removed, no support check | UNVERIFIED | Unity LauncherEntry signal (virtual session only) | S | Medium |
| Thumbnails, restart, display affinity | ShareX.HelpersLib/Native/NativeMethods_Helpers.cs:549 | Shell thumbnails, RegisterApplicationRestart, SetWindowDisplayAffinity | LIMITED | Freedesktop thumbnail cache [A]. Lost: capture exclusion, restart | M | Medium |
| Dead classes | ShareX.HelpersLib/SevenZipManager.cs:33 | Also TimerResolutionManager, FFmpeg downloaders; 0 callers. M3: deleted, with `DWMManager` and the FFmpeg downloaders. `NativeMethods.cs` went from 158 externs to 92 | DROP | Delete | S | High |


### 5.4 Screenshot and region capture

Paths: `S` = `ShareX.ScreenCaptureLib/`, `Scr` = `ShareX.ScreenCaptureLib/Screenshot.cs`, `R` = `ShareX.ScreenCaptureLib/Presentation/RegionCapture/RegionCaptureWindow.axaml.cs`, `H` = `ShareX.HelpersLib/`.

| Feature | Evidence | Windows dependency | Label | KDE Wayland replacement | Effort | Confidence |
|---|---|---|---|---|---|---|
| Build | `S`ShareX.ScreenCaptureLib.csproj | Done: net10.0; Vortice removed in M4 with `HDRScreenCapture.cs` | READY | None | M | High |
| Fullscreen, rectangle, custom, last region | `Scr`; ShareX/CaptureHelpers/CaptureCustomRegion.cs:36; CaptureLastRegion.cs:48 | Done in M4 | UNVERIFIED | `CaptureWorkspace` in `S`KWinScreenShot.cs, then `spectacle -b -n -f -o` in `S`SpectacleCapture.cs, the only place that spawns it, if that fails. Rectangles crop the image; asking KWin for only the rectangle would be faster still on large desktops. KWin draws the whole rectangle, so what hangs off a monitor is cleared from the space beside it (virtual session only) |
| ScreenShot2 | `S`KWinScreenShot.cs | Replaces spectacle as the first choice | UNVERIFIED | KWin 6.8.0 dropped its caller check (commit 0ea31ae7; 6.8.1 refused nothing from a plain process with no desktop file), so this needs no `-install`, no desktop file and nothing system-wide, and an AppImage is no different. A caller's own windows are hidden by default, so `hide-caller-windows` is off. Refused, or no such service: spectacle for the rest of the run, logged once. Any other failure: spectacle for that shot; a silent pipe gives up after 5 s. Pixels are read straight into one bitmap, so memory is one image and no PNG. Before 6.8, without the desktop file key, it's refused and spectacle takes over (played by a fake service; older KWin not run). Virtual session only | M | Medium |
| Active monitor | `Scr`; `H`Helpers/CaptureHelpers.cs:50 | Done in M4 | READY | `spectacle -m`, active window's output. The PNG's `screen` text chunk says which (M4; owner confirmed on the real desktop, 2026-11-03) | S | Medium |
| Active window, by handle | `Scr` | Done in M4 | READY | `CaptureActiveWindow`, and `CaptureWindow(string)` replaces IntPtr and asks KWin for that window without activating it. Spectacle is the fallback, which activates through a KWin script first (M4; owner confirmed on the real desktop, 2026-11-03, on spectacle; ScreenShot2 in a virtual session only) | M | Medium |
| Client area | `Scr` | Done in M4 | LIMITED | `include-decoration` and `include-shadow` off, the options `-a -e -S` asks for, same pixels (virtual session only); apps drawing their own titlebars keep them | S | Medium |
| Auto-hide taskbar | Removed from `Scr` in M4; setting left in place | None | DROP | None | S | High |
| Transparent window | `Screenshot_Transparent.cs` deleted in M4 | None | UNVERIFIED | `spectacle -a` is RGBA with the shadow; the setting no longer changes anything. Alpha over a real desktop not checked (M4, virtual session only) | M | Low |
| Cursor capture | ShareX/TaskHelpers.cs; ShareX/CaptureHelpers/CaptureRegion.cs | Done in M4 | LIMITED | `spectacle -p`: drawn at the pointer only with `-p`, measured with an EI pointer (M4, virtual session only). Movable cursor annotation lost: in region capture it is part of the frozen image | M | Medium |
| HDR | `HDRScreenCapture.cs` deleted in M4; setting left in place | None | DROP | Use KWin output; tone-mapping unknown | S | Low |
| Overlay window | `R`; `S`Presentation/RegionCapture/RegionCaptureSession.cs | Done in M4 | UNVERIFIED | One fullscreen, Topmost window per output, sized in X11 pixels. Covers real Plasma panels on both outputs, top and bottom, at full output size, with focus on either output (virtual session only, plasmashell inside it). M4's check had one bottom panel, so it missed the top-panel offset the owner saw. Real monitors not seen with this fix yet | L | Medium |
| Selection, dimming | `R`; `S`Presentation/RegionCapture/RegionSelectionOverlay.cs | Done in M4 | UNVERIFIED | Selection is global, in image pixels, and each window draws its part. Dragging and moving across outputs work by the X11 implicit grab, driven with `xdotool` (M4, virtual session only) | S | Medium |
| Initial pointer, arrow-key nudge | `R` | Done in M4 | LIMITED | Start point from the KWin script. Arrow keys move a virtual point (HUD, hover, selection being created): the real cursor doesn't move, and it wasn't run | M | Medium |
| Active-monitor cursor lock | Removed in M4 | None | LIMITED | One overlay on the output `-m` captured (M4, virtual session only); pointer can leave it | S | Medium |
| Magnifier | `R`; MagnifierPixelGrid.cs | Reads each monitor's frozen crop | UNVERIFIED | Shown on the monitor with the pointer, and samples across monitors (M4, virtual session only). Was READY; the code changed and the owner hasn't seen it | S | High |
| Annotation | `R`; ShareX.ImageEditor/Presentation/Rendering/WindowsCursorBitmapRenderer.cs:81,391 | One editor per monitor, composed on completion | LIMITED | Each monitor has its own toolbar, tool and undo, and a shape can't cross monitors. One shape in a two-monitor selection checked (M4, virtual session only); the other tools not run | S | Medium |
| Window snapping | `H`Helpers/KWinDesktop.cs; `S`Presentation/RegionCapture/RegionCaptureSession.cs | Done in M4; `WindowsRectangleList.cs` deleted | UNVERIFIED | KWin script before the overlay opens. Skips minimized, other desktops, closing windows, pid -1 (M4, virtual session only) | M | Medium |
| Control snapping | Deleted in M4 | None | DROP | None; only frames are snapped, and the `DetectControls` setting does nothing | S | High |
| Window under cursor (8ed375c7b) | `S`Presentation/RegionCapture/RegionCaptureSession.cs | Done in M4 | READY | Same logic over `KWinWindow`. File names pick up the process name from `/proc` (M4; owner confirmed on the real desktop, 2026-11-03) | M | Medium |
| Monitor HUD clamp (627ceb477) | `R` | Done in M4 | UNVERIFIED | Each window clamps to itself. In mixed scale the toolbar can be wider than a small monitor and is cut off, and it can cover the magnifier at the top (M4, virtual session only) | S | Medium |
| Window list menu | ShareX/Presentation/MainWindow/MainMenuBuilder.cs:156 | Done in M4; `WindowsList.cs` deleted | UNVERIFIED | Same KWin script, normal windows that aren't skip-taskbar (M4, virtual session only) | S | Medium |
| Scrolling input | `S`ScrollingCaptureManager.cs:104-147,232 | SendInput, SendMessage, GetScrollInfo | LIMITED | RemoteDesktop portal or XTest. XTest from XWayland does prompt: KWin's "Remote Control" dialog, seen in the M4 spike unless `XwaylandEisNoPrompt` is set. Scroll-message method and scrollbar query lost. Since M4, selecting a window here returns none, because it still needs an HWND | L | Low |
| Scrolling frame | `S`Presentation/ScrollingCapture/ScrollingCaptureRegionWindow.axaml.cs | M2 removed ExStyle and SetWindowRgn so it opens under X11. Lost: click-through, the hole in the frame and the tool-window style, so the window now sits over the region and takes its clicks | LIMITED | X11 shape region; exact position only under XWayland | M | Low |
| Scrolling UI | `S`Presentation/ScrollingCapture/ScrollingCaptureWindow.axaml.cs | None found | UNVERIFIED | Keep; its capture and selection depend on rows above | S | Medium |


### 5.5 Screen recording

Paths: `SRL` = `ShareX.ScreenRecordingLib`, `SCL` = `ShareX.ScreenCaptureLib`, `HL` = `ShareX.HelpersLib`, `SX` = `ShareX`. M6 built this on the host's `gpu-screen-recorder` (gsr); see the design note in section 4. **Nothing here has run on real gsr, so no row is READY.** "Stub" means proven against a fake recorder that acts out each measured behaviour; measured gsr behaviour comes from the M6 spike.

**Regression tests:** `ShareX.ScreenRecordingLib.Tests` drives `ScreenRecorder` against a fake `gpu-screen-recorder` (the scripts in its `Fixtures/`), with `PATH` set on the test process alone so the real recorder can't be reached. It needs no desktop, GPU or D-Bus, but does need `python3` and `setpriv` (util-linux). `ScreenRecordManager.FinishRecording` is in the app project behind windows and isn't covered. Run it from the repo root:

```
systemd-run --user --scope -q -p MemoryMax=4G -p MemorySwapMax=0 env MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test ShareX.ScreenRecordingLib.Tests/ShareX.ScreenRecordingLib.Tests.csproj -p:Platform=x64 -p:UseSharedCompilation=false -m:2 -nodeReuse:false
```

| Feature | Evidence (file:line) | Dependency | Label | State on KDE Wayland | Effort | Confidence |
|---|---|---|---|---|---|---|
| Build, GPU, COM plumbing | `Audio/`, `Encoding/`, `Native/`, `Video/` deleted; SRL csproj | None | DROP | Deleted | S | High |
| Region, custom, last | `SRL/Gsr/GsrCommand.cs`; `SX/ScreenRecordManager.cs` | gsr KMS capture, `kscreen-doctor` | LIMITED: must fit one monitor, spanning or overhanging regions are refused before starting; scale other than 1 is refused (not tested) | `-w WxH+X+Y`; stub for arguments and refusals | S | Med |
| Monitor capture | `SRL/Gsr/GsrCommand.cs` | gsr | UNVERIFIED | A whole monitor is a region that fits it; stub | S | Med |
| Move region on resume | `SRL/ScreenRecorder.cs` `Resume(Rectangle)`; `SCL/Presentation/ScreenRecording/ScreenRecordWindow.axaml.cs` | gsr has no such request | LIMITED: can't move while recording; frame drag is off during a pause, and a moved rectangle is logged and ignored | None; stub | S | High |
| Window, active window | `SX/ScreenRecordManager.cs` `GetActiveWindowRegion`; `H`Helpers/KWinDesktop.cs `FitToOneOutput` | gsr: `-w focused` exits 2 on Wayland, and the window handle option is deleted | LIMITED: records the window's rectangle like any region, with the limits in 5.1 | Follow the window through the portal later, needs an owner-agreed session | M | Med |
| Cursor | `SRL/Gsr/GsrCommand.cs` | gsr `-cursor` | UNVERIFIED | Flag measured working in the spike; stub for the mapping | S | High |
| MP4, hardware encode | `SRL/Gsr/GsrCommand.cs`; `SRL/RecordingOptions.cs` | gsr `-k h264`, `-bm cbr -q`, `-ac aac`, NVENC | UNVERIFIED | `RequireHardwareEncoder=false` adds `-fallback-cpu-encoding yes` (not run) | S | High |
| No-overwrite output | `SRL/ScreenRecorder.cs` | gsr overwrites silently | UNVERIFIED | Temporary name, then move; stub, including a name taken mid-recording. A failed recording that wrote anything stays at the output name, and a zero-byte one or an abort removes only the empty placeholder, so a real file put at that name survives; stub | S | High |
| Session, timer, duration | `SRL/ScreenRecorder.cs`; `SRL/Gsr/GsrSession.cs`; `SRL/RecordingClock.cs` | gsr `-ipc`, no duration flag | UNVERIFIED | No armed state, so Prepare checks and reserves and Start spawns; ShareX's timer sends the stop; stub | S | Med |
| Failure handling | `SRL/Gsr/GsrSession.cs`; `SRL/Gsr/HostTools.cs` | Exit codes 0, 1, 2, 50, 51, 126; black video markers; SIGKILL | UNVERIFIED | Each path run on the stub, plus the recorder ignoring every stop. A failure no longer deletes anything with content: the file stays at the output name and the error says where. Started through `setpriv --pdeathsig INT`, so it stops if ShareX dies, leaving its hidden temp file behind for the row below | S | High |
| Interrupted recordings | `SRL/RecordingJournal.cs`; `SX/Infrastructure/ApplicationLifecycle.cs`; `SX/ScreenRecordManager.cs` | `PosixSignalRegistration`, `/proc/PID/stat` | UNVERIFIED | SIGTERM, SIGHUP and SIGINT finish the recording before exit, up to 60 s. For SIGKILL, each recording keeps an entry in `RecordingJournal` in the personal folder, and the next start moves a leftover to its final name, never over a real file, and shows a notification. An entry whose ShareX or recorder is still alive waits for a later start, so right after a kill the file can stay hidden until then. Run in a virtual session on the stub: all three signals, SIGKILL, a taken name, a second instance, a recorder that ignores its stop. Not run with the real recorder | S | High |
| Frame and drop counts | `SRL/RecordingOptions.cs` | gsr reports none; ffprobe | LIMITED: dropped frames and audio discontinuities are always -1 | Frames from ffprobe; -1 without it. If ffprobe fails for its own reasons (can't start, nonsense output, killed, crashed, or any exit code other than 0 and 1) or hangs for 10 seconds, the recording is still delivered with -1 and a line through `Diagnostic`; stub. Exit 1 means ffprobe rejected the file (real ffprobe on a non-video, run), or failed for its own reasons in the same way: that's reported as an error and the file stays at the output name, as with no-frames and black videos | S | High |
| GIF input | `SX/ScreenRecordManager.cs` `RecordNativeAsync` | gsr, `-fm cfr` | READY | MP4 first, because gsr has no raw frames; `GifWriter` deleted; owner confirmed on the real desktop, 2026-11-03 | S | Med |
| GIF encode | `SCL/ScreenRecording/ScreenRecorder.cs` `FFmpegEncodeAsGIF`; `SX/ScreenRecordManager.cs` `EncodeNativeGif` | FFmpeg | LIMITED: quality starts from 4:2:0 H.264 instead of lossless BGRA | ShareX's method run on real FFmpeg with generated clips; 801x601 output and crop offset measured. `full` and `diff` now make the palette in a separate run, because one run held every frame until the input ended. 640x360 clips, 50 and 200 frames: peak RSS was 127 and 253 MB, now 88 and 93 MB, with byte-identical GIFs. `single` stays one run (flat at 80-82 MB). Progress stays at 0 during the palette run, as before. The MP4 is deleted only after a complete GIF; a stop or failure discards the partial GIF and keeps the MP4 under the output name with `.mp4` (real FFmpeg, stopped mid-encode in a virtual session). Not yet run by the owner. | S | Med |
| System audio, mic | `SRL/Gsr/GsrCommand.cs`; `SRL/ScreenRecorder.cs` | gsr `-a` | LIMITED: two tracks when both are on, so first-track players miss the microphone | Empty track logged through `Diagnostic`, not shown in the UI; stub with generated silent AAC (6 bytes per packet, as measured on the Blue mic) | S | Med |
| Audio gain | `SRL/RecordingOptions.cs` | gsr has no option | LIMITED: gain settings are ignored and logged | None | S | High |
| Audio device lists | `SRL/AudioCaptureDevices.cs`; `SX/Presentation/ScreenRecording/ScreenRecorderBarWindow.axaml.cs:82` | `pactl -f json` | UNVERIFIED | JSON shape from memory, run only against a fake `pactl`; saved Windows IDs fail with exit 50 and a clear error | S | Med |
| Webcam capture, overlay | `SRL/CameraCaptureDevices.cs`; `SRL/ScreenRecorder.cs` | gsr can overlay rectangles only (READ, not run) | LIMITED: no camera for now; the device list is empty and the option is logged and ignored; circle shape has no gsr equivalent | `-w "monitor\|v4l2:/dev/video0"` could be wired later | M | Low |
| Recording controls | `SCL/Presentation/ScreenRecording/ScreenRecordWindow.axaml.cs`; `SX/Presentation/ScreenRecording/ScreenRecorderBarWindow.axaml.cs` | Win32 styles and GDI region removed, they threw `DllNotFoundException` on open (run) | LIMITED: no capture exclusion | An X11 input shape limits input to the toolbar, so the area inside the frame is click-through. Virtual session: a click inside the frame reached both an X11 and a Wayland window under it, with the frame still drawn, and failed on the build before this. A second click on Record within the double-click time stays on the bar, which now closes that much later; the Stop button already had its tooltip (hover run). What is recorded no longer moves when KWin pushes the frame inside the work area, which shifted a region near the bottom edge 46 px up. But the frame and controls stay where KWin put them, so there they sit over the region and are recorded. For a window filling the monitor the controls are off screen and one pixel of frame is recorded: stop with the hotkey that started it. Not on real monitors or at a scale other than 1 | M | Med |
| Mouse highlighter | `ShareX.Tools/Tools/MouseHighlighter/MouseHighlighterManager.cs` `BeginRecordingAsync` | Overlay on during a recording, off after it | LIMITED | Shows the pointer circle, not clicks. Run with the fake recorder: script loaded for the recording and unloaded after it, circle on screen. Not run: a real recording containing it | M | Med |
| Legacy FFmpeg capture | `ScreenRecordingOptions.cs:56,100-196`; `TaskSettings.cs:403` | gdigrab, ddagrab, dshow | DROP | Remove these sources | S | High |
| Legacy encode args, two-pass, concat | `ScreenRecordingOptions.cs:208-338`; `SCL/ScreenRecording/ScreenRecorder.cs:258`; `FFmpegCLIManager.cs:282` | None found | UNVERIFIED | Keep; AMF/QSV need review | S | Med |
| FFmpeg path | `FFmpegCLIManager.cs:89`; `FFmpegOptions.cs:78`; `FFmpegOptionsWindow.axaml.cs:483`; `TaskHelpers.cs:2209` | `.exe` guard blocks Linux. M3: resolved from PATH; ran through the thumbnailer. A `"` in a path is still unescaped in recording arguments (`ScreenRecorder.cs:268`, `ScreenRecordingOptions.cs:200`). Picker filter is `ffmpeg*`, not run | READY | Resolve `ffmpeg`; remove guard | S | High |
| FFmpeg device list | `FFmpegCLIManager.cs:229`; `DirectShowDevices.cs:30` | dshow | DROP | Reuse `pactl` list | S | High |
| FFmpeg download | `FFmpegDownloader.cs:40,61`; `FFmpegGitHubDownloader.cs:37,54`; `FFmpegUpdateChecker.cs:38,64` | Windows zips, `ffmpeg.exe`. M3: deleted | DROP | Package dependency | S | High |


### 5.6 Hotkeys, input, tray, taskbar

| Feature | Evidence | Windows dependency | Label | KDE Wayland replacement | Effort | Confidence |
|---|---|---|---|---|---|---|
| Hotkey data model | `HelpersLib/Input/HotkeyInfo.cs:33-70`, `InputKey.cs`, `InputMouseButton.cs` | VK-style values | READY | Add InputKey to Qt key mapping in the backend | S | High |
| HotkeyManager | `ShareX/HotkeyManager.cs:69-80,112-141` | Via `IHotkeyHost`, `IsActiveWindowFullscreen` | READY | Keep | S | High |
| Hotkey host | `KGlobalAccelHotkeyHost.cs`, `QtKeys.cs`; `IHotkeyHost.cs` | Done in M5; `WindowsHotkeyHost`, `NullHotkeyHost` deleted | UNVERIFIED | KGlobalAccel: `globalShortcutsByKey`, `doRegister`, `setShortcutKeys`, `unregister`, `setInactive`, `globalShortcutPressed`. Register, change, remove, restart and rebind ran in a virtual session with XTEST key presses; needs the owner's physical press | M | Med-High |
| Default hotkeys | `HotkeyManager.cs:243-253` | Print and Shift+Print clashed with Spectacle | UNVERIFIED | Full screen is Meta+Ctrl+F and screen recording Meta+Ctrl+V; the other three stay. All five are free against KWin, Spectacle and Plasma's stock shortcuts (virtual session); the owner's own bindings weren't read. A saved config keeps its old keys, which show as failed | S | High |
| Press-any-key UI | `HotkeySettingsWindow.axaml.cs:165-199`; `HotkeySettingsAvaloniaService.cs:189-192,242` | PrintScreen KeyUp workaround deleted in M5 | LIMITED | `blockGlobalShortcuts` during capture: a bound key otherwise fires its shortcut; with it Print, Meta+W, Alt+Tab and Meta combinations all reached the box (XTEST). Lost: it is desktop-wide, so ShareX killed during capture leaves every desktop shortcut off until the next ShareX start. VT switching and Meta alone as a launcher key not run | M | Med |
| Repeat limit, fullscreen suppression | `HotkeyRepeatLimiter` in `KGlobalAccelHotkeyHost.cs`; `HotkeyManager.cs:69-71`; `CaptureHelpers.cs` | Done in M5, with M4's KWin `fullScreen` check | UNVERIFIED | Both ran in a virtual session. Only `globalShortcutPressed` is used, so holding a key should trigger once rather than repeat; a physical hold not run | M | Med-Low |
| Session end | `ApplicationLifecycle.cs:66-76` | QUERYENDSESSION handler deleted in M5 | SWAP | Avalonia lifetime shutdown; SIGTERM makes hotkeys inactive, then takes its default action; session restore not investigated | S | Med |
| Low-level mouse hook | `ShareX.HelpersLib/Helpers/KWinCursorWatcher.cs` | Done in M7. WH_MOUSE_LL deleted | LIMITED | One KWin script on `workspace.cursorPosChanged`, reporting over D-Bus at most every 16 ms and only when the pointer moved. 30 s at 125 Hz input in a virtual session (software rendering, % of one core), tool off then on: ShareX 0 to 5.6, KWin 4.0 to 9.5, Xwayland 0.4 to 2.7. Idle: no added cost in any of them. No position while ShareX is frozen for over 5 s: the script takes that for dead. Unloads on toggle off, exit and SIGTERM. After SIGKILL it costs nothing idle, and goes the next time the pointer moves for 5 s or ShareX starts the tool again. Lost: click ripple, per-button colours, release crosshairs and fade, because Wayland shows no clicks without the `input` group | M | Med |
| Highlighter overlay | `MouseHighlighterOverlayWindow.cs` | Done in M7. Avalonia window, override-redirect, empty input shape through `X11InputShape.SetEmpty` | LIMITED | One radius-sized window that moves with the pointer, not one per monitor: nothing repaints as it moves, KWin never blends a screen-sized surface, and an override-redirect window has no panel placement problem and stays above panels. Run with a real plasmashell on 2 outputs: on both monitors, in both panel strips, across the boundary, at scale 2 and mixed 2 and 1. Clicks reach Wayland and X11 windows beneath, with an unshaped control window eating them. It is in captures, as on Windows: `SetWindowDisplayAffinity` had no caller and capture used CaptureBlt. KWin lists it as unmanaged, so `KWinDesktop` no longer calls such windows normal, or region capture would snap to it. Not run: real desktop, fullscreen games, where any overlay can stop direct scanout | M | Med |
| Synthetic input | `InputManager.cs:49`; `InputHelpers.cs:32-117` | SendInput, GetSystemMetrics | SWAP | RemoteDesktop portal with libei or `Notify*`; uinput fallback | M | Med |
| Scrolling capture input | `ScrollingCaptureManager.cs:104,110,133,138,142` | SendInput, window activation | LIMITED | Goes to focused/hovered surface; loses reliable window targeting; consent prompt expected | M | Med |
| ScrollMessage | `ScrollingCaptureManager.cs:111,147` | SendMessage WM_VSCROLL | DROP | Hide the option | S | High |
| Paste/keybd_event/mouse_event | no hits or callers | None | DROP | Nothing to port; `SendKeyPressText` unused | S | High |
| "Auto Clicker" | `EasterEggs/ShareXClicker/ShareXClickerGame.cs:43` | None (game upgrade) | READY | Keep | S | High |
| Tray icon and menu | `TrayIconService.cs`, `MainMenuBuilder.cs` | Avalonia only since M5 | LIMITED | Avalonia `DBusTrayIconImpl` (SNI). `Menu.Opening` never fires over DBusMenu, so a right click in a real plasmashell opened nothing (run, virtual session); it is now built up front and rebuilt on `RefreshMenus` and after each of its own clicks. Item, submenu, checkmark and Exit ran through a real plasmashell in a virtual session. Lost: Capture > Window in the tray (would be stale; the main window's menu and the ToggleTrayMenu hotkey still list windows), the toggle on After capture's Add image effects (a parent with a submenu can't also be clicked). The monitor list only updates on a rebuild | S | Med-High |
| Tray mouse actions | `TrayIconService.cs` | WindowsTrayIcon button events deleted in M5 | LIMITED | Left and double click ran through a real plasmashell, with the single click delayed by the double-click time. Middle click and scroll don't arrive: Avalonia ignores `SecondaryActivate`, so that setting is deleted. Right click is plasmashell's menu only | S | High |
| WindowsTrayIcon | Deleted in M5 | Shell_NotifyIcon | DROP | None | S | High |
| LucideTrayIcon | `LucideTrayIcon.cs`; `Helpers.GetProgressIconBytes` | Registry and metrics deleted | UNVERIFIED | Avalonia sends the largest frame, up to 128 pixels, and Plasma scales it down: logo, clock glyph and progress icon looked sharp at desktop scale 1, the logo at scale 2 (virtual session). Progress rendered at 64 rather than 16, which Plasma was scaling up. Color follows Avalonia's theme, not the panel's, so a light app on a dark panel gets a black glyph (by the code, not run) | S | Med |
| TaskbarManager | `Native/TaskbarManager.cs`; `TaskManager.cs:345,351`; `WorkerTask.cs:391`. A D-Bus monitor on the private bus saw `Update` for `application://ShareX.desktop` rise from 0 to 1 during an upload, then a hide, and nothing with the setting off. A real plasmashell in that session drew the bar on the task button; not seen on the owner's desktop | None | UNVERIFIED | `com.canonical.Unity.LauncherEntry.Update`; shows nothing without an installed `ShareX.desktop`. `Indeterminate`, `Error` and `Paused` removed: no caller used them, and the signal has no such states, so a pulsing, red or yellow bar is not available | S | Med-High |
| Toast window | `NotificationWindow.axaml:8-10`; `NotificationWindow.cs:322,335,439`; `TaskManager.cs:282`; `TaskHelpers.cs:2770` | GetCursorPos | READY | Keep under XWayland; pointer events for hover; `KWin.activeOutputName` for screen; owner confirmed on the real desktop, 2026-11-03 | S | Med |
| Native notifications (optional) | same call sites | None | SWAP | org.freedesktop.Notifications; loses placement, hover, per-button click actions | M | Med-High |
| GetCursorPos helper | `CaptureHelpers.cs:91`; callers `QuickTaskMenu.cs:94`, `ActionsToolbarWindow.axaml.cs:271` | Done in M4 | LIMITED | KWin script, `workspace.cursorPos`, converted to X11. Each call loads a script (a few ms), blocking the caller, and returns (0,0) with a log entry if KWin fails, so it must never be polled (`KWinCursorWatcher` is the way to follow the pointer): the notification's 40 ms hover check now uses `IsPointerOver`, and it looks up the cursor only once per toast duration, while it is open under a still pointer. Callers that own the pointer should use pointer events | M | Med |
| Direct GetCursorPos | `ScreenColorPickerWindow.axaml.cs:137`; `EditorView.ImageInsert.cs:422`; `MonitorTestWindow.axaml.cs:53`; `VideoPlayerWindow.axaml.cs:142` | user32. M3: replaced with `Screens.ScreenFromVisual` or pointer events; not run. No such call left in the emoji picker (M7, by the code; placing an emoji ran in a virtual session) | UNVERIFIED | Pointer events or `Screens.Primary` | S | Med-High |
| SetCursorPos, ClipCursor | `CaptureHelpers.SetCursorPosition` deleted in M4; `ClipCursor` call removed | None | LIMITED | Loses warp and confinement; arrow keys move an internal point | S | Med-High |
| CursorData (cursor in captures) | Deleted in M4 | None | LIMITED | `spectacle -p` (M4, virtual session only) | M | Med-Low |
| CursorAssetLoader, `.cur` assets | `ShareX.Avalonia/Input/CursorAssetLoader.cs:113-135`; `ShareX.Avalonia/Assets/*.cur` | None, managed parser | UNVERIFIED | Keep. M7: the crosshair showed over the editor canvas in a virtual session, captured with the pointer; open and closed hand not checked | S | Med-High |
| Annotation cursor renderer | `P/Rendering/CursorBitmapRenderer.cs` | user32 and gdi32 fallback deleted in M7 | UNVERIFIED | See Cursor annotation in 5.10 | S | Med |


### 5.7 Shell integration and app lifecycle

| Feature | Evidence | Windows dependency | Label | KDE Wayland replacement | Effort | Conf. |
|---|---|---|---|---|---|---|
| Build targets | `ShareX/ShareX.csproj:3`, `Directory.Build.props:15,19`, `ShareX.HelpersLib/ShareX.HelpersLib.csproj` | Moved off `net10.0-windows`, win RIDs, WindowsDesktop in M1 | READY | None needed | L | H |
| Start-up sequence | `ShareX/Program.cs:47-119` | None direct | UNVERIFIED | None | S | M |
| UseNls | `ShareX/ShareX.csproj:59` | NLS | DROP | ICU, sort order may differ | S | H |
| Personal folder, portable | `ShareX/Infrastructure/AppPaths.cs:40-42,105`, `PersonalPathManager.cs:42,118` | Confirmed in M2: a missing `~/Documents` made `GetFolderPath` return "" and the path relative. Now `DoNotVerify`, so it's absolute and the folder gets created. A relative `HOME` refuses to start. Case-insensitive compare remains | READY | Keep; portable needs writable app dir | S | M |
| Policy options | `ShareX/SystemOptions.cs`. `DisableUpload`, `DisableUpdateCheck`, `DisableLogging` and `PersonalPath` are all still read, the first in over a dozen places, so kept rather than dropped | Registry deleted; it always read as empty here | UNVERIFIED | `/etc/ShareX/SystemOptions.json`, same names, root's to write. The per-user fallback is gone: a user can't restrict themselves through it. A file that can't be read turns uploads and update checks off. Ran with a private overlay on `/etc`, not with a real file there | S | H |
| Autostart, shortcuts | `ShareX/StartupManager.cs`. `ShortcutHelpers`, `WshShell` deleted. The toggle wrote the entry; `systemd-xdg-autostart-generator` made a unit from it, its line ran `-silent` with no window, and it skipped a `Hidden=true` entry (virtual session only; a real login is untested). `StartupState` is now just `Enabled` and `Disabled` | None | UNVERIFIED | `$XDG_CONFIG_HOME/autostart/ShareX.desktop`, `Hidden=true` or an entry for another copy reads as off. On a first run `-silent` used to open the welcome window; it now waits for the next plain start (virtual session) | S | H |
| Context menu | `ShareX/IntegrationHelpers.cs`. In a virtual session Dolphin showed both entries, and clicking one uploaded the file through a running ShareX. The upload `Exec` line also took spaced paths, several files and a folder | None | UNVERIFIED | `$XDG_DATA_HOME/kio/servicemenus`, executable as KIO requires; Dolphin/KIO only. By the code, a selection over 100 files sent to a running ShareX is dropped with only a log line, because `SingleInstanceManager` refuses more arguments. `desktop-file-validate` rejects any `Type=Service` file, KDE's own included | M | H |
| Send To | Toggle removed in M5, along with its `.lnk` code | None | DROP | "Upload with ShareX" covers it; no separate Send To folder | S | M |
| `.sxcu`, `.sxie` | `IntegrationHelpers.Install`. `FileTypeRegistration` deleted, so running ShareX no longer registers anything by itself. `kmimetypefinder` and `gio` resolved both types, and `gio open` on a `.sxcu` reached the import prompt of a running ShareX (virtual session only; `xdg-mime query filetype` printed nothing there, even for a PNG) | None | UNVERIFIED | MIME package, one hidden `.desktop` per type because a plain `%F` would upload the file instead of importing it, `update-mime-database`, `update-desktop-database`, PNG icons. `.sxie` import not run | M | H |
| Browser manifests | `ShareX/IntegrationHelpers.cs`. The settings toggles write and remove `com.getsharex.sharex.json` in `$XDG_CONFIG_HOME/google-chrome/NativeMessagingHosts` and `chromium/NativeMessagingHosts`, and `ShareX.json` in `~/.mozilla/native-messaging-hosts`, with the ids the deleted JSON files named (run in a throwaway home). `-uninstall` removes them | None | UNVERIFIED | Absolute path to this copy of the host, so a moved copy or an AppImage whose mount path changes reads as off until toggled again. Flatpak and Snap browsers have other folders and aren't covered. No real browser has read them | M | M |
| Native messaging host | `ShareX.HelpersLib/NativeMessagingHost.cs`, `ShareX.NativeMessagingHost/Program.cs`, `TaskHelpers.HandleNativeMessagingInput`. Framing, hand-off to a running ShareX and a cold start ran with a test driver as the browser. Messages over 64 MiB are refused and replies over 1 MiB shortened to `{}`. The input file is 0600 and `CreateNew`. A URL must be http or https, or an image data URL for `UploadImage`, so `file:` is refused before any download | `CreateProcess` replaced by `setsid` from util-linux | UNVERIFIED | Same protocol. The real extensions couldn't be installed here, so their actual messages and any use of the reply aren't confirmed | S | M |
| Single instance | `ShareX.HelpersLib/SingleInstanceManager.cs`, `MutexManager.cs:43` | Done: `PipeOptions.CurrentUserOnly` on both ends, with a mutex shared across Unix sessions but not users. Another uid was refused when run from a user namespace. A second process with `-ScreenRecorder` reached the first from the same and a new session (virtual session only, not yet seen by the owner). The socket is in `$TMPDIR`: another uid holding its name first stops arguments being delivered, though none reached that uid (run). By the code, a process with a different `$TMPDIR` won't find the first | UNVERIFIED | None | M | H |
| CLI parsing | `ShareX.HelpersLib/CLI/CLIManager.cs:55-95`, `ShareX/ShareXCLIManager.cs:161` | None; relative paths resolve to app dir | READY | Resolve against caller's cwd | S | H |
| Window activation | `ShareX/Infrastructure/ApplicationCommandLine.cs:77-86` | Assumes foreground raise works | LIMITED | Forward `XDG_ACTIVATION_TOKEN`; may become an attention request | M | M |
| Restart, elevation | `ShareX/Infrastructure/ApplicationLifecycle.cs`, `ShareX.HelpersLib/Helpers/Helpers.cs:565-597` | `runas`, WindowsPrincipal | DROP | Plain restart stays; the tray's admin item is removed | S | H |
| Update check | `ShareX.HelpersLib/UpdateChecker/GitHubUpdateChecker.cs:153-186` | Windows asset names | SWAP | Match Linux assets; no match reports failure hourly | S | H |
| Installer, signature | `ShareX.HelpersLib/Presentation/UpdateChecker/DownloaderWindow.axaml.cs:137,303-312`, `AuthenticodeSignatureVerifier.cs:89` | wintrust, `/UPDATE`, `runas` | REWRITE | Package-manager notice or signed archive check; never stub as trusted | M | H |
| ShareX.Setup | Removed in M1 | Was Inno, makeappx, `C:\`, registry | REWRITE | PKGBUILD/AppImage/Flatpak installing `.desktop`, MIME, icons, manifests | L | M |
| ShareX.Steam, Store | `ShareX.Steam` removed in M1; `ShareX/Program.cs:97-99` remains | WinRT behind `#if MicrosoftStore` | DROP | Remove the Store block | S | H |
| External programs | `ShareX.HelpersLib/ExternalProgram.cs:59,67,102-107`, `ShareX/TaskHelpers.cs` `AddDefaultExternalPrograms` | Registry discovery deleted; `File.Exists` still rejects bare commands | UNVERIFIED | PATH lookup; `.desktop` discovery not built, see Actions presets | M | M |
| Open file, URL | `FileHelpers.cs:262-265`, `URLHelpers.cs:65-71` | ShellExecute | UNVERIFIED | Explicit `xdg-open` avoids running executable files | S | M |
| Open, show folder | `FileHelpers.cs:294-296,337`, `Native/NativeMethods_Helpers.cs:482-488` | Appends `\`; SHOpenFolderAndSelectItems. M3: replaced; `ShowItems`, `ShowFolders` and fallback ran in a virtual KWin session | READY | `FileManager1.ShowItems`, fallback `xdg-open` parent; repo has no D-Bus client | S | H |
| MimeTypes | `ShareX.HelpersLib/MimeTypes.cs:70` | HKCR fallback after static table | SWAP | shared-mime-info globs, or remove | S | H |
| Folder variables | `FileHelpers.cs:201-203,438-443` | `%SpecialFolder%`, `%VAR%`, imported `C:\`. M3: empty expansions no longer reach `/`. M8: import rewrites Windows paths in settings | SWAP | None | M | M |
| Filename, path rules | `FileHelpers.cs:92,211,223`, `ShareX.Tools/Tools/DirectoryIndexer/IndexerHtml.cs:55-56`, `ShareX.ImageEffectsLib/ImageEffectPathHelpers.cs:53-54` | `\`, `:\`, case-insensitive containment. M3: fixed; `FileHelpers` and containment checked with a scratch program, `IndexerHtml` not run | READY | Separator-aware helpers, platform comparer | M | H |
| Hard-coded `.exe` tools | `FFmpegCLIManager.cs:89`, `ShareX.ScreenCaptureLib/ScreenRecording/FFmpegOptions.cs:78`, `ShareX.Tools/Tools/VideoTrimmer/VideoTrimmerWindow.axaml.cs:40`, `SevenZipManager.cs:39` | `.exe` gate makes FFmpeg `Run` return false. M3: FFmpeg from PATH, ran; `SevenZipManager` deleted | READY | PATH resolution; remove gate | S | H |


### 5.8 Clipboard, printing, dialogs, windowing

| Feature | Evidence | Windows dependency | Label | KDE Wayland replacement | Effort | Confidence |
|---|---|---|---|---|---|---|
| Build target | `ShareX.HelpersLib/ShareX.HelpersLib.csproj:3`; `Directory.Build.props:15,19` | Moved off `net10.0-windows`, WindowsDesktop.App, win RIDs in M1 | READY | None needed | M | High |
| Backend selection | `AvaloniaBootstrapper.cs:164` | None | UNVERIFIED | Keep X11 now; trial `UseWaylandWithFallback` | S | High |
| Text and file clipboard | `ShareX.HelpersLib/Helpers/AvaloniaClipboard.cs:55` | None | UNVERIFIED | Avalonia offers only `text/uri-list`; add `x-special/gnome-copied-files` for GNOME apps | S | Medium |
| Image copy | `AvaloniaClipboard.cs:75,86`; `WindowsClipboard.cs:37` (a comment, not a WinForms reference) | Win32 clipboard, Windows branch only | SWAP | Existing PNG path, `image/png` bytes only: `DataFormat.Bitmap` on the X11 clipboard lists `image/png` a second time, which Electron on Wayland pastes as two files. No `text/html`: it only names the file, and Electron's default paste then inserts a broken image in place of the picture, so `UseAlternativeClipboardCopyImage` does nothing on Linux. Its checkbox is gone from Application settings; the stored value stays, so imported config still loads | S | Medium |
| Background and exit persistence | `ShareX.Avalonia/Integration/DesktopServices.cs:52`; `WaylandDataDevice.cs:61-83`; `X11ClipboardImpl.cs:54-56` | None | LIMITED | `wl-copy` fallback; loses multi-MIME offers (one `-t` per run) | M | Medium |
| `ClipboardFormat`, `ClipboardDataFormats` | `ShareX.HelpersLib/ClipboardFormat.cs:28`; `ClipboardData.cs:41` | Windows-style names only | READY | None | S | High |
| Printing | `ShareX.HelpersLib/Printer/PrintHelper.cs`; `PortalPrint.cs` | Windows printing deleted in M7, with `PrintTextHelper` (text printing had no caller) | UNVERIFIED | Skia PDF on the paper chosen in the dialog, image at its own resolution, to portal Print. Portal over `lp`: `lp` is not on this host, and there would be no dialog to choose a printer. Virtual session: real portal and KDE backend wrote the PDF through its Print to File; a fake portal checked the calls and the PDF (page size, image, orientation, cancel, failure). A real printer is the owner's check, and needs `cups`: the KDE backend runs `lpr` | L | Medium |
| Page transfer, print dialog | `PortalPrint.cs`; `WindowsPrintInterop.cs` and `WindowsPrintDialog.cs` deleted in M7 | None | LIMITED | Portal `PreparePrint`/`Print`. Lost: printing without a dialog and the default printer override, because the portal always asks; both settings are gone. Landscape: the page is sized landscape, and whether CUPS then turns it again is not known until a real print | M | Medium |
| File dialogs | `ShareX.HelpersLib/Helpers/FileDialogHelpers.cs:41,56,74`; `X11Window.cs:290-300` | None | UNVERIFIED | Avalonia's portal FileChooser (default); no custom code | S | High |
| `DesktopServices` | `DesktopServices.cs:52` | None | UNVERIFIED | Test hidden service window | S | Medium |
| Screen colour picker | `ShareX.Avalonia/Windows/ScreenColorPickerWindow.axaml.cs` | Done in M7; 10 P/Invokes deleted | LIMITED | Frozen `spectacle` screenshot rather than portal `PickColor`, which would lose the magnifier, hover preview and position. See 5.1 | M | High |
| Colour picker dialog | `ShareX.HelpersLib/Presentation/ColorPicker/ColorPickerWindowIntegration.cs:64,102` | Unguarded `ReleaseCapture`. M3: calls removed; not run | UNVERIFIED | Guard or remove | S | High |
| Dark mode, accent | `ShareX.Avalonia/Theming/ThemeManager.cs:123-124` | None | UNVERIFIED | Avalonia reads the portal keys (`DBusPlatformSettings.cs:48,67`) | S | High |
| Tray theme | `ShareX.HelpersLib/Helpers/LucideTrayIcon.cs:46,110` | Registry | SWAP | `PlatformSettings` | S | Medium |
| DWM, uxtheme, Mica | `ShareX.HelpersLib/Native/NativeMethods.cs:476-512,763`; `DWMManager.cs:30` | DWM. M3: `DWMManager` and uncalled externs deleted; externs with callers remain | DROP | None; no Mica hits and no theming callers found | S | Medium |
| Custom title bar | `ShareX/Presentation/MainWindow/MainWindow.axaml:13-14`; `WindowImpl.cs:398` | None | SWAP | Height hint is a no-op; server decorations may double the bar. Use `WindowDecorations="None"` or `ForceDrawnDecorations` | M | Medium |
| Placement restore | `ShareX.HistoryLib/Views/HistoryWindow.axaml.cs:190`; `ImageHistoryWindow.axaml.cs:162`; `ShareX/Presentation/ActionsToolbar/ActionsToolbarWindow.axaml.cs:264` | None | LIMITED | Expected on X11; native loses it | S | Medium |
| Topmost windows | `ScreenColorPickerWindow.axaml:7`; `ShareX.Tools/Tools/PinToScreen/PinToScreenWindow.axaml.cs:109`; `ShareX.Tools/Tools/Ruler/RulerWindow.axaml` | None | LIMITED | X11 atom ran under XWayland in M7, above other windows and panels; native needs KWin rule or layer-shell helper | M | Medium |
| Transparency | `ScreenColorPickerWindow.axaml:8`; `ShareX.ScreenCaptureLib/Presentation/ScreenRecording/ScreenRecordWindow.axaml:20` | None | UNVERIFIED | X11 hint; native `SetTransparencyLevelHint` is a no-op | S | Medium |
| Click-through, shaped regions | `ScrollingCaptureRegionWindow` lost both in M2; `ScreenRecordWindow` GDI region removed in M6 | `WS_EX_TRANSPARENT`, GDI regions | REWRITE | XShape input region on the XID; no Avalonia native API | M | Medium |
| Tool-window style, Z-order | `ScreenRecordWindow` style call removed in M6; `ScreenRecorderBarWindow` lost both in M2 | `WS_EX_TOOLWINDOW`, `SetWindowPos` | SWAP | `ShowInTaskbar=False`, owned windows; native taskbar hint is a no-op (`WindowImpl.cs:285`) | S | Medium |
| Exclude from capture | Removed from `ScreenRecordWindow` in M6 | `SetWindowDisplayAffinity` | LIMITED | No known equivalent; controls may appear in recordings | M | Low |
| Taskbar flash | `ShareX.HelpersLib/Presentation/UpdateChecker/UpdateMessageWindow.axaml.cs:161` | `FlashWindowEx` call removed in M2, so a background update prompt only minimizes and no longer flashes | SWAP | X11 urgency hint or tray notification | S | Medium |
| Cursor lock, HWND move | `ShareX.HelpersLib/Helpers/Helpers.cs:919,937` | `ClipCursor`, `SetWindowPos` | LIMITED | Guarded no-ops; cursor confinement lost | S | Medium |
| Foreign-window tools | `ShareX.Tools/Tools/InspectWindow/InspectWindowViewModel.cs`; `ShareX.Tools/Tools/BorderlessWindow/BorderlessWindowManager.cs`; `ShareX.ScreenCaptureLib/Presentation/RegionCapture/RegionCaptureWindow.axaml.cs:519` | HWND enumeration and control deleted from both tools in M7 | LIMITED | KWin scripts, see 5.1. Region capture needs its own window-detection plan | L | Medium |
| Mouse highlighter overlay | `ShareX.Tools/Tools/MouseHighlighter/MouseHighlighterOverlayWindow.cs` | Done in M7, see 5.6 | LIMITED | Follows the pointer through a KWin script. Lost: click ripple, per-button colours, release crosshairs and fade, because Wayland shows no clicks without the `input` group | M | Med |


### 5.9 ShareX.Tools

"Fix A" = folder opening in `FileHelpers.cs:337`. "Fix B" = the `.exe` check in `FFmpegCLIManager.cs:89`.

| Tool | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort | Confidence |
|---|---|---|---|---|---|---|
| AnalyzeImage | `ShareX.Tools/Tools/AnalyzeImage/AIOptions.cs:44` | API keys live in `ApplicationConfig.json` and `HotkeysConfig.json`, which never encrypted before M2. Both do now; save and reload run. The tool itself not run | UNVERIFIED | Secret Service | S | M |
| AnimatedGifMaker | `ShareX.Tools/Tools/AnimatedGifMaker/AnimatedGifMakerViewModel.cs:239` | Fix A | SWAP | FileManager1 | S | H |
| AnimatedGifTrimmer | `ShareX.Tools/Tools/AnimatedGifTrimmer/AnimatedGifTrimmerViewModel.cs:314` | Fix A | SWAP | FileManager1 | S | H |
| BackgroundRemover | `ShareX.Tools/Tools/BackgroundRemover/BackgroundRemovalService.cs` | DirectML, DXGI removed | UNVERIFIED | ONNX Runtime 1.23.2 on the CPU only | S | M |
| BorderlessWindow | `ShareX.Tools/Tools/BorderlessWindow/BorderlessWindowManager.cs` | Done in M7 | LIMITED | KWin script, see 5.1. Lost: direct control, and a program can tell it's full screen | M | M |
| ClipboardViewer | `ShareX.Tools/Tools/ClipboardViewer/ClipboardViewerData.cs:93` | DIB branches only | UNVERIFIED | Avalonia clipboard; MIME names | S | M |
| DirectoryIndexer | `ShareX.Tools/Tools/DirectoryIndexer/Indexer.cs:78` | None | READY | None needed | S | H |
| HashChecker | `ShareX/TaskHelpers.cs:964` | None in tool; handler unaudited | UNVERIFIED | None needed | S | M |
| IconConverter | `ShareX.Tools/Tools/IconConverter/IconConverterWindow.axaml.cs:80` | None (ICO is only the output format) | READY | None needed | S | H |
| ImageCombiner | `ShareX/TaskHelpers.cs:1045` | None in tool; services unaudited | UNVERIFIED | None needed | S | M |
| ImageComparer | `ShareX.Tools/Tools/ImageComparer/ImageComparisonService.cs:79` | None (Skia) | READY | None needed | S | H |
| ImageConverter | `ShareX.Tools/Tools/ImageConverter/ImageConverterViewModel.cs:277` | Fix A | SWAP | FileManager1 | S | H |
| ImageResizer | `ShareX.Tools/Tools/ImageResizer/ImageResizerViewModel.cs:214` | Fix A. M3: Fix A done; ran end to end in a virtual KWin session | READY | FileManager1 | S | H |
| ImageSplitter | `ShareX.Tools/Tools/ImageSplitter/ImageSplitterViewModel.cs:117` | Fix A | SWAP | FileManager1 | S | H |
| ImageThumbnailer | `ShareX.Tools/Tools/ImageThumbnailer/ImageThumbnailerViewModel.cs:162` | Fix A | SWAP | FileManager1 | S | H |
| ImageWatermark | `ShareX.Tools/Tools/ImageWatermark/ImageWatermarkViewModel.cs:272` | Fix A | SWAP | FileManager1 | S | H |
| InspectWindow | `ShareX.Tools/Tools/InspectWindow/InspectWindowService.cs` | Done in M7 | LIMITED | KWin `queryWindowInfo` and scripts, see 5.1. Lost: child controls, Win32 styles, icons | L | M |
| Metadata | `ShareX.Tools/Tools/Metadata/MetadataViewModel.cs:223` | None (managed parsers) | READY | None needed | S | H |
| MonitorTest | `ShareX.Tools/Tools/MonitorTest/MonitorTestWindow.axaml.cs:53` | `GetCursorPos`, uncaught. M3: uses `Screens`; not run | UNVERIFIED | `Screens` API | S | H |
| MouseHighlighter | `ShareX.Tools/Tools/MouseHighlighter/MouseHighlighterService.cs`; `ShareX.HelpersLib/Helpers/KWinCursorWatcher.cs`; `ShareX.Tools/Tools/MouseHighlighter/MouseHighlighterOverlayWindow.cs` | Done in M7, see 5.6 | LIMITED | Lost: click ripple, per-button colours, release crosshairs and fade, because Wayland shows no clicks without the `input` group. Their options are gone from the settings and from `MouseHighlighterOptions`. A saved transparent colour becomes the default colour, since it would now draw nothing | M | Med |
| NetworkMonitor | `ShareX.Tools/Tools/NetworkMonitor/NetworkMonitorProbe.cs:83` | None | READY | None needed | S | H |
| OCR | `ShareX.Tools/Tools/OCR/OCRHelper.cs` | Done in M7: was `Windows.Media.Ocr` | UNVERIFIED | Host `tesseract` CLI. Saved Windows tags map to Tesseract codes (`en` to `eng`), except Chinese. Single line joins lines, as on Windows. Search and translate services stay: they only open a browser when clicked. The help button now opens Tesseract's documentation, as the ShareX page describes Windows language packs. Run in a virtual session only | S | H |
| PinToScreen | `ShareX.Tools/Tools/PinToScreen/PinToScreenWindow.axaml.cs` | Done in M7 | LIMITED | XWayland `Topmost`, plus a KWin script to place a pin from a region; no KWin rule or layer-shell. See 5.1 | S | M |
| QRCode | `ShareX/TaskHelpers.cs:1792` | Screen capture in main app, through `SpectacleCapture` since M4 | UNVERIFIED | Spectacle for both scan modes. M7: capture and decoding left the UI thread, a capture failure shows an error box, and the window now shows its status line, which nothing displayed before, so "No QR code was found." was never seen. Virtual session only | S | M |
| RemoteStorageBrowser | `ShareX/TaskHelpers.cs:1724` | None in tool; providers unaudited | UNVERIFIED | None needed | S | M |
| Ruler | `ShareX.Tools/Tools/Ruler/ScreenPixelBuffer.cs` | Done in M7; BitBlt deleted | LIMITED | `spectacle` screenshot; one window spanning all monitors rather than one per output, as it ran correctly. See 5.1 | M | M |
| VideoConverter | `ShareX/TaskHelpers.cs:1212` | Fix B. M3: Fix B done; ran end to end in a virtual KWin session. GIF two-run palette not yet run through the window | READY | System FFmpeg; hide AMF/QSV | S | H |
| VideoPlayer | `ShareX.Tools/Controls/Media/MpvPlayer.cs` | Done in M7: Media Foundation and the child HWND deleted; design note in section 4. Virtual session on software decoding: frames change on play, stop on pause, move on seek and frame step, resize, fullscreen, click on the video, keys, play again from the end. Not heard: sound, volume. Not run: real desktop | UNVERIFIED | Host `libmpv.so.2`, child X11 window | M | M |
| VideoThumbnailer | `ShareX.Tools/Tools/VideoThumbnailer/VideoThumbnailer.cs:53` | Fix B makes duration 0, silent null; Fix A. M3: ran end to end in a virtual KWin session, including a `"` in the file name | READY | FFmpeg; ffmpegthumbnailer unnecessary | S | H |
| VideoTrimmer | `ShareX.Tools/Tools/VideoTrimmer/VideoTrimmerViewModel.cs:61`, `:180` | Done in M7: player above, filmstrip from one FFmpeg run per frame, Fix A already in. Virtual session: lossless and re-encode trims of a 2 to 5 s selection lasted 3.02 s in `ffprobe`, source unchanged, through the real save dialog. Show output button not run | UNVERIFIED | Same player; FFmpeg frames | M | M |


### 5.10 Editors and image effects

Paths: `IE/` = `ShareX.ImageEditor`, `IE.App/` = `ShareX.ImageEditor.App`, `PE/` = `ShareX.PhotoEditor`, `FX/` = `ShareX.ImageEffectsLib`, `HL/` = `ShareX.HelpersLib`, `P/` = `ShareX.ImageEditor/Presentation`.

| Feature | Evidence (file:line) | Windows dependency | Label | KDE Wayland replacement | Effort | Confidence |
|---|---|---|---|---|---|---|
| Build props | `Directory.Build.props:15` | Renamed and x64 maps to `linux-x64` in M1 | READY | None needed | S | High |
| HL dependency (blocks PE, FX) | `HL/ShareX.HelpersLib.csproj:3,14` | `net10.0-windows`, WindowsDesktop.App | SWAP | Move Skia, font and clipboard helpers to a `net10.0` library | M | Med |
| PE target | `PE/ShareX.PhotoEditor.csproj:3,20` | HL only | SWAP | Retarget `net10.0` | S | High |
| FX target | `FX/ShareX.ImageEffectsLib.csproj:3`; `FX/Drawings/DrawText.cs:135` | HL only | READY | Retarget after HL; drop unused `UseSystemDrawing` | S | High |
| Annotation tools (shapes, arrow, freehand, text, balloon, step, image, clone, blur, pixelate, highlight, magnify, spotlight, smart eraser) | `IE/Core/Editor/EditorTool.cs:31` | None found | READY | None | S | High |
| Crop, cut-out, undo | `IE/Core/Annotations/Shapes/CropAnnotation.cs:34`; `IE/Core/History/EditorHistory.cs` | None found | READY | None | S | High |
| Image effects (235 files) | `P/Effects/ImageEffectCatalog.cs:32` | Font names only | READY | See Fonts | S | High |
| PE layers and tools | `PE/Core/Compositor.cs:32`; `PE/Tools/ToolManager.cs:55` | HL only | READY | None | S | Med |
| Emoji | `P/Emoji/EmojiBitmapRenderer.cs` | D2D and Vortice deleted from the editor in M7 | UNVERIFIED | Fontconfig's `emoji` family (`noto-fonts-emoji`), shaped with HarfBuzz and drawn by Skia. Colour in a virtual session: the picker, ZWJ flags such as the rainbow flag, and a sticker on the canvas. An emoji the font lacks draws nothing rather than a box (by the code, not run). With no emoji font, the picker names the package. Not run with `~/.local/share/fonts/ms-fonts` visible, where `fc-match emoji` still gives Noto | M | Med |
| Cursor annotation | `P/Rendering/CursorBitmapRenderer.cs`; `P/Controllers/EditorInputController.cs:379` | Win32 deleted in M7 | UNVERIFIED | 13 types from the bundled `.cur` assets. The 15 stock types were Windows' own images with no asset, so they come from the host's Xcursor theme: `XCURSOR_THEME`, then `breeze_cursors`, then `Adwaita`. Default and a bundled type drawn on the canvas, and 22 of the 28 previews seen, in a virtual session; the last rows of the list were off screen | M | High |
| Cursor-relative placement | `P/Views/EditorView.ImageInsert.cs:242,396,422`; `P/Views/EditorView.axaml.cs:1704` | Unguarded `GetCursorPos` throws `DllNotFoundException`; paste fails, emoji insert may crash. M3: uses the screen holding the window; not run | UNVERIFIED | Last pointer event plus Avalonia `Screens` | S | High |
| Screen colour picker | `P/Controls/ColorPickerPanel.axaml.cs:117`; `ShareX.Avalonia/Windows/ScreenColorPickerWindow.axaml.cs` | Done in M7; GDI capture deleted | LIMITED | Frozen `spectacle` screenshot, see 5.1. Opening it from here not run | M | M |
| Wallpaper background | `IE/Integration/LinuxDesktopWallpaperService.cs` `TryGetKdeWallpaper`; `P/ViewModels/MainViewModel.BackgroundState.cs:257` | None | UNVERIFIED | Reads `plasma-org.kde.plasma.desktop-appletsrc` only. Ran in a virtual session with a throwaway config and synthetic images, never a real wallpaper: a plain image and a package folder drew behind the editor's image, with the package's largest file picked, and no wallpaper set still runs. Not handled: a second monitor with its own wallpaper, as the first containment wins; a package's `images_dark` under a dark theme; a default wallpaper never written to the config. `org.kde.PlasmaShell.wallpaper(u)` would answer the first and third | S | High |
| Fonts | `P/ViewModels/MainViewModel.ToolOptions.cs:1281`; `PE/Tools/TypeTools.cs:39`; `HL/FontSafe.cs:28`; `IE/Core/Annotations/Text/TextAnnotation.cs:54`; `IE/Core/ImageEffects/Filters/ASCIIArtImageEffect.cs:79` | Defaults Segoe and Consolas; enumeration is portable | SWAP | fontconfig aliases (sans, monospace) | S | Med |
| PNG, JPEG, WebP, GIF, BMP, ICO | `IE.App/App.axaml.cs:41`; `P/Views/EditorView.axaml.cs:2345`; `HL/Helpers/SkiaImageHelpers.cs:285` | None; WIC appears only in emoji | UNVERIFIED | Skia. `IE/Core/ImageEffects/Helpers/ImageHelpers.cs:168-170` bypasses HL's GIF/BMP encoders; likely cross-platform failure | S | Med |
| TIFF | `PE/Core/ImageIO.cs` | GDI+ deleted in M7 | UNVERIFIED | Host `magick` (package `imagemagick`, not a NuGet library), PNG through pipes with zip compression. SkiaSharp has no TIFF decoder here. First page only. Open ran in a virtual session; save only in `PhotoEditorImageIOTests`. Without `magick`, a message names the package | M | High |
| HEIC, HEIF, AVIF (PE open only) | `PE/Core/ImageIO.cs` | None | UNVERIFIED | Host `magick`, whose build here has HEIC and AVIF through libheif; SkiaSharp has neither. The file goes in on stdin with the coder named, under limits on memory, area and time, because files are untrusted. Damaged files, a TIFF header claiming 60000 x 60000 and an MVG script named `.tiff` all gave the normal message (virtual session). Synthetic files only; no real phone photo was tried | M | Med |
| Skia natives | `~/.nuget/packages/avalonia.skia/12.1.4/avalonia.skia.nuspec` | None | READY | `SkiaSharp.NativeAssets.Linux` 3.119.4, HarfBuzz 8.3.1.3 | S | High |
| GPU, shaders | `P/EasterEggs/ShaderEasterEggOverlay.cs:194,262` | None; SkSL easter egg only, no `GRContext` or D3D | READY | None | S | High |
| Paste, copy | `P/Views/EditorView.ClipboardHandler.cs:127,217`; `P/Views/EditorView.axaml.cs:2269`; `PE/UI/Commands/EditorCommands.File.cs:121` | None; paste blocked by the placement row | UNVERIFIED | Avalonia X11 clipboard via XWayland. Reads `image/png` first, as ShareX's own copy has no Bitmap format. The view's own copy writes `image/png` bytes too: it runs where no host handler is set, which is Ctrl+C in the region overlay and `ShareX.ImageEditor.App`, and a Bitmap there crashed ShareX after an in-process read. Virtual session: Ctrl+V and Insert in the center ran for ShareX's own copy and another client's PNG and JPEG; copy ran in the editor window, the overlay and the standalone app, then an in-process read and an outside `image/png` request, alpha kept | S | Med |
| Drag-in | `P/Views/EditorView.axaml.cs:193,1749`; `PE/UI/PhotoEditorWindow.cs:213`; `FX/Presentation/ImageEffectsWindow.axaml.cs:57` | None | UNVERIFIED | Avalonia XDND via XWayland | S | Med |
| File dialogs | `P/Views/EditorView.axaml.cs:2321`; `PE/UI/PhotoEditorWindow.Lifetime.cs:47` | None | UNVERIFIED | Avalonia StorageProvider | S | Med |
| Host, windowing | `IE.App/Program.cs:44`; `PE/UI/PhotoEditorWindow.Lifetime.cs:129-132` | None | LIMITED | XWayland client: no native Wayland surface; position restore relies on X11 semantics | S | Med |

