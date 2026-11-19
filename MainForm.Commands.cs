// RetroGleek - the main window, continued: the command table (every menu command, its keys and when it is available)
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// ====================================================================== commands

// Every command of the menu bar is defined here once: its menu text, its keys, where the keys work, what it does,
// and when it is available or ticked. From this table:
//   - the menu bar (BuildMenuBar) places each command by its id, with its keys shown beside it;
//   - the keyboard (RunShortcut) runs the command whose keys were pressed, where they work;
//   - the F1 page (ShowHelp) and the right-click menus show the keys of the commands they mention.
// So a key is written down once, and the menu, the keyboard and the help can't disagree. When RetroGleek starts,
// two commands whose keys would clash are reported (CheckCommandKeys).
//
// New command: add it here, place it in BuildMenuBar, and mention it on the F1 page (ShowHelp) by its id.
//
// Keys the viewer and the thumbnail grid handle themselves (the effect keys, Ctrl+A in the list, Q / D in the retro
// preview ...) are only shown here (ShownKeys); Canvas.HandleKey, ThumbGrid and BeforeAfterKey act on them.

// where a command's keys work
[Flags]
enum KeyWhere
{
    None = 0,
    Always = 1,          // everywhere: also while comparing, also while typing in the filter or address box
    Viewer = 2,          // in the viewer
    Browser = 4,         // in the browser, also while typing in the filter or address box
    BrowserKeys = 8      // in the browser, not while typing there or using the sort box or size slider
}

sealed class Command
{
    public string Id;
    public string Text;                  // the menu text (& marks the access key)
    public Func<string> TextNow;         // the menu text, when it changes (worked out each time the menu opens)
    public Keys[] Shortcuts;             // the keys that run it (the first one is shown); the others are alternatives
    public string ShownKeys;             // keys shown although something else handles them (see above)
    public KeyWhere Where;
    public Func<bool> KeyOk;             // its keys work only while this is true (null: always)
    public Action Run;
    public Func<bool> On, Checked, Show; // available / ticked / shown in the menu (null: always / never / always)
    public bool InCompare;               // its menu entry stays available while two pictures are compared
    public bool Mark;                    // a rating, colour label or tag (ignored while comparing)

    public string KeyText
    {
        get { return ShownKeys ?? (Shortcuts != null && Shortcuts.Length > 0 ? MainForm.KeyName(Shortcuts[0]) : ""); }
    }
}

partial class MainForm
{
    readonly List<Command> commands = new List<Command>();
    readonly Dictionary<string, Command> commandById = new Dictionary<string, Command>();

    void AddCommand(Command c)
    {
        commands.Add(c);
        commandById.Add(c.Id, c);
    }

    Command Cmd(string id)
    {
        Command c;
        if (!commandById.TryGetValue(id, out c)) throw new ArgumentException("There is no command \"" + id + "\".");
        return c;
    }

    // the keys of a command, as the menus and F1 show them
    string KeysOf(string id) { return Cmd(id).KeyText; }

    static Keys[] K(params Keys[] k) { return k; }

    // "Ctrl+Shift+S", "Alt+Enter", "Del", "+" ...
    internal static string KeyName(Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        string mods = ((keyData & Keys.Control) != 0 ? "Ctrl+" : "") + ((keyData & Keys.Shift) != 0 ? "Shift+" : "") + ((keyData & Keys.Alt) != 0 ? "Alt+" : "");
        string name;
        if (k >= Keys.D0 && k <= Keys.D9) name = ((int)k - (int)Keys.D0).ToString();
        else if (k >= Keys.NumPad0 && k <= Keys.NumPad9) name = "Num " + ((int)k - (int)Keys.NumPad0);
        else switch (k)
        {
            case Keys.Delete: name = "Del"; break;
            case Keys.Back: name = "Backspace"; break;
            case Keys.Escape: name = "Esc"; break;
            case Keys.Enter: name = "Enter"; break;
            case Keys.Add: name = "+"; break;
            case Keys.Subtract: name = "-"; break;
            case Keys.Oemcomma: name = ","; break;
            case Keys.OemPeriod: name = "."; break;
            default: name = k.ToString(); break;
        }
        return mods + name;
    }

    // Runs the command whose keys these are, if its keys work at this stage (where). True when one ran.
    bool RunCommandKey(Keys keyData, KeyWhere where)
    {
        foreach (Command c in commands)
        {
            if ((c.Where & where) == 0 || c.Shortcuts == null || Array.IndexOf(c.Shortcuts, keyData) < 0) continue;
            if (c.KeyOk != null && !c.KeyOk()) continue;
            c.Run();
            return true;
        }
        return false;
    }

    bool IsMarkKey(Keys keyData)
    {
        foreach (Command c in commands)
            if (c.Mark && c.Shortcuts != null && Array.IndexOf(c.Shortcuts, keyData) >= 0) return true;
        return false;
    }

    // Two commands that the same key could run at the same time (null: none). Checked when RetroGleek starts.
    string CheckCommandKeys()
    {
        var clashes = new List<string>();
        for (int i = 0; i < commands.Count; i++)
            for (int j = i + 1; j < commands.Count; j++)
            {
                Command a = commands[i], b = commands[j];
                if (a.Shortcuts == null || b.Shortcuts == null) continue;
                KeyWhere wa = a.Where, wb = b.Where;
                if ((wa & KeyWhere.Browser) != 0) wa |= KeyWhere.BrowserKeys;      // the browser's keys overlap
                if ((wb & KeyWhere.Browser) != 0) wb |= KeyWhere.BrowserKeys;
                bool overlap = (wa & wb) != 0 || ((wa | wb) & KeyWhere.Always) != 0;
                if (!overlap) continue;
                foreach (Keys k in a.Shortcuts)
                    if (Array.IndexOf(b.Shortcuts, k) >= 0) clashes.Add(KeyName(k) + ": " + a.Id + " and " + b.Id);
            }
        return clashes.Count == 0 ? null : string.Join("\n", clashes.ToArray());
    }

    void BuildCommands()
    {
        const KeyWhere Both = KeyWhere.Viewer | KeyWhere.Browser;                  // viewer and browser, also while typing
        const KeyWhere BothKeys = KeyWhere.Viewer | KeyWhere.BrowserKeys;          // viewer, and browser when not typing
        Func<bool> browsing = () => !viewing;
        Func<bool> inViewer = () => viewing && canvas.HasPicture;
        Func<bool> onTree = () => lastPane == tree;
        Func<bool> hasSel = () => !viewing && (onTree() ? IsPlainFolder(tree.SelectedNode) : grid.SelectedIndices().Count > 0);
        Func<bool> hasItems = () => !viewing && grid.Items.Count > 0;
        Func<bool> notOnTree = () => viewing || !onTree();                          // marks: not while the tree has the keyboard
        Func<bool> canMark = () => viewing ? canvas.CurrentPath != null : MarkTargets().Count > 0;
        Func<bool> canStep = () => viewing && canvas.FileCount > 1;
        Func<Keys, Action> viewerKey = key => () => canvas.HandleKey(key);         // the viewer's own key, from the menu

        // ---- File
        AddCommand(new Command { Id = "open", Text = "&Open...", Shortcuts = K(Keys.Control | Keys.O), Where = Both, Run = OpenDialog });
        AddCommand(new Command { Id = "new-folder", Text = "&New folder", Run = DoNewFolder, On = () => !viewing && NodePath(tree.SelectedNode) != null });
        AddCommand(new Command
        {
            Id = "save-png", Text = "&Save as PNG (lossless)...", Shortcuts = K(Keys.Control | Keys.S), Where = Both,
            Run = () => { if (viewing) SaveCurrentPng(); else DoSavePng(); },
            On = () => viewing ? canvas.HasPicture : grid.SelectedIndices().Count > 0
        });
        AddCommand(new Command
        {
            Id = "properties", Text = "&Properties (all details, edit title and keywords)...", Shortcuts = K(Keys.Alt | Keys.Enter), Where = BothKeys,
            Run = ShowProperties, On = () => PropertiesTarget() != null
        });
        AddCommand(new Command
        {
            Id = "export-frames", Text = "Export all &frames as PNG...", Shortcuts = K(Keys.Control | Keys.Shift | Keys.S), Where = Both,
            Run = DoExportFrames, On = () => viewing ? canvas.HasPicture : grid.SelectedIndices().Count > 0
        });
        AddCommand(new Command
        {
            Id = "save-turn", Text = "Save the &turn into the JPEG (lossless)...",
            Run = () => { string sp; int st; bool sfx, sfy; if (canvas.GetSaveSource(out sp, out st, out sfx, out sfy)) RotateFiles(st); },
            On = () => viewing && canvas.ExtraTurns && canvas.CurrentPath != null && JpegRotate.IsJpeg(canvas.CurrentPath) && !rotateBusy
        });
        AddCommand(new Command { Id = "exit", Text = "E&xit", ShownKeys = "Alt+F4", Run = Close, InCompare = true });

        // ---- Edit
        AddCommand(new Command
        {
            Id = "undo", Text = "&Undo", TextNow = UndoMenuText, Shortcuts = K(Keys.Control | Keys.Z), Where = KeyWhere.BrowserKeys,
            Run = DoUndo, On = () => !viewing && undoSteps.Count > 0
        });
        AddCommand(new Command
        {
            Id = "copy", Text = "&Copy", TextNow = () => viewing ? "&Copy picture" : "&Copy", Shortcuts = K(Keys.Control | Keys.C), Where = BothKeys,
            Run = () => { if (viewing) CopyCurrentPicture(); else DoCopy(onTree()); },
            On = () => viewing ? canvas.HasPicture : hasSel()
        });
        AddCommand(new Command { Id = "cut", Text = "Cu&t", Shortcuts = K(Keys.Control | Keys.X), Where = KeyWhere.BrowserKeys, Run = () => DoCut(onTree()), On = hasSel });
        AddCommand(new Command
        {
            Id = "paste", Text = "&Paste", Shortcuts = K(Keys.Control | Keys.V), Where = KeyWhere.BrowserKeys,
            Run = () => DoPaste(onTree()), On = () => !viewing && ClipHasFiles()
        });
        AddCommand(new Command { Id = "copy-to", Text = "&Choose a folder...", Shortcuts = K(Keys.Control | Keys.Shift | Keys.Y), Where = Both, Run = () => TransferTo(false, null) });
        AddCommand(new Command { Id = "move-to", Text = "&Choose a folder...", Shortcuts = K(Keys.Control | Keys.Shift | Keys.M), Where = Both, Run = () => TransferTo(true, null) });
        AddCommand(new Command
        {
            Id = "copy-other", Text = "Copy to the ot&her pane", Shortcuts = K(Keys.Shift | Keys.F5), Where = KeyWhere.BrowserKeys,
            Run = () => SendToOther(false), On = CanSendToOther
        });
        AddCommand(new Command
        {
            Id = "move-other", Text = "Move to the other pa&ne", Shortcuts = K(Keys.Shift | Keys.F6), Where = KeyWhere.BrowserKeys,
            Run = () => SendToOther(true), On = CanSendToOther
        });
        AddCommand(new Command
        {
            Id = "rename", Text = "&Rename...", Shortcuts = K(Keys.F2), Where = KeyWhere.BrowserKeys, Run = () => DoRename(onTree()),
            On = () => !viewing && (onTree() ? IsPlainFolder(tree.SelectedNode) : grid.SelectedIndices().Count == 1)
        });
        AddCommand(new Command
        {
            Id = "delete", Text = "&Delete (to Recycle Bin)", Shortcuts = K(Keys.Delete), Where = BothKeys,
            Run = () => { if (viewing) DeleteCurrentPicture(); else DoDelete(onTree()); },
            On = () => viewing ? canvas.CurrentPath != null : hasSel()
        });
        AddCommand(new Command { Id = "turn-cw", Text = "&Clockwise", Shortcuts = K(Keys.Control | Keys.Shift | Keys.R), Where = Both, Run = () => RotateFiles(1) });
        AddCommand(new Command { Id = "turn-ccw", Text = "C&ounter-clockwise", Shortcuts = K(Keys.Control | Keys.Shift | Keys.L), Where = Both, Run = () => RotateFiles(3) });
        AddCommand(new Command { Id = "turn-180", Text = "&180 degrees", Run = () => RotateFiles(2) });
        for (int i = 0; i <= 5; i++)
        {
            int n = i;
            AddCommand(new Command
            {
                Id = "rate-" + n, Text = n == 0 ? "&No rating" : "&" + n + "  " + Marks.StarText(n),
                Shortcuts = K(Keys.Alt | (Keys)((int)Keys.D0 + n), Keys.Alt | (Keys)((int)Keys.NumPad0 + n)), Where = BothKeys, KeyOk = notOnTree,
                Run = () => DoMark(n, -1, false), Mark = true
            });
        }
        AddCommand(new Command { Id = "write-ratings", Text = "&Write my ratings into the files (JPEG, PNG)...", Run = WriteRatingsToFiles, On = () => !ratingBusy });
        for (int i = 0; i <= 5; i++)
        {
            int n = i;
            AddCommand(new Command
            {
                Id = "label-" + n, Text = "&" + n + "  " + Marks.LabelNames[n],
                Shortcuts = K(Keys.Shift | (Keys)((int)Keys.D0 + n), Keys.Shift | (Keys)((int)Keys.NumPad0 + n)), Where = BothKeys, KeyOk = notOnTree,
                Run = () => DoMark(-1, n, false), Mark = true
            });
        }
        AddCommand(new Command
        {
            Id = "tag", Text = "Ta&g / untag", Shortcuts = K(Keys.X), Where = BothKeys, KeyOk = notOnTree,
            Run = () => DoMark(-1, -1, true), On = canMark, Mark = true
        });
        AddCommand(new Command { Id = "select-tagged", Text = "Sel&ect tagged", Run = () => grid.SelectTagged(), On = () => !viewing && grid.CountTagged() > 0 });
        AddCommand(new Command { Id = "clear-tags", Text = "Clear tags in this &view...", Run = ClearTags, On = () => !viewing && grid.CountTagged() > 0 });
        AddCommand(new Command { Id = "select-all", Text = "Select &all", ShownKeys = "Ctrl+A", Run = () => grid.SelectAllItems(), On = hasItems });   // Ctrl+A: ThumbGrid
        AddCommand(new Command
        {
            Id = "invert", Text = "&Invert selection", Shortcuts = K(Keys.Control | Keys.I), Where = KeyWhere.BrowserKeys, KeyOk = () => !onTree(),
            Run = () => grid.InvertSelection(), On = hasItems
        });
        AddCommand(new Command { Id = "filter", Text = "&Filter...", Shortcuts = K(Keys.Control | Keys.F), Where = KeyWhere.Browser, Run = ShowFilter, On = browsing });
        AddCommand(new Command
        {
            Id = "subfolders", Text = "Include &subfolders", Shortcuts = K(Keys.Control | Keys.Shift | Keys.F), Where = KeyWhere.Browser,
            Run = ToggleSubfolders, On = browsing, Checked = () => grid.Recursive
        });

        // ---- View
        AddCommand(new Command
        {
            Id = "viewer", Text = "Open in &viewer", TextNow = () => viewing ? "Back to the &browser" : "Open in &viewer", ShownKeys = "Enter / Esc",
            Run = () => { if (viewing) SetViewing(false); else OpenIndex(grid.FocusIndex); },
            On = () => viewing || (grid.FocusIndex >= 0 && grid.FocusIndex < grid.Items.Count)
        });
        AddCommand(new Command { Id = "fullscreen", Text = "F&ullscreen", Shortcuts = K(Keys.F11), Where = KeyWhere.Always, Run = ToggleFull, Checked = () => full, InCompare = true });
        AddCommand(new Command
        {
            Id = "compare", Text = "&Compare side by side", TextNow = () => comparing ? "&Close the comparison" : "&Compare side by side (2 pictures selected)",
            Shortcuts = K(Keys.Control | Keys.Shift | Keys.C), Where = KeyWhere.Browser,               // while comparing: CompareKey
            Run = () => { if (comparing) EndCompare(); else StartCompare(); }, On = () => comparing || CanCompare(), InCompare = true
        });
        AddCommand(new Command { Id = "refresh", Text = "&Refresh", Shortcuts = K(Keys.F5), Where = KeyWhere.BrowserKeys, Run = ReloadAll, On = browsing });
        AddCommand(new Command
        {
            Id = "dual", Text = "D&ual pane (two folders side by side)", Shortcuts = K(Keys.Control | Keys.Shift | Keys.D2, Keys.Control | Keys.Shift | Keys.NumPad2),
            Where = KeyWhere.Browser, Run = () => SetDual(!dual), On = browsing, Checked = () => dual
        });
        AddCommand(new Command
        {
            Id = "switch-pane", Text = "S&witch to the other pane", Shortcuts = K(Keys.Tab), Where = KeyWhere.BrowserKeys, KeyOk = () => dual,
            Run = SwitchPane, On = () => dual && !viewing
        });
        AddCommand(new Command { Id = "swap-panes", Text = "Swap the panes' f&olders", Run = SwapPanes, On = CanSwap });
        AddCommand(new Command
        {
            Id = "details", Text = "&Details list", Shortcuts = K(Keys.Control | Keys.Shift | Keys.D), Where = KeyWhere.Browser,
            Run = () => SetDetailsView(!grid.DetailsMode), On = browsing, Checked = () => grid.DetailsMode
        });
        AddCommand(new Command
        {
            Id = "tile-details", Text = "Show &details on tiles", Run = () => grid.ShowDetails = !grid.ShowDetails,
            On = () => !viewing && !grid.DetailsMode, Checked = () => grid.ShowDetails
        });
        AddCommand(new Command { Id = "zoom-fit", Text = "&Fit this picture", Shortcuts = K(Keys.D0), Where = KeyWhere.Viewer, Run = viewerKey(Keys.D0) });
        AddCommand(new Command { Id = "zoom-100", Text = "&Actual size (100%)", Shortcuts = K(Keys.D1), Where = KeyWhere.Viewer, Run = viewerKey(Keys.D1) });
        AddCommand(new Command { Id = "zoom-in", Text = "Zoom &in", Shortcuts = K(Keys.Add), Where = KeyWhere.Viewer, Run = viewerKey(Keys.Add) });
        AddCommand(new Command { Id = "zoom-out", Text = "Zoom &out", Shortcuts = K(Keys.Subtract), Where = KeyWhere.Viewer, Run = viewerKey(Keys.Subtract) });
        AddCommand(new Command { Id = "rotate-cw", Text = "Rotate &clockwise", Shortcuts = K(Keys.R), Where = KeyWhere.Viewer, Run = viewerKey(Keys.R), On = inViewer });
        AddCommand(new Command { Id = "rotate-ccw", Text = "Rotate co&unter-clockwise", Shortcuts = K(Keys.L), Where = KeyWhere.Viewer, Run = viewerKey(Keys.L), On = inViewer });
        AddCommand(new Command { Id = "flip-h", Text = "Flip &left-right", Shortcuts = K(Keys.H), Where = KeyWhere.Viewer, Run = viewerKey(Keys.H), On = inViewer });
        AddCommand(new Command { Id = "flip-v", Text = "Flip &top-bottom", Shortcuts = K(Keys.V), Where = KeyWhere.Viewer, Run = viewerKey(Keys.V), On = inViewer });
        AddCommand(new Command { Id = "info", Text = "&Info bar", Shortcuts = K(Keys.I), Where = KeyWhere.Viewer, Run = viewerKey(Keys.I), On = inViewer, Checked = () => canvas.InfoOn });
        AddCommand(new Command { Id = "exif", Text = "&EXIF details", Shortcuts = K(Keys.E), Where = KeyWhere.Viewer, Run = viewerKey(Keys.E), On = inViewer, Checked = () => canvas.ExifOn });
        AddCommand(new Command { Id = "pixel-grid", Text = "Pixel &grid (800% and above)", Shortcuts = K(Keys.G), Where = KeyWhere.Viewer, Run = viewerKey(Keys.G), On = inViewer, Checked = () => canvas.GridOn });
        AddCommand(new Command { Id = "loupe", Text = "Loupe: hold Z over the picture (4 times the current size)", ShownKeys = "Z", Run = () => { }, On = () => false });   // Canvas
        AddCommand(new Command { Id = "filmstrip", Text = "Fil&mstrip", Shortcuts = K(Keys.J), Where = KeyWhere.Viewer, Run = viewerKey(Keys.J), On = inViewer, Checked = () => canvas.Filmstrip });
        AddCommand(new Command
        {
            Id = "preview", Text = "&Preview pane", Shortcuts = K(Keys.Alt | Keys.P), Where = KeyWhere.BrowserKeys,
            Run = () => SetPreview(!preview.Visible), On = browsing, Checked = () => preview.Visible
        });

        // ---- Effects (the keys are the viewer's own: Canvas.HandleKey, and BeforeAfterKey for the retro preview)
        AddCommand(new Command { Id = "effects", Text = "&Show screen effects", ShownKeys = "O", Run = () => canvas.EffectsOff = !canvas.EffectsOff, Checked = () => !canvas.EffectsOff });
        AddCommand(new Command
        {
            Id = "before-after", Text = "&Before / after: plain and with effects, side by side", ShownKeys = "Shift+O",   // RunShortcut
            TextNow = () => beforeAfter && !baSim ? "Close &before / after" : "&Before / after: plain and with effects, side by side",
            Run = () => { if (beforeAfter) EndBeforeAfter(); else StartBeforeAfter(); },
            On = () => (beforeAfter && !baSim) || CanBeforeAfter(), Checked = () => beforeAfter && !baSim, InCompare = true
        });
        AddCommand(new Command
        {
            Id = "retro-preview", Text = "&Retro preview: this picture on an 8-bit computer", ShownKeys = "Shift+O",
            TextNow = () => beforeAfter && baSim ? "Close the &retro preview" : "&Retro preview: this picture on an 8-bit computer",
            Run = () => { if (beforeAfter) EndBeforeAfter(); else StartBeforeAfter(); },
            On = () => (beforeAfter && baSim) || CanRetroPreview(), Checked = () => beforeAfter && baSim, InCompare = true
        });
        AddCommand(new Command { Id = "sim-computer", Text = "Retro preview c&omputer", ShownKeys = "Q", Run = () => { }, InCompare = true });
        AddCommand(new Command { Id = "sim-dither", Text = "Retro preview &dithering", ShownKeys = "D", Run = () => { }, InCompare = true });
        AddCommand(new Command
        {
            Id = "sim-fill", Text = "Retro preview: &fill the screen (crop)", ShownKeys = "Shift+F",
            Run = () => SimSet(simOpts.Machine, simOpts.Dither, !simOpts.Fill), Checked = () => simOpts.Fill, InCompare = true
        });
        AddCommand(new Command
        {
            Id = "sim-save", Text = "Retro preview: sa&ve the computer's file and a PNG of the screen...", ShownKeys = "Ctrl+S",
            Run = SimSave, On = () => beforeAfter && baSim && !simSaving, InCompare = true
        });
        AddCommand(new Command
        {
            Id = "sim-save-anim", Text = "Retro preview: save the &animation (GIF, and PNGs with the screen effects)...", ShownKeys = "Ctrl+Shift+S",
            Run = SimSaveAnim, On = () => beforeAfter && baSim && !simSaving && cmp.SimFrameCount > 1, InCompare = true
        });
        AddCommand(new Command
        {
            Id = "auto", Text = "&Automatic: the usual screen of each picture's computer", ShownKeys = "A",
            TextNow = () =>
            {
                EffectSet ac = canvas.AutoChoice;
                return ac != null && viewing ? "&Automatic: " + ac.Name.Replace("&", "&&") : "&Automatic: the usual screen of each picture's computer";
            },
            Run = () => canvas.ToggleAuto(), Checked = () => canvas.AutoEffects
        });
        AddCommand(new Command { Id = "tv", Text = "True &TV aspect", ShownKeys = "T", Run = () => canvas.TvAspect = !canvas.TvAspect, Checked = () => canvas.TvAspect });
        AddCommand(new Command { Id = "scanlines", Text = "Scan&lines", ShownKeys = "K", Run = () => { } });
        AddCommand(new Command { Id = "mask", Text = "Phosphor &mask", ShownKeys = "M", Run = () => { } });
        AddCommand(new Command { Id = "signal", Text = "Video si&gnal", ShownKeys = "C", Run = () => { } });
        AddCommand(new Command { Id = "glow", Text = "Gl&ow", ShownKeys = "W", Run = () => { } });
        AddCommand(new Command { Id = "curvature", Text = "Screen c&urvature", ShownKeys = "U", Run = () => { } });
        for (int i = 0; i < MaxPresets; i++)
        {
            int slot = i;
            AddCommand(new Command
            {
                Id = "preset-" + (slot + 1), Text = "Preset " + (slot + 1),
                TextNow = () => presets[slot] == null ? "" : "&" + (slot + 1) + "  " + presets[slot].Name.Replace("&", "&&"),
                Shortcuts = K(Keys.Control | (Keys)((int)Keys.D1 + slot), Keys.Control | (Keys)((int)Keys.NumPad1 + slot)), Where = KeyWhere.Always,
                Run = () => { ApplyPreset(slot); UpdatePreview(); }, Show = () => presets[slot] != null, InCompare = true
            });
        }
        AddCommand(new Command { Id = "save-preset", Text = "Sa&ve these effects as a preset...", Run = SavePreset });

        // ---- Go
        AddCommand(new Command { Id = "back", Text = "&Back", Shortcuts = K(Keys.Alt | Keys.Left), Where = KeyWhere.Browser, Run = () => GoHistory(-1), On = () => !viewing && hpos > 0 });
        AddCommand(new Command { Id = "forward", Text = "&Forward", Shortcuts = K(Keys.Alt | Keys.Right), Where = KeyWhere.Browser, Run = () => GoHistory(1), On = () => !viewing && hpos < hist.Count - 1 });
        AddCommand(new Command { Id = "up", Text = "&Up one folder", Shortcuts = K(Keys.Alt | Keys.Up), Where = KeyWhere.Browser, Run = GoUp, On = browsing });
        AddCommand(new Command { Id = "up-back", Text = "Up one folder", Shortcuts = K(Keys.Back), Where = KeyWhere.BrowserKeys, Run = GoUp });   // not in the menu
        AddCommand(new Command { Id = "address", Text = "&Address bar", Shortcuts = K(Keys.Control | Keys.L), Where = KeyWhere.Browser, Run = FocusAddress, On = browsing });
        AddCommand(new Command
        {
            Id = "favourite", Text = "&Add this folder", TextNow = () => IsFavourite(grid.Dir) ? "&Remove this folder from the favourites" : "&Add this folder",
            Shortcuts = K(Keys.Control | Keys.D), Where = Both, Run = () => ToggleFavourite(grid.Dir), On = () => grid.Dir != null
        });
        AddCommand(new Command { Id = "prev-pic", Text = "&Previous picture", Shortcuts = K(Keys.Left), Where = KeyWhere.Viewer, Run = viewerKey(Keys.Left), On = canStep });
        AddCommand(new Command { Id = "next-pic", Text = "&Next picture", Shortcuts = K(Keys.Right), Where = KeyWhere.Viewer, Run = viewerKey(Keys.Right), On = canStep });
        AddCommand(new Command { Id = "first-pic", Text = "F&irst picture", Shortcuts = K(Keys.Home), Where = KeyWhere.Viewer, Run = viewerKey(Keys.Home), On = canStep });
        AddCommand(new Command { Id = "last-pic", Text = "&Last picture", Shortcuts = K(Keys.End), Where = KeyWhere.Viewer, Run = viewerKey(Keys.End), On = canStep });

        // ---- Tools
        AddCommand(new Command
        {
            Id = "analyse", Text = "Analyse &folder (colours, frames, duplicates)",
            TextNow = () => grid.Analysing ? "Stop &analysing" : "Analyse &folder (colours, frames, duplicates)",
            Run = () => { if (grid.Analysing) grid.StopAnalysis(); else { SetDetailsView(true); grid.StartAnalysis(); } },
            On = () => !viewing && grid.Dir != null
        });
        AddCommand(new Command
        {
            Id = "audit", Text = "Folder &audit report (HTML)...", Shortcuts = K(Keys.Control | Keys.Shift | Keys.A), Where = Both,
            Run = () => DoAudit(true), On = () => grid.Dir != null && !auditBusy
        });
        AddCommand(new Command { Id = "audit-small", Text = "Folder audit report, &no thumbnails (smaller)...", Run = () => DoAudit(false), On = () => grid.Dir != null && !auditBusy });
        AddCommand(new Command { Id = "duplicates", Text = "Find &duplicates (also similar pictures)...", Run = DoFindDuplicates, InCompare = true });
        AddCommand(new Command { Id = "contact-sheet", Text = "&Contact sheet (lossless PNG)...", Run = DoContactSheet, On = () => !viewing && !sheetBusy && grid.SelectedIndices().Count > 0 });
        AddCommand(new Command
        {
            Id = "convert", Text = "Con&vert to a retro format...", Shortcuts = K(Keys.Control | Keys.Shift | Keys.E), Where = Both, Run = DoConvert,
            On = () => !convertBusy && (viewing ? canvas.CurrentPath != null : grid.SelectedIndices().Count > 0)
        });
        AddCommand(new Command
        {
            Id = "batch-rename", Text = "Batch &rename...", Shortcuts = K(Keys.Control | Keys.F2), Where = KeyWhere.BrowserKeys,
            Run = () => { if (!onTree()) DoBatchRename(); }, On = () => !viewing && grid.SelectedIndices().Count > 0
        });
        AddCommand(new Command
        {
            Id = "slideshow", Text = "&Slideshow", Shortcuts = K(Keys.S), Where = KeyWhere.Viewer, Run = viewerKey(Keys.S),
            On = () => viewing && canvas.FileCount > 1, Checked = () => canvas.Slideshow
        });
        AddCommand(new Command { Id = "anim-pause", Text = "&Pause", Shortcuts = K(Keys.P), Where = KeyWhere.Viewer, Run = viewerKey(Keys.P), Checked = () => canvas.AnimPaused });
        AddCommand(new Command { Id = "prev-frame", Text = "Pre&vious frame", Shortcuts = K(Keys.Oemcomma), Where = KeyWhere.Viewer, Run = viewerKey(Keys.Oemcomma) });
        AddCommand(new Command { Id = "next-frame", Text = "&Next frame", Shortcuts = K(Keys.OemPeriod), Where = KeyWhere.Viewer, Run = viewerKey(Keys.OemPeriod) });

        // ---- Settings
        AddCommand(new Command
        {
            Id = "retro", Text = "Show &retro-computer formats (RECOIL)", Shortcuts = K(Keys.F8), Where = KeyWhere.BrowserKeys,
            Run = ToggleRetro, Checked = () => Retro.Enabled
        });
        AddCommand(new Command { Id = "fit-lock", Text = "&Fit to screen", ShownKeys = "F", Run = () => canvas.ToggleFitLock(), Checked = () => canvas.FitLock });
        AddCommand(new Command { Id = "auto-advance", Text = "Move to the &next picture after marking", Run = () => autoAdvance = !autoAdvance, Checked = () => autoAdvance });
        AddCommand(new Command { Id = "checker", Text = "&Checkerboard behind transparency", ShownKeys = "B", Run = () => canvas.ToggleChecker(), Checked = () => canvas.Checker });
        AddCommand(new Command { Id = "pixels", Text = "&Enlarged pixels", ShownKeys = "N", Run = () => { } });
        AddCommand(new Command { Id = "clear-history", Text = "Clear &history...", Run = ClearHistory });
        AddCommand(new Command { Id = "reset", Text = "&Reset to default settings...", Run = ResetPrefs });

        // ---- Help
        AddCommand(new Command { Id = "help", Text = "&Keys and mouse", Shortcuts = K(Keys.F1), Where = KeyWhere.Always, Run = ShowHelp, InCompare = true });
        AddCommand(new Command { Id = "self-test", Text = "Run the &self-test", Run = RunSelfTest, On = () => !selfTesting });
        AddCommand(new Command { Id = "about", Text = "&About RetroGleek", Run = ShowAbout, InCompare = true });

        string clash = CheckCommandKeys();
        if (clash != null)
            Shown += delegate { MessageBox.Show(this, "These keys are given to two commands at once (MainForm.Commands.cs):\n\n" + clash, "RetroGleek", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
    }
}
