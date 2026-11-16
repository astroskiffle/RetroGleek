# RetroGleek - update of 16 November 2026

Changes made on top of 1.2.0. Built and tested on Windows 11, except the two MSX conversion formats (added afterwards, not yet built).

### EXIF details for modern formats
- The viewer's **EXIF details (E)** now work for HEIC / HEIF, AVIF, camera RAW, JPEG XR, WebP and JPEG XL: camera,
  lens, date taken, exposure, aperture, ISO, focal length, exposure bias, flash, software, artist, copyright,
  description and GPS, whenever the file holds them and Windows' decoder can read them. Same wording and order as for
  JPEG photos. Thumbnails don't read them, so browsing is no slower.
- These files are no longer checked a second time just to show their size.

### Cloud-only files stay in the cloud
- **Copying a folder** leaves out the cloud-only files and folders inside it (OneDrive and similar), so a copy never
  starts a download. A message says how many were left out. A folder that is itself cloud-only is left alone.
- **Moving a folder to another drive** (which Windows does by copying, downloading cloud-only files first) now asks
  before it starts if the folder holds cloud-only files: **Yes** moves anyway, **No** (the default) leaves that folder
  where it is and moves the rest, **Cancel** moves nothing. Moves on the same drive download nothing and don't ask.
  The check reads file attributes only, so it downloads nothing itself.

### Marks survive renames in Explorer
- A picture **renamed in Explorer** keeps its rating, colour label and tag, when that is certain: exactly one marked
  picture has gone from the same folder, and the renamed picture is the only file there with its size and date.
  Copies and duplicates never take a mark.

### Ratings written into JPEG and PNG files
- **Edit > Rating > Write my ratings into the files (JPEG, PNG)** is now in the menu. The command was described in
  the 1.2.0 notes and the README, but its menu entry was missing, so it could not be used.
- It now writes into **PNG** files too: your star rating goes into the PNG's XMP (the standard `iTXt` chunk, just
  before the picture data), and Windows rating tags in an `eXIf` chunk are updated if there are any. Every other
  chunk is copied byte for byte and checked, with every checksum, before the new file replaces the old one.
- Always asks first; pictures without a RetroGleek rating and other formats are left alone and listed. Marks stay
  with the files. F1 help lists the command.

### Animated WebP
- Animated WebPs now **play in the viewer** with their own frame times, instead of showing only the first frame.
  `P` pauses / plays, `,` / `.` step one frame, the info bar shows the frame number.
- **Ctrl+Shift+S** saves every frame of an animated WebP as a verified lossless PNG.
- How: RetroGleek reads the frame layout (position, time, blending) from the file itself, wraps each frame as a small
  still WebP and has Windows' own WebP decoder decode it, then puts the frames together the way browsers do. No
  decoder is bundled. Animations over about 384 MB of frames show their first frame.

### Convert to a retro format (new)
- **Tools > Convert to a retro format (Ctrl+Shift+E)**: the selected pictures, or the picture in the viewer, become
  new **ZX Spectrum screen** files (`.scr`). Choose whole picture (black borders) or fill the screen, and dithering:
  none, ordered pattern or error diffusion. A preview shows the first picture exactly as the file will look.
- For each 8 x 8 block the converter picks the brightness and ink / paper pair that best covers the block, including
  what dithering between the two can show, then sets each pixel.
- New files only, never overwriting; every file is checked by RECOIL before it is kept. New source file
  `RetroConvert.cs` (added to `build.bat`).
- **More formats:** **C64 Koala Painter** (`.koa`: 160 x 200 multicolour; the converter picks the screen's shared
  background colour, then the best 3 more colours for each 4 x 8 cell), **Atari ST Degas** (`.pi1`: 320 x 200, the
  16 colours that suit the picture best from the ST's 512) and **Amiga IFF** (`.iff`: 320 x 256, 32 colours from
  4096). For the ST and Amiga the palette is found by median cut and refined by k-means, then snapped to the
  machine's levels; the ordered pattern leaves flat areas and borders clean. The preview is now up to 640 x 512.
- **MSX:** **Screen 2** (`.sc2`: 256 x 192, the TMS9918's 15 colours, a foreground and background colour per
  8 x 1 strip) and **MSX2 Screen 8** (`.sc8`: 256 x 212, 256 fixed colours), both as BASIC "BSAVE" files.

### Fixes
- The viewer could show an old version of a picture: after a file was deleted in the browser and a new file was
  made under the same name (for example, converting the same picture again with other options), the viewer kept
  showing the earlier picture until RetroGleek was restarted. Pictures kept in memory now remember their file's size
  and modified time, and are read again when the file has changed.

### Documentation
- README: marks, cloud-only copy / move behaviour, the EXIF panel and writing ratings (JPEG and PNG) updated.
- RELEASE_NOTES.md: the 1.2.0 section now includes the changes above.

### Changed files
`WinCodecs.cs`, `Canvas.cs`, `Util.cs`, `MainForm.Files.cs`, `Marks.cs`, `JpegRotate.cs`, `Imaging.cs`, `RetroConvert.cs` (new), `build.bat`, `Program.cs`, `README.md`, `RELEASE_NOTES.md`.
Still exactly three native imports, no network code, no telemetry and no `unsafe` code.
