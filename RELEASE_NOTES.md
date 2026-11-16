# RetroGleek release notes

## 1.2.0 - modern formats (November 2026)

### New formats, through the decoders installed in Windows
- **WebP, AVIF, HEIC / HEIF, JPEG XL, JPEG XR and camera RAW** (DNG, CR2, CR3, NEF, ARW, ORF, RW2, RAF, PEF, SRW and
  more) open in the viewer, thumbnails, Details list, preview pane, contact sheets, Save as PNG and the folder audit.
- RetroGleek bundles no decoder of its own and adds no native code: it uses the Windows Imaging Component through
  WPF's imaging classes in the .NET Framework, so Microsoft keeps the decoders patched through Windows Update and the
  Microsoft Store.
- If a PC has no decoder for a format, the picture says which free extension to install (WebP Image Extensions,
  AV1 Video Extension, HEIF + HEVC Video Extensions, JPEG XL Image Extension, Raw Image Extension). RetroGleek never
  opens the Store or goes online itself.
- Star ratings stored in these files are shown too, and count for the Show filter and for sorting.
- The viewer's **EXIF details (E)** work for these formats too: camera, lens, date taken, exposure, aperture, ISO,
  focal length, flash, GPS and so on, whenever the file holds them and Windows' decoder can read them.

### Convert to a retro format
- **Tools > Convert to a retro format (Ctrl+Shift+E)** turns the selected pictures, or the picture in the viewer, into
  new files in a retro-computer format: **ZX Spectrum screen** (`.scr`), **C64 Koala Painter** (`.koa`, 160 x 200
  multicolour), **Atari ST Degas** (`.pi1`, 16 of 512 colours), **Amiga IFF** (`.iff`, 320 x 256, 32 of 4096
  colours), **MSX Screen 2** (`.sc2`, 2 colours per 8 x 1 strip) or **MSX2 Screen 8** (`.sc8`, 256 fixed colours). Whole picture or fill the screen; dithering none / ordered pattern / error diffusion; a live preview of
  the first picture.
- The originals are never changed and nothing is overwritten. Every new file is checked by decoding it with RECOIL
  (pixel for pixel), and each machine's colours come from RECOIL, so the viewer shows exactly the preview.

### Animated WebP
- **Animated WebPs play in the viewer**, with their own frame times; `P` pauses, `,` / `.` step a frame, and
  **Ctrl+Shift+S** saves every frame as a verified lossless PNG. RetroGleek reads the frame layout from the file and
  has Windows' WebP decoder decode each frame (still no bundled decoder), then puts the frames together the way
  browsers do. Animations over about 384 MB of frames show their first frame.

### Faster with big photos
- Thumbnails, the preview pane and contact sheets of large JPEG photos are decoded straight at a half, a quarter or an
  eighth of the full size by Windows' JPEG decoder, instead of decoding every pixel and scaling down.

### Ratings other programs can see
- **Edit > Rating > Write my ratings into the files (JPEG, PNG)** copies your RetroGleek star rating into the selected
  JPEG and PNG files (XMP `xmp:Rating`, plus the Windows EXIF rating tags if the file already has them), so Windows
  Explorer, Lightroom and others see it. In a PNG the XMP goes into the standard `iTXt` chunk before the picture data.
  Always asks first. Only the information block changes: the picture data (and every other PNG chunk) is copied byte
  for byte and every other XMP property is kept. Each new file is checked before it replaces the old one.

### Marks survive Explorer
- Pictures moved outside RetroGleek (in Explorer, say), or whose folder was moved or renamed, keep their ratings,
  labels and tags: a picture with no mark takes the mark of a file with the same name, size and date that is no
  longer where it was. A copy never takes a mark, and nothing is taken from a drive that is unplugged.
- A picture **renamed in Explorer** keeps its marks too, when that is certain: exactly one marked picture has gone from
  the same folder, and the renamed picture is the only file there with its size and date.
- The command-line audit still never writes `marks.tsv`.

### Cloud-only files stay in the cloud
- Copying a folder leaves out the cloud-only files and folders inside it (OneDrive and similar), so a copy never
  starts a download; a message says how many were left out.
- Moving a folder that holds cloud-only files to another drive (which Windows does by copying, downloading them first)
  now asks before it starts: move anyway, leave that folder where it is, or cancel. Moves on the same drive download
  nothing and don't ask.

### Under the hood
- New source file `WinCodecs.cs`. `build.bat` now also references `System.Xml`, `System.Xaml`, `WindowsBase` and
  `PresentationCore` (all part of the .NET Framework in Windows).
- Still exactly three native imports of RetroGleek's own, no network code, no telemetry and no `unsafe` code.

### Known limitations
- Thumbnails, the preview pane, contact sheets, Save as PNG and Copy picture show an animated WebP's first frame.
- What opens depends on the decoders installed on each PC; HEIC needs the HEVC Video Extensions, which Microsoft
  sells for a small fee unless the PC came with them.
- Ratings are written into JPEG and PNG files only (not TIFF or the Windows-codec formats). Files with extended or
  compressed XMP, an XMP block that can't be read, or a damaged PNG chunk are left alone, with the reason.
- A picture renamed in Explorer loses its marks when RetroGleek can't be sure which file it was (for example, two
  marked pictures with the same size and date were renamed in the same folder).

## 1.1.0 - culling and photo tools (November 2026)

This release adds the "low-hanging fruit" from ACDSee and Photo Mechanic that suit a fast, minimal viewer: rating and
culling, quicker filing, better views of the folder, and two careful ways of working on photos. RetroGleek is still
not an image editor: the only change it ever makes to a picture's contents is the lossless JPEG turn, and only after
you confirm it.

### Rate, label and tag
- **Star ratings (Alt+1 .. Alt+5, Alt+0 removes), colour labels (Shift+1 .. Shift+5: red, yellow, green, blue,
  purple; Shift+0 removes) and a tag (X)**, in the browser and in the viewer (also during a slideshow), or from
  Edit > Rating / Colour label / Tag.
- Marks show on the thumbnails, in new **Rating** and **Label** columns of the Details list, and in the viewer's info
  bar.
- Kept in `marks.tsv` next to `settings.ini`, never inside the pictures, so they work for every format including
  retro ones. They follow pictures you rename or move in RetroGleek (and undo), and stop applying if a file is changed
  or replaced.
- **Ratings stored in photos:** star ratings that Windows Explorer, Lightroom and other programs keep inside JPEG,
  TIFF and PNG files (EXIF Rating or XMP `xmp:Rating`) are shown too, in silver instead of gold. Read only; your own
  rating takes their place.
- **Settings > Move to the next picture after marking** for quicker culling.

### Find and file
- **Show pull-down** next to the filter box (or View > Show): all, tagged / not tagged, at least 1 .. 5 stars, no
  rating, one colour label or no label. Works together with the name filter.
- **Sort by rating or label.** While you filter or sort by rating, ratings stored in the rest of the folder are read
  in the background.
- **Edit > Select tagged** and **Edit > Clear tags in this view**; the status bar counts the tagged pictures.
- **Copy to / Move to (Ctrl+Shift+Y / Ctrl+Shift+M):** pick a folder or one of the last 8 you used. Same rules as
  every copy and move: local drives only, nothing is overwritten, and Ctrl+Z undoes a move.
- **Favourite folders (Ctrl+D):** up to 20, shown with a star in the folder tree and in Go > Favourites; also from the
  tree's right-click menu.
- **Go > Recent folders:** the last 10 folders you opened.

### Views
- **Preview pane (Alt+P):** a large preview of the focused picture beside the thumbnails or the Details list, with its
  details and marks. Drag its edge to resize.
- **Filmstrip (J)** along the bottom of the viewer; click a thumbnail to show that picture.
- **Loupe:** hold Z over the picture for a round magnifier at 4 times the current size (at least 400%), with the
  pixel under the pointer outlined.

### Output
- **Contact sheet (Tools > Contact sheet):** lossless PNG index sheets of the selected pictures, with small, medium or
  large thumbnails, dark or light background, and optional ratings, labels and tags. Up to 100 pictures a page and
  2,000 per export; you see the page count and approximate size first, free space is checked, and every page is
  verified. Built one picture at a time, so memory use stays well under 100 MB.
- **Folder audit report:** new Rating, Label and Tag columns, filters for marks, and Rating / Label / Tagged / Rating
  source columns in the CSV. The command-line audit reads `marks.tsv` but never writes it.

### Lossless JPEG turn
- **Ctrl+Shift+R / Ctrl+Shift+L** (or Edit > Turn the file, also 180 degrees) turn the selected JPEG photos on disk;
  in the viewer, **File > Save the turn into the JPEG** saves the turn made with R / L. Always asks first.
- Nothing is re-compressed. A photo with an EXIF orientation tag just gets a new tag value; one without a tag is turned
  with Windows' lossless JPEG transform, only when its size is a whole number of JPEG blocks (otherwise it is left
  alone, with the reason).
- Each new file is written under a temporary name and checked before it replaces the original. Marks stay with the
  photo. Other formats are never changed.

### Under the hood
- New source files `Marks.cs`, `ContactSheet.cs` and `JpegRotate.cs` (all next to `build.bat`, which builds them).
- `.gitignore` now also ignores `marks.tsv`.
- Still exactly three native imports, no network code, no telemetry and no `unsafe` code.

### Known limitations
- Ratings inside files are looked for in the first 256 KB of each file only, which is where Windows, Lightroom and
  cameras put them.
- RetroGleek's own ratings, labels and tags are not written into the pictures or into XMP sidecar files. (Lightroom
  Classic ignores sidecars for JPEG files, and other programs disagree on how sidecars are named, so sidecars would
  not carry them reliably.)
- A JPEG without an orientation tag whose width or height is not a whole number of blocks (usually 8 or 16 pixels)
  can't be turned without loss, so it is left unchanged.

## 1.0.0 - first public release (November 2026)

RetroGleek is a small, fast, fully offline image browser and viewer for Windows 10 and 11, with built-in support for
567 retro-computer picture formats (Amiga, Atari, Commodore 64, ZX Spectrum, MSX, Amstrad CPC and many more) through
RECOIL 7.0.0. It builds into one small `.exe` with the C# compiler that ships with Windows: no SDK, no installer, no
NuGet packages and no internet connection. Released under the MIT License, Copyright (c) 2026 Mark Billson.

This first release brings together the original browser and viewer and all of the work below.

### Screen effects for retro pictures (new Effects menu)
All effects are display only: saved and copied pictures never change.
- **True TV aspect (T):** each pixel takes the shape it had on the original PAL / NTSC screen, from RECOIL's figures
  for that machine.
- **Scanlines (K):** off / light / medium / strong. Each original screen line is a soft beam; bright colours make a
  wider beam that fills more of the gap, as on a real CRT. Hidden while the picture is turned on its side.
- **Phosphor mask (M):** aperture grille (Trinitron), slot mask (most TVs, Commodore 1084S) or dot mask, drawn in
  screen pixels so it stays still while you pan.
- **Video signal (C):** RGB, S-Video or composite. Composite blends 1-pixel dithering into solid in-between colours,
  as on a TV.
- **Glow (W):** soft or strong halo around bright areas.
- **Screen curvature (U):** slight or strong bulge with darker, rounded corners; switches itself off at 800% and above.
- **All effects off / on (O)**, to compare with the picture as decoded.
- **Automatic effects (A):** each picture gets the effects of the screen its computer was usually seen on (for example
  S-Video and a slot mask for a Commodore 64, composite for a ZX Spectrum, the SM124 mono monitor look for 640 x 400
  Atari ST pictures, no CRT effects for LCD machines). Your own settings come back when you turn it off.
- **Presets:** save up to 9 effect combinations by name and recall them with Ctrl+1 .. Ctrl+9.

### Viewer
- **Sharp enlargement:** every pixel stays the same size at any zoom (sharp-bilinear), now the default for retro
  pictures; N cycles crisp / sharp / smooth.
- **Side-by-side compare (Ctrl+Shift+C):** two pictures with linked zoom and panning.
- **Fit-to-Screen toggle (F)** that stays on or off as you move between pictures.
- Export every frame of an animated GIF, or every page of a multi-page TIFF, as separate verified PNGs (Ctrl+Shift+S).

### Browser and file management
- Menu bar (File, Edit, View, Effects, Go, Tools, Settings, Help) with every command and its shortcut.
- **Details list (Ctrl+Shift+D)** with sortable columns: name, type, platform, dimensions, frames, colours, size, date
  and status; each view keeps its own size.
- **Analyse folder:** fills in frames, colours and exact duplicates in the background.
- **Subfolders switch (Ctrl+Shift+F):** list the pictures in every folder below the open one.
- Sort pull-down and thumbnail size slider in the filter bar.
- **Batch rename (Ctrl+F2)** with a pattern and a full preview; nothing is ever overwritten.
- **Undo (Ctrl+Z)** for renames, batch renames and moves (up to 20 steps).
- **Cut and paste, drag and drop** to move or copy pictures and folders, compatible with Explorer; cut items are shown
  faded until they are pasted.
- The folder tree refreshes by itself when folders change, and when USB sticks or memory cards are plugged in or out.

### Folder audit
- **Folder audit report (Ctrl+Shift+A):** one self-contained, offline HTML page per folder listing every picture
  file with its platform, type, whether it opened (and why not), size, dimensions, frames, colours, date, a thumbnail
  and a SHA-256 fingerprint. Searchable, filterable and sortable, with CSV export. A "no thumbnails" version makes a
  much smaller file.
- **Command-line audit:** `RetroGleek.exe --audit <folder> [--out <file>] [--no-thumbs] [--csv] [--overwrite]`,
  with exit codes for batch files and the Task Scheduler.
- **Console companion `RetroGleekCli.exe`:** the same audit with progress and a printed summary (built by
  `build.bat` alongside the main program).

### Settings
- **settings.ini** next to `RetroGleek.exe` remembers the window and layout, browser view, viewer options, screen
  effects, presets and the last folder. It falls back to `%LOCALAPPDATA%\RetroGleek\` if that folder can't be written.
  Settings > Reset to default settings removes it.

### Under the hood
- Source split into readable files, all next to `build.bat`; new `ScreenEffects.cs` for presets and the automatic
  choice of effects.
- `RECOIL.cs` regenerated by `tools/downgrade_recoil.py`, which now also switches off the harmless CS0162 warning, so
  the build is clean.
- Still exactly three native imports, no network code, no telemetry and no `unsafe` code.

### Building
Double-click `build.bat` on Windows 10 or 11. It produces `RetroGleek.exe` and the optional `RetroGleekCli.exe`.

### Known limitations
- Composite video does not reproduce the rainbow fringes and crawling dots of real hardware, which depend on each
  machine's exact timing.
- Near the edges of a curved screen, the pixel readout gives the pixel before bending.
- WebP, AVIF, HEIC and JPEG XL are deliberately not supported (see README).
