# RetroGleek - update of 17 November 2026

Changes made on top of the update of 16 November 2026.

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
- The converted frames are kept in memory up to about 384 MB; an even longer animation stops there.
- This shows what each frame would look like on the computer, not what it could play: an 8-bit computer couldn't
  redraw a whole screen at a GIF's speed.

### Cleaner colour edges with error diffusion
- For the ZX Spectrum, C64 Koala, C64 Doodle and MSX Screen 2 (in Convert and in the retro preview), error diffusion
  now stays inside each colour block (8 x 8 on the Spectrum and the C64 hires, 4 x 8 on the C64 multicolour, the
  8-pixel column on the MSX). What is left at a block's edge is dropped instead of being passed to a block with other
  colours, so the edges between colour blocks stay clean instead of speckled. The colours of each block were already
  chosen from the picture itself and never dithered; ordered dithering and no dithering are unchanged.

### Known limitations
- Each side has half the window, so pictures are smaller than in the viewer. Scanlines are shown from about 300%,
  so on smaller screens (or with overscan pictures) they may not appear on the right side.

### Changed files
`MainForm.cs`, `MainForm.Files.cs` (Effects menu entries, Shift+O, presets, F1 help, matching sizes, the retro
preview bar and keys), `Canvas.cs` (the retro preview mode), `RetroConvert.cs` (conversion in memory),
`Imaging.cs` (RECOIL reading a picture from memory, the frames of an animation one by one), `README.md`.
Still exactly three native imports, no network code, no telemetry and no `unsafe` code.
