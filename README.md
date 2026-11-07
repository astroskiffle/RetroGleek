# RetroGleek

A small, fast, **fully offline** image browser and viewer for Windows 10/11, with built-in support for
**retro-computer picture formats** (Amiga, Atari, Commodore 64, ZX Spectrum, MSX, Amstrad CPC and hundreds more)
through [RECOIL](https://github.com/pfusik/recoil).

It builds into **one small `.exe`** using the C# compiler that already ships with Windows: no SDK, no installer,
no NuGet packages and no internet connection.

## Features

- **Folder tree + thumbnail grid.** The tree is lazy-loaded; folders are scanned and thumbnails are decoded on
  background threads, so large folders stay responsive.
- **Full-canvas viewer.** Zoom, pan, fit / 100 %, rotate, fullscreen, and keyboard / mouse-wheel navigation through
  the folder.
- **Formats.** JPEG, PNG, GIF, BMP and TIFF (Windows GDI+), plus 567 retro file extensions (RECOIL).
- **Save as PNG (lossless).** One picture or a whole selection; every PNG is read back and compared pixel by pixel
  before it is kept.
- **File management.** Rename, copy / paste, delete to the Recycle Bin, new folder.
- Dark interface, high-DPI aware.

## Privacy and security

- **No network code at all.** No sockets, HTTP, telemetry, update checks, licensing or cloud APIs. The program uses
  only the .NET Framework that ships with Windows (`System`, `System.Drawing`, `System.Windows.Forms`,
  `Microsoft.VisualBasic` for Recycle Bin deletes) plus the bundled `RECOIL.cs`, which is pure C# with no `unsafe`
  or native code. You can check this yourself: the whole program is two source files.
- **Writes files only when you ask** (save, copy / paste, rename, delete, new folder). No settings files, no registry,
  nothing is remembered between runs.
- **Cloud placeholder files are never opened.** Files that OneDrive and similar services keep online-only are shown
  as "cloud-only" tiles, so browsing cannot trigger a download. Network drives are not listed in the tree.
- **Retro decoders are managed, bounds-checked code** with hard limits (32 MB per file, 16 million pixels per
  picture). Any decoding error is caught and shown as a message instead of crashing the viewer.
- The five standard formats are decoded by Windows' own GDI+, which Windows Update keeps patched.

## Build

Requirements: Windows 10 or 11 (which includes .NET Framework 4.x and its C# compiler).

1. Keep `Program.cs`, `RECOIL.cs`, `RetroGleek.manifest` and `build.bat` together in one folder.
2. Double-click **`build.bat`**. It produces `RetroGleek.exe` next to them.

The source is written in C# 5 so that the compiler built into Windows can build it.

Run it with `RetroGleek.exe`, optionally followed by a folder or an image path, e.g. `RetroGleek.exe C:\Pictures`.
You can also drag a file or folder onto the window, or use **Ctrl+O**.

## Using it

**Browser (folder tree and thumbnails)**

| Action | Keys / mouse |
|---|---|
| Open a picture in the viewer | Double-click or Enter |
| Move / select | Arrows, Home / End, PgUp / PgDn; Shift / Ctrl for multi-select; Ctrl+A |
| Scroll / change thumbnail size | Mouse wheel / Ctrl + wheel |
| Rename, Copy, Paste, Delete (Recycle Bin), Refresh | F2, Ctrl+C, Ctrl+V, Del, F5 (or right-click) |
| Save selection as lossless PNG | Ctrl+S (or right-click) |
| Show / hide retro-computer formats | F8 (or right-click) |

**Viewer**

| Action | Keys / mouse |
|---|---|
| Previous / next picture | Left / Right, PgUp / PgDn, Space / Backspace, mouse wheel |
| First / last picture | Home / End |
| Zoom | Ctrl + wheel, `+` / `-` |
| Pan | Click and drag |
| Fit / actual size | `F` or `0` / `1`; double-click toggles |
| Rotate clockwise / counter-clockwise | `R` / `L` |
| Info bar | `I` |
| Save current picture as lossless PNG | Ctrl+S |
| Back to the browser / leave fullscreen | Esc |

Everywhere: **F11** fullscreen, **Ctrl+O** open, **F1** about / credits.

## Retro-computer formats (RECOIL)

- RECOIL supports 567 file extensions. They are **shown by default**; press **F8**, use the right-click menu, or start
  with `--no-retro` to hide them.
- Opening a retro picture directly turns them on for that folder. The viewer shows the platform name (for example
  "ZX Spectrum") in the info bar.
- RECOIL itself marks some extensions as too generic (`.cs`, `.obj`, `.map`, `.raw`, `.img` ...) or made-up. Those
  are never listed automatically, but still open when you open the file directly. `.scr` (ZX Spectrum) stays
  listed; `.fnt` and `.3` are excluded.
- Pictures that flicker between sub-pictures on the original hardware are blended into one image.
- Some formats keep their palette in a companion file; those are read only from the picture's own folder.
- `RECOIL.cs` is a **modified, machine-generated copy** of RECOIL's C# output (C# 5 syntax, lower memory caps). See
  [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). Regenerate it with `tools/downgrade_recoil.py`.

## Why WebP is not supported

RetroGleek sticks to the formats Windows' built-in GDI+ decoder handles (JPEG, PNG, GIF, TIFF, BMP). WebP was
considered and left out for these reasons:

* **GDI+ cannot decode WebP.** RetroGleek uses `System.Drawing`, a thin wrapper over the GDI+ component that ships
  with Windows. GDI+ has no WebP decoder, so supporting WebP would mean adding a second one.
* **The usual way to add one conflicts with the single-executable design.** Bundling `libwebp` means shipping an
  extra native DLL or unpacking one at runtime. That breaks the one-file, standalone goal, and unpacking files at
  runtime can raise antivirus false-positive scores.
* **Security and patch management.** Image decoders parse untrusted input and are high-value exploit targets.
  `libwebp` had a heap buffer overflow in 2023 ([CVE-2023-4863](https://nvd.nist.gov/vuln/detail/CVE-2023-4863);
  rated Critical by Chromium, CVSS 8.8 High in NVD) that was exploited in the wild and fixed in libwebp 1.3.2. A DLL
  bundled with an app is not serviced by Windows Update: it stays at the version shipped until the maintainer tracks
  upstream advisories, rebuilds and publishes a release, and users install it.
* **Native code either way, but a different patch channel.** RetroGleek's own code is C# (memory-safe), but GDI+ is
  native code too and has had serious parsing bugs, from
  [MS04-028](https://docs.microsoft.com/en-us/security-updates/securitybulletins/2004/ms04-028) (2004) to
  [CVE-2025-53766](https://nvd.nist.gov/vuln/detail/CVE-2025-53766) (2025). The difference is servicing: GDI+ is
  patched through regular Windows updates; a bundled `libwebp` is not.
* **Alternatives were also considered.** Using a WebP codec installed on the PC (via the Windows Imaging Component)
  depends on a separately installed native codec that behaves differently across machines. A pure managed decoder
  would avoid native code, but would be a substantial new component to write, test and maintain.

Other modern codecs (AVIF, HEIC, JPEG XL) are left out for the same reasons.

## Antivirus note

RetroGleek is an unsigned executable with no reputation yet, and it does the kinds of things file managers do
(enumerates drives, copies, renames and deletes files). A few heuristic / machine-learning scanners may therefore flag
a freshly built copy as suspicious. Because the program is two readable source files, the safest approach is to read
the source and build it yourself with `build.bat`.

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | The application |
| `RECOIL.cs` | Bundled, modified C# build of RECOIL (retro formats) |
| `RetroGleek.manifest` | Windows manifest: high-DPI awareness, Windows 10/11 compatibility |
| `build.bat` | One-click build using the compiler included in Windows |
| `THIRD-PARTY-NOTICES.txt` | RECOIL's MIT license and a description of the modifications |
| `tools/downgrade_recoil.py` | Regenerates `RECOIL.cs` from a RECOIL checkout (needs the `fut` translator) |

## Credits

Retro-computer picture decoding by **[RECOIL](https://github.com/pfusik/recoil)** by Piotr Fusik, used under the MIT
License. See `THIRD-PARTY-NOTICES.txt`.
