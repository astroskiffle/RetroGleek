# RetroGleek release notes

## 1.3.0 - retro preview, dual pane and more (November 2026)

This release shows your photos on 8-bit computers, puts everything RetroGleek knows about a picture on screen, and
makes filing quicker with a dual-pane browser and a duplicate finder.

### Retro preview for photos and other modern pictures (new)
- **Shift+O** on a photo, PNG, WebP or any other non-retro picture (or **Effects > Retro preview**) shows it side by
  side: on the left as it is, on the right as it might look on an 8-bit computer.
- Computers: **ZX Spectrum**, **Commodore 64** multicolour and hires, **Amstrad CPC** mode 0 and mode 1, and **MSX**.
  Machines with larger colour spaces (Atari ST, Amiga, MSX2 Screen 8) are left out on purpose.
- The picture is converted in memory exactly as Convert to a retro format (Ctrl+Shift+E) would do it, then read back by
  RECOIL, so it gets that machine's resolution, colours and colour rules, and the screen effects work on it as on a
  real retro file. With no screen effects of your own, the automatic ones for that computer are used (for the preview
  only, unless you change them there).
- Choose with the bar above the right picture, the Effects menu, or the keys: **Q** / Shift+Q the computer, **D** /
  Shift+D the dithering (none, ordered pattern, error diffusion), **Shift+F** whole picture with borders or cropped to
  fill the screen. The choices are remembered in settings.ini.
- **Fill the screen: a draggable crop.** The left side shows the whole picture with a frame around the part the
  computer's screen shows; drag it to choose that part, and the right side is converted again when you let go.
  **Centre the crop** puts it back in the middle. The position stays as you step through pictures (handy for a series).
- With the whole picture, both sides show it at the same height, and zoom and pan together. Moving to the next or previous picture
  moves both; each picture is converted as you get to it (the two neighbours are prepared in the background).
- **Save... (or Ctrl+S, or Effects > Retro preview: save)**, next to the computer, makes two new files:
  - the computer's own file (ZX Spectrum `.scr`, C64 Koala `.koa` or Doodle `.dd`, Amstrad CPC `.scr`, MSX `.sc2`),
    converted exactly as previewed (computer, dithering, framing and crop) and without screen effects. Like every
    conversion, it is checked by RECOIL and read back before it is kept;
  - beside it, a lossless PNG of the screen as the right side shows it, with its screen effects (curvature too, if it
    is on), each of the computer's original lines 4 pixels high (for example 1024 x 768 for a ZX Spectrum): a picture
    to share, named like `holiday - ZX Spectrum screen.png`.
  The Save dialog asks before replacing a file, the PNG always gets a free name, and the original is never changed.
  This is the one place where screen effects end up in a file, and only because you ask for that PNG.
- The preview itself writes nothing. Shift+O or Esc closes it.

### Animated GIFs and WebPs in the retro preview
- An animated GIF or WebP now plays in the retro preview, on both sides, at the file's own frame times. Both sides
  always show the same frame; **P** pauses, **,** and **.** step one frame.
- The frames are converted in the background, and the animation plays as they are ready: the first time through it
  may wait for a frame. The info bar shows how many frames are converted, and a message says when all are.
- **The same colours in every frame**, so they don't jump from frame to frame: the Amstrad CPC's 16 (mode 0) or 4
  (mode 1) colours, and the C64 multicolour's shared background colour, are chosen once from up to 8 frames spread
  over the animation. The ZX Spectrum, the C64 hires and the MSX have fixed colours; their colours per block follow
  each frame, as they would on the real machine.
- Ordered dithering suits animations best: error diffusion changes from frame to frame, so moving areas shimmer.
- **Save** pauses the animation and saves the frame shown: the computer's file and the PNG (named with the frame
  number, for example `cat - ZX Spectrum screen frame 12.png`).
- **Ctrl+Shift+S** (or **Effects > Retro preview: save the animation**) saves the whole animation:
  - an **animated GIF** of the converted frames, without screen effects (a GIF holds at most 256 colours; the
    effects make thousands). It loops, keeps the file's frame times, stores only what changes from frame to frame,
    and is read back with Windows' own GIF decoder and compared frame by frame before it is kept;
  - if you like, **every frame as a PNG with its screen effects** (`name frame 001.png` ...), drawn as Ctrl+S draws
    one, for sharing or making a video. Nothing is overwritten. Changing the preview or closing it stops them.
  - It waits until every frame is converted.
- The converted frames are kept in memory up to about 384 MB; an even longer animation stops there.
- This shows what each frame would look like on the computer, not what it could play: an 8-bit computer couldn't
  redraw a whole screen at a GIF's speed.

### Before / after for retro pictures (new)
- **Shift+O** (or **Effects > Before / after**) shows a retro picture twice, side by side: on the left as decoded,
  with no screen effects at all, and on the right with your screen effects, or the automatic ones (with curvature
  if it is on).
- Works in the viewer, and in the browser with one retro picture selected.
- Both sides fit the window, and zoom and pan together. Moving to the next or previous picture, with the keys or
  the mouse wheel, moves both.
- Both sides are always shown at the same size: the left side takes the right side's zoom and position. (Before
  this fix, with Fit to screen and scanlines on, the right side fitted at a whole-number size, such as 200%, while
  the left side fitted at a larger size, such as 300%.) With True TV aspect on, the right side can still be
  slightly wider or narrower, because TV aspect changes the pixel shape there only.
- Effect keys (T, K, M, C, W, U, O, A) and presets (Ctrl+1 .. Ctrl+9) change the right side only. What you set
  there becomes your setting in the viewer when you close it (also if you close RetroGleek meanwhile).
- **Shift+O** or **Esc** closes it and goes back to the viewer or the browser, at the picture you were looking at.
- Built on side-by-side compare. No slideshow, filmstrip, loupe or marks while it is open.
- Display only: saved and copied pictures never change.

### Screen effect badges in the viewer (new)
- On retro pictures, small badges just above the info bar show how the picture is drawn, in two rows:
  - **the screen:** the **video signal** (a drawn RGB / SCART plug, the round S-Video plug or the yellow composite
    plug), the **scanlines** (a little screen with no, light, medium or strong lines), the **phosphor mask** (grille
    stripes, staggered slots or dots), the **glow** (a light with no, a soft or a strong halo) and the **curvature**
    (a flat or bulging screen);
  - **the picture:** **True TV aspect** (a square pixel, or the TV's pixel shape with its figure, such as "TV 1.05"),
    the **enlarged pixels** (crisp, sharp or smooth) and **automatic effects** (a monitor marked A when automatic chose
    the screen; resting the pointer on it names that screen).
- A badge is dimmed while its effect is set but not shown at this size, or the picture is turned sideways. With the
  screen effects off (`O`), a single "Effects off" badge takes their place, so one click brings them back.
- **Click a badge** to do what its key does (`C`, `K`, `M`, `W`, `U`, `T`, `N`, `A`, `O`); resting the pointer on one
  says which. Shown while the info bar (`I`) is on. In before / after and the retro preview they sit on the right side
  only; in side-by-side compare a click changes both sides.

### The computer badge (new)
- Left of the upper badge row, a wider badge names the computer a retro picture belongs to: the **maker** in plain
  lettering (no logos), a small **flag** for where the machine was designed (two where two countries shared the work),
  the **model**, the **year** it came out and the size of its **colour palette**. For example: *Sinclair* (UK flag),
  *ZX Spectrum*, *1982 · 15 colours*. It is for information only and doesn't respond to clicks.
- In the retro preview, the right side shows the computer being simulated. With the screen effects off it sits left
  of the "Effects off" badge. "PC" and unknown pictures have no badge.
- **In the preview pane too** (Alt+P): under a retro picture's facts, the computer badge beside the signal, scanlines,
  mask, glow and TV aspect badges (curvature, pixels and automatic are left out there), showing what the viewer would
  show it with (your own effects, or the automatic choice for its computer, or
  "Effects off"). They are for information only and can't be clicked; they wrap to the pane's width and follow any
  change you make from the Effects menu or a preset.
- The facts, one line per RECOIL platform (palette = colours the hardware can produce, not how many at once):

  | Platform | Maker | Flag(s) | Model | Year | Palette |
  |---|---|---|---|---|---|
  | Amiga | Commodore | US | Amiga | 1985 | 4,096 (OCS; AGA pictures use more) |
  | Amiga DCTV | Digital Creations | US | DCTV | 1991 | 16.7 million |
  | Amiga HAM-E | Black Belt Systems | US | HAM-E | 1991 | 16.7 million |
  | Amstrad CPC | Amstrad | UK | CPC | 1984 | 27 |
  | Apple II / IIe / IIGS | Apple | US | | 1977 / 1983 / 1986 | 16 / 16 / 4,096 |
  | Apple Macintosh | Apple | US | Macintosh | 1984 | 2 |
  | Atari 8-bit | Atari | US | 400 / 800 / XL / XE | 1979 | 256 |
  | Atari 8-bit VBXE | Atari + VBXE | US, Poland | 8-bit with VBXE | 2006 | 2 million (21-bit) |
  | Atari Portfolio | Atari | US, UK (designed by DIP) | Portfolio | 1989 | 2 |
  | Atari ST / STE / TT / Falcon | Atari | US | | 1985 / 1989 / 1990 / 1992 | 512 / 4,096 / 4,096 / 262,144 |
  | BBC Micro | Acorn | UK | BBC Micro | 1981 | 8 |
  | Commodore VIC-20 / C16, 116, Plus/4 / C64 / C128 | Commodore | US | | 1980 / 1984 / 1982 / 1985 | 16 / 121 / 16 / 16 |
  | Electronika BK | Elektronika | Soviet Union | BK-0010 | 1985 | 4 |
  | Electronika MC 0515 | Elektronika | Soviet Union | MS 0515 | not shown | not shown |
  | FM Towns | Fujitsu | Japan | FM Towns | 1989 | 16.7 million |
  | HP 48 | Hewlett-Packard | US | HP 48 calculator | 1990 | 2 |
  | MSX | ASCII / Microsoft | Japan, US | MSX | 1983 | 16 |
  | MSX2 / MSX2+ | ASCII | Japan | | 1985 / 1988 | 512 / 19,268 |
  | MSX V9990 VDP | Yamaha V9990 | Japan | GFX9000 for MSX | 1994 | 32,768 |
  | NEC PC-80 / PC-88 / PC-88 VA / PC-98 | NEC | Japan | PC-8001 / PC-8801 / PC-88 VA / PC-9801 | 1979 / 1981 / 1987 / 1982 | 8 / 8 / 65,536 / 4,096 |
  | Oric | Tangerine | UK | Oric | 1983 | 8 |
  | PlayStation | Sony | Japan | PlayStation | 1994 | 16.7 million |
  | Psion Series 3 | Psion | UK | Series 3 | 1991 | 2 |
  | SAM Coupe | Miles Gordon Technology | UK | SAM Coupe | 1989 | 128 |
  | Sharp X68000 | Sharp | Japan | X68000 | 1987 | 65,536 |
  | Tandy 1000 / TRS-80 / TRS-80 Color Computer | Tandy | US | | 1984 / 1977 / 1980 | 16 / 2 / 9 |
  | Timex 2048 | Timex | Portugal, UK | TC 2048 | 1984 | 15 |
  | Vector-06C | Temirazov / Sokolov | Soviet Union | Vector-06C | 1987 | 256 |
  | ZX81 / ZX Spectrum | Sinclair | UK | | 1981 / 1982 | 2 / 15 |
  | ZX Spectrum ULAplus | ULAplus | UK | ZX Spectrum with ULAplus | 2009 | 256 |
  | ZX Spectrum Profi | Profi | Soviet Union | ZX Spectrum Profi | 1990 | 256 |
  | ZX Spectrum Next | SpecNext | UK, Brazil | ZX Spectrum Next | 2017 | 512 |
  | ZX Evolution | NedoPC | Russia | ZX Evolution | 2009 | 32,768 (TS-Conf) |
  | ZX-Uno | ZX-Uno team | Spain | ZX-Uno | 2016 | 256 |
  | eLeMeNt ZX | LMN128 | Czech Republic | eLeMeNt ZX | not shown | 16.7 million |

### Colours used, in the viewer (new)
- Above the badges, a small panel says how many colours the retro picture uses, against its computer's palette
  ("11 of 16 colours used"), with a strip of swatches, most used first, when there are 64 or fewer (more: the count
  only). Counted once per picture from the picture as decoded, and per frame in an animation.
- In the retro preview it shows what the conversion used, handy for comparing dithering settings (for example
  "4 of 27 colours used" for the Amstrad CPC in mode 1).
- Shown with the badges (info bar on, `I`), hidden when there isn't room; information only, not clickable.

### More facts in the preview pane
- **Retro pictures:** the original resolution on the computer's screen when RECOIL enlarges it ("Original 160 x 200
  on the computer's screen"), and the colours the picture uses: how many, against the computer's palette where that
  makes sense ("11 of 16 colours used"), with a strip of swatches, most used first, when there are 64 or fewer.
  Counted from the full picture, so the count is exact.
- **Photos:** the camera and lens, the exposure on one line (for example 1/250 s · f/5.6 · ISO 200 · 50 mm) and when
  it was taken, from the EXIF details, as the viewer's E panel reads them. The GPS position is never shown here.
- Only the pane's own picture is read for these; the thumbnails are unchanged.

### Dual pane (new)
- **View > Dual pane** (Ctrl+Shift+2) shows two folders side by side, with one folder tree on the left that follows
  the active pane (outlined in blue). Click a pane or press **Tab** to switch; a folder clicked in the tree opens in
  the active pane.
- Each pane has its own folder, filter, sort, Show choice, Subfolders switch, Details / Thumbnails view, thumbnail
  size, back / forward history and auto-refresh. The address bar and filter bar follow the active pane, and so does
  everything else: menus, keys, marks, Properties, the preview pane and the viewer.
- **Drag pictures from one pane to the other**: the same drive moves, another drive copies, Ctrl copies, Shift moves,
  as in Explorer. **Shift+F5 / Shift+F6** copy / move the selection to the other pane's folder (also on the Edit and
  right-click menus). Files dragged in from Explorer are copied into the pane (Shift moves them). Same safe copy and
  move as everywhere: nothing overwritten, local drives only, Ctrl+Z undoes a move.
- **Buttons right of the address bar** (which now takes the left half): dual / single pane, swap the two panes'
  folders (also View > Swap the panes' folders), and copy or move the selection to the other pane, with arrows that
  point at it, and the preview pane (blue while it is shown). Each is greyed out until it can be used.
- **Drive buttons** after them: one for each local drive that is ready (internal, USB, and optical drives with a
  disc; not network drives or empty card-reader slots), with a small icon, the letter, and its name and free space
  when you rest the pointer on it. A click opens the drive in the active pane; the active pane's drive is highlighted.
  They appear and disappear as drives are plugged in or out, and are read in the background so the bar never waits.
- The bar between the panes can be dragged. Dual pane, the second pane's folder and the split are remembered, and so
  is each pane's own view (Details or Thumbnails, sort, sizes).
- In single-pane mode nothing changes: a file dropped on the thumbnails still opens it.

### Find duplicates (new)
- **Tools > Find duplicates** searches one or more folders (with their subfolders if you like, up to 20,000 pictures;
  local drives only, cloud-only files skipped) for:
  - **identical files**: files that share a size are compared by SHA-256;
  - **the same picture in other files**: the decoded pixels get a fingerprint of their own (pictures up to 2048 pixels
    a side), so a PNG saved again as BMP, or a JPEG whose information block changed, still matches. For retro
    pictures, plain borders are trimmed layer by layer and a whole-number enlargement is undone, so an emulator
    screenshot at 2x with a border matches the original `.scr` or `.koa`;
  - **similar pictures** (strict, normal or loose): a difference hash and a DCT ("perceptual") hash of the picture
    without its border, in all 8 orientations (turned and mirrored copies match), the shapes, and a 16 x 16 colour copy
    must all agree. The "alike" percentage comes from that copy, and says when only the colours differ. Large
    pictures that look exactly alike are compared pixel by pixel. Heavily cropped copies are not found.
- Groups are built around their best picture (the original retro file, the largest picture, lossless, has your
  marks, the largest file, the oldest), never by chaining, so every picture is like the first of its group.
- The results window lists each group with thumbnails, how each picture matches, size, marks, date and folder. The
  one to keep is first, in bold, unticked; the others are ticked. Keep this one, Compare the two selected (side by
  side in the main window), double-click to open, Show in its folder. Then **Tag the ticked** (Edit > Select tagged
  takes over) or **Move the ticked to the Recycle Bin** (asks first, and never every picture of a group).
- Nothing is changed by the search itself.

### Properties: every detail of a picture (new)
- **Alt+Enter**, **File > Properties** or right-click > **Properties**, in the browser or the viewer, opens a window
  with every detail the picture holds, grouped: the file (size, dates, format, dimensions, resolution), EXIF (picture
  and camera), GPS, IPTC, XMP and PNG text. Read by the same Windows readers Explorer uses, so it works for JPEG, TIFF,
  PNG, GIF, HEIC, AVIF, WebP, JPEG XL and camera RAW. Search, Copy all (or Ctrl+C on the selected rows), and Show the
  XMP text. Several windows can be open at once. Reading never changes anything.
- **Edit title, description, keywords...** (JPEG and PNG): the title, description, keywords, author and copyright,
  written into the file's XMP (`dc:title`, `dc:description`, `dc:subject`, `dc:creator`, `dc:rights`), which Windows
  Explorer, Lightroom and photo websites read. The same careful rewrite as the star ratings: only the information
  block changes, the picture data is copied byte for byte, and the new file is checked (it still decodes, the details
  read back) before it replaces the old one. Every other XMP property is kept; marks stay with the file. Older copies
  of these details in EXIF or IPTC are left alone, and the edit window says when a file has them. Camera details are
  never edited, and only JPEG and PNG files are ever changed.
- The rating writer and the new details writer now share one rewrite for JPEG and one for PNG (`JpegRotate.cs`).

### Rating from the right-click menu
- Right-click a thumbnail (or a row of the Details list) > **Rating** gives the selected pictures 1 to 5 stars, or
  removes the rating, as Alt+0 .. Alt+5 do. The rating they all share is ticked.

### Clear history (new)
- **Settings > Clear history** forgets the recent folders, the folder RetroGleek opens at next time, the recent
  Copy to / Move to folders and the back / forward list, and saves that at once. The folder open at the time isn't
  remembered either, until you open another one. Favourite folders, marks, presets and other settings stay; no
  pictures or folders are touched. Asks first.
- The guide's privacy section now says that `marks.tsv` and `settings.ini` hold file paths in plain text, and how to
  protect them with Windows' own encryption.

### Cleaner colour edges with error diffusion
- For the ZX Spectrum, C64 Koala, C64 Doodle and MSX Screen 2 (in Convert and in the retro preview), error diffusion
  now stays inside each colour block (8 x 8 on the Spectrum and the C64 hires, 4 x 8 on the C64 multicolour, the
  8-pixel column on the MSX). What is left at a block's edge is dropped instead of being passed to a block with other
  colours, so the edges between colour blocks stay clean instead of speckled. The colours of each block were already
  chosen from the picture itself and never dithered; ordered dithering and no dithering are unchanged.

### A shorter README, and a guide
- The README is now a short introduction: what RetroGleek is, the download, a quick start, the main features, privacy
  in brief, and how to build it. Everything else (every key and feature, privacy in full, the command-line audit,
  retro and modern formats, the antivirus note, the project layout) moved, unchanged, to the new **GUIDE.md**.

### A built-in self-test (new)
- **Help > Run the self-test**, `RetroGleek.exe --selftest` or `RetroGleekCli.exe --selftest` tries every part of
  RetroGleek that writes or changes a picture file (Save as PNG, Convert in every format and dithering, the retro
  preview, the animated GIF writer, ratings and details written into JPEG and PNG files, the lossless JPEG turn) on
  pictures it makes itself, in a temporary folder of its own that it deletes afterwards. 47 tests, a few seconds.
  It also checks the safety rules: nothing is overwritten, and a file that can't be changed safely is left as it was.

### More dependable keys and refreshing
- **Keys match exactly:** a shortcut no longer also fires with extra keys you didn't mean (Ctrl+F5 used to refresh,
  Ctrl+Del to delete, Ctrl+Alt+S, which is AltGr+S on many keyboards, to save).
- The menus, the keys, the F1 page, the tooltips and the messages all take their keys from one list, so they always
  agree.
- **Dual pane:** after a copy, move, rename, delete, conversion, turn, rating or undo, both panes, the folder tree and
  the preview pane update at once. When a folder is renamed, moved or deleted, the other pane goes along with it
  instead of showing an error.

### Under the hood
- The main window's code is split into files by topic, with the compare view (`CompareView.cs`) and the folder tree
  (`FolderTree.cs`) as controls of their own and every command in one table (`MainForm.Commands.cs`). New source files
  also include `Duplicates.cs` (Find duplicates), `Metadata.cs` (Properties), `MainForm.Dual.cs` (dual pane) and
  `SelfTest.cs`, all listed in `build.bat`. `build.bat` now ends by showing the SHA-256 fingerprint of
  `RetroGleek.exe`, for the release notes.
- The star-rating writer and the new details writer share one careful rewrite for JPEG and one for PNG.
- New `GUIDE.md` with every detail; the README is a short introduction.
- Still exactly three native imports of RetroGleek's own, no network code, no telemetry and no `unsafe` code.

### Known limitations
- Screen flicker is deliberately not simulated: a 60 Hz monitor can't show a 50 Hz CRT's flicker faithfully, and the
  slower pulse it would make sits in the range that can trigger photosensitive epilepsy. The guide explains this.
- Each side has half the window, so pictures are smaller than in the viewer. Scanlines are shown from about 300%,
  so on smaller screens (or with overscan pictures) they may not appear on the right side.

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
  multicolour), **C64 Doodle** (`.dd`, 320 x 200 hires), **Amstrad CPC screen** (`.scr`, mode 0: 16 of 27 colours,
  or mode 1: 4 of 27), **Atari ST Degas** (`.pi1`, 16 of 512 colours), **Amiga IFF** (`.iff`, 320 x 256, 32 of 4096
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
