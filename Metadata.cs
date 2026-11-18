// RetroGleek - File > Properties: every piece of metadata a picture holds (EXIF, GPS, IPTC, XMP, PNG text ...), and
// writing the title, description, keywords, author and copyright into JPEG and PNG files
// Copyright (c) 2026 Mark Billson. MIT License (see LICENSE). Part of RetroGleek; see Program.cs for the overview.
//
// Reading goes through the metadata readers of the Windows Imaging Component (WPF's BitmapMetadata), the same ones
// Windows Explorer uses, so it works the same for JPEG, TIFF, PNG, GIF, HEIC, AVIF, WebP, JPEG XL and camera RAW.
// Nothing is changed by reading. Writing changes only the file's XMP block, through the same careful rewrite as the
// star ratings (JpegRating.Rewrite, PngRating.Rewrite): the picture data is copied byte for byte and the new file is
// checked before it replaces the old one. Retro pictures (RECOIL) hold no metadata of this kind.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using WI = System.Windows.Media.Imaging;

sealed class MetaEntry
{
    public string Group, Name, Value;
}

// ====================================================================== reading

static class MetaReader
{
    // Everything the file holds, grouped. rawXmp = the XMP packet as text (JPEG and PNG), or null. Any thread.
    public static List<MetaEntry> Read(string path, out string rawXmp, out string note)
    {
        var r = new List<MetaEntry>();
        rawXmp = null; note = null;
        var fi = new FileInfo(path);
        Add(r, "File", "Name", fi.Name);
        Add(r, "File", "Folder", fi.DirectoryName);
        Add(r, "File", "Size", Util.FmtSize(fi.Length) + "  (" + fi.Length.ToString("#,0", CultureInfo.InvariantCulture) + " bytes)");
        Add(r, "File", "Modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Add(r, "File", "Created", fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if ((fi.Attributes & Util.CloudMask) != 0) { note = "This file is cloud-only (not stored on this PC), so it was not opened."; return r; }
        if (!Util.IsNative(path) && Retro.Claims(path))
        {
            note = "A retro picture, decoded by RECOIL. Retro formats hold no EXIF, IPTC or XMP details.";
            return r;
        }
        byte[] data = Util.ReadShared(path);
        if (JpegRotate.IsJpeg(path)) rawXmp = JpegRating.ReadXmp(data);
        else if (PngRating.IsPng(path)) rawXmp = PngRating.ReadXmp(data);
        try
        {
            using (var ms = new MemoryStream(data, false))
            {
                WI.BitmapDecoder dec = WI.BitmapDecoder.Create(ms, WI.BitmapCreateOptions.IgnoreColorProfile | WI.BitmapCreateOptions.PreservePixelFormat,
                                                               WI.BitmapCacheOption.None);
                string fmt = null;
                try { fmt = dec.CodecInfo.FriendlyName; } catch (Exception) { }
                if (fmt != null) Add(r, "File", "Format", fmt);
                if (dec.Frames.Count > 0)
                {
                    WI.BitmapFrame f = dec.Frames[0];
                    Add(r, "File", "Dimensions", f.PixelWidth + " x " + f.PixelHeight);
                    if (dec.Frames.Count > 1) Add(r, "File", "Frames / pages", dec.Frames.Count.ToString(CultureInfo.InvariantCulture));
                    try { Add(r, "File", "Resolution", Math.Round(f.DpiX) + " x " + Math.Round(f.DpiY) + " dpi"); } catch (Exception) { }
                    try { Add(r, "File", "Pixel format", f.Format.ToString() + " (" + f.Format.BitsPerPixel + " bits per pixel)"); } catch (Exception) { }
                    try { if (f.ColorContexts != null && f.ColorContexts.Count > 0) Add(r, "File", "Colour profile", "embedded (ignored for display)"); } catch (Exception) { }
                }
                var all = new List<MetaEntry>();
                try { var cm = dec.Metadata; if (cm != null) Walk(cm, "", all, 0); } catch (Exception) { }
                if (dec.Frames.Count > 0)
                {
                    WI.BitmapMetadata md = null;
                    try { md = dec.Frames[0].Metadata as WI.BitmapMetadata; } catch (Exception) { }
                    if (md != null) Walk(md, "", all, 0);
                }
                List<MetaEntry> merged = Merge(all);
                if (merged.Count == 0) note = "This file holds no EXIF, IPTC or XMP details.";
                r.AddRange(merged);
            }
        }
        catch (Exception ex)
        {
            note = ex is NotSupportedException || ex is System.Runtime.InteropServices.COMException
                 ? "Windows has no reader for this file's details." : "The file's details could not be read: " + Util.Why(ex);
        }
        return r;
    }

    static void Add(List<MetaEntry> r, string g, string n, string v)
    {
        if (!string.IsNullOrEmpty(v)) r.Add(new MetaEntry { Group = g, Name = n, Value = v });
    }

    // every value in a metadata block and the blocks inside it (depth-limited)
    static void Walk(WI.BitmapMetadata md, string prefix, List<MetaEntry> r, int depth)
    {
        if (depth > 8 || r.Count > 5000) return;
        var names = new List<string>();
        try { foreach (string q in md) names.Add(q); } catch (Exception) { return; }
        foreach (string q in names)
        {
            object v;
            try { v = md.GetQuery(q); } catch (Exception) { continue; }
            if (v == null) continue;
            string full = prefix + q;
            var child = v as WI.BitmapMetadata;
            if (child != null) { Walk(child, full, r, depth + 1); continue; }
            string group, name; int tag;
            Classify(full, out group, out name, out tag);
            string text = Format(v, tag, group);
            if (!string.IsNullOrEmpty(text)) r.Add(new MetaEntry { Group = group, Name = name, Value = text });
        }
    }

    // values with the same name (keywords, the items of an XMP list) joined into one line
    static List<MetaEntry> Merge(List<MetaEntry> all)
    {
        var r = new List<MetaEntry>();
        var at = new Dictionary<string, MetaEntry>(StringComparer.Ordinal);
        foreach (MetaEntry e in all)
        {
            string key = e.Group + "\u0001" + e.Name;
            MetaEntry m;
            if (at.TryGetValue(key, out m)) { if (m.Value.Length < 4000 && m.Value != e.Value) m.Value += "; " + e.Value; continue; }
            m = new MetaEntry { Group = e.Group, Name = e.Name, Value = e.Value };
            at.Add(key, m);
            r.Add(m);
        }
        var pos = new Dictionary<MetaEntry, int>();
        for (int i = 0; i < r.Count; i++) pos[r[i]] = i;
        r.Sort(delegate (MetaEntry a, MetaEntry b)                        // by group, keeping the order found inside each
        {
            int c = GroupOrder(a.Group).CompareTo(GroupOrder(b.Group));
            return c != 0 ? c : pos[a].CompareTo(pos[b]);
        });
        return r;
    }

    static readonly string[] Groups = { "File", "EXIF (picture)", "EXIF (camera)", "GPS", "IPTC", "XMP", "PNG text", "PNG", "GIF", "JFIF", "EXIF (thumbnail)", "Other" };
    static int GroupOrder(string g) { int i = Array.IndexOf(Groups, g); return i < 0 ? Groups.Length : i; }

    // a WIC query path (e.g. /app1/ifd/exif/{ushort=33434}) -> group, readable name, EXIF tag (or -1)
    static void Classify(string full, out string group, out string name, out int tag)
    {
        string lower = full.ToLowerInvariant();
        string[] parts = full.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        string last = parts.Length > 0 ? parts[parts.Length - 1] : full;
        tag = TagOf(last);
        if (lower.Contains("/xmp"))
        {
            group = "XMP";
            var sb = new StringBuilder();
            bool after = false;
            foreach (string p in parts)
            {
                if (!after) { if (p.ToLowerInvariant() == "xmp") after = true; continue; }
                if (p.IndexOf(':') > 0 && !p.StartsWith("{", StringComparison.Ordinal)) { if (sb.Length > 0) sb.Append(" / "); sb.Append(p); }
            }
            name = sb.Length > 0 ? sb.ToString() : Strip(last);
            tag = -1;
            return;
        }
        if (lower.Contains("/iptc")) { group = "IPTC"; name = Strip(last); tag = -1; return; }
        if (lower.Contains("/gps/")) { group = "GPS"; name = tag >= 0 ? GpsName(tag) : Strip(last); return; }
        if (lower.Contains("/thumb/")) { group = "EXIF (thumbnail)"; name = tag >= 0 ? TagName(tag) : Strip(last); return; }
        if (lower.Contains("/exif/")) { group = "EXIF (camera)"; name = tag >= 0 ? TagName(tag) : Strip(last); return; }
        if (lower.Contains("/ifd/") || lower.StartsWith("/ifd", StringComparison.Ordinal))
        { group = "EXIF (picture)"; name = tag >= 0 ? TagName(tag) : Strip(last); return; }
        if (lower.Contains("/text/") || lower.Contains("/itxt/") || lower.Contains("/ztxt/"))
        { group = "PNG text"; name = parts.Length >= 2 && (Strip(last) == "TextEntry" || Strip(last) == "Keyword") ? Strip(parts[parts.Length - 2]) + " " + Strip(last) : Strip(last); tag = -1; return; }
        foreach (string png in new[] { "/chrm", "/gama", "/iccp", "/srgb", "/phys", "/time", "/bkgd", "/hist" })
            if (lower.StartsWith(png, StringComparison.Ordinal)) { group = "PNG"; name = Strip(parts[0]) + " " + Strip(last); tag = -1; return; }
        foreach (string gif in new[] { "/logscrdesc", "/imgdesc", "/grctlext", "/appext", "/commentext" })
            if (lower.StartsWith(gif, StringComparison.Ordinal)) { group = "GIF"; name = Strip(parts[0]) + " " + Strip(last); tag = -1; return; }
        if (lower.StartsWith("/app0", StringComparison.Ordinal)) { group = "JFIF"; name = Strip(last); tag = -1; return; }
        group = "Other"; name = full; tag = -1;
    }

    // "{ushort=271}" -> 271, else -1
    static int TagOf(string part)
    {
        if (!part.StartsWith("{ushort=", StringComparison.OrdinalIgnoreCase) || !part.EndsWith("}", StringComparison.Ordinal)) return -1;
        int v;
        return int.TryParse(part.Substring(8, part.Length - 9), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
    }

    // "{str=Keywords}" -> "Keywords"
    static string Strip(string part)
    {
        int eq = part.IndexOf('=');
        if (part.StartsWith("{", StringComparison.Ordinal) && part.EndsWith("}", StringComparison.Ordinal) && eq > 0) return part.Substring(eq + 1, part.Length - eq - 2);
        return part;
    }

    // ---- values

    static readonly HashSet<int> XpTags = new HashSet<int> { 0x9C9B, 0x9C9C, 0x9C9D, 0x9C9E, 0x9C9F };   // Windows' UTF-16 text tags

    static string Format(object v, int tag, string group)
    {
        bool ifd = group.StartsWith("EXIF", StringComparison.Ordinal) || group == "GPS";
        string s;
        try { s = Value(v, tag, ifd); } catch (Exception) { return null; }
        if (s == null) return null;
        var sb = new StringBuilder(Math.Min(s.Length, 400));
        foreach (char c in s)
        {
            if (sb.Length >= 400) { sb.Append("..."); break; }
            sb.Append(c < ' ' ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    static string Value(object v, int tag, bool ifd)
    {
        if (v == null) return null;
        var str = v as string;
        if (str != null) return str;
        var blob = v as WI.BitmapMetadataBlob;
        if (blob != null) return Bytes(blob.GetBlobValue(), tag);
        var bytes = v as byte[];
        if (bytes != null) return Bytes(bytes, tag);
        if (v is DateTime) return ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (v is System.Runtime.InteropServices.ComTypes.FILETIME)
        {
            var ft = (System.Runtime.InteropServices.ComTypes.FILETIME)v;
            return DateTime.FromFileTime(((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        if (v is ulong && ifd) return Ratio((ulong)v);
        if (v is long && ifd) return SRatio((long)v);
        var arr = v as Array;
        if (arr != null)
        {
            if (arr is char[]) return new string((char[])arr);
            var parts = new List<string>();
            foreach (object o in arr)
            {
                if (parts.Count == 16) { parts.Add("..."); break; }
                parts.Add(Value(o, -1, ifd));
            }
            return string.Join(", ", parts.ToArray());
        }
        if (v is double) return ((double)v).ToString("0.######", CultureInfo.InvariantCulture);
        if (v is float) return ((float)v).ToString("0.######", CultureInfo.InvariantCulture);
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    // EXIF rational numbers come as 64 bits: numerator in the low half, denominator in the high half
    static string Ratio(ulong u)
    {
        uint num = (uint)(u & 0xFFFFFFFFUL), den = (uint)(u >> 32);
        if (den == 0) return num.ToString(CultureInfo.InvariantCulture);
        if (num % den == 0) return (num / den).ToString(CultureInfo.InvariantCulture);
        if (num == 1) return "1/" + den.ToString(CultureInfo.InvariantCulture);
        return ((double)num / den).ToString("0.####", CultureInfo.InvariantCulture);
    }

    static string SRatio(long l)
    {
        int num = unchecked((int)(l & 0xFFFFFFFFL)), den = unchecked((int)(l >> 32));
        if (den == 0) return num.ToString(CultureInfo.InvariantCulture);
        if (num % den == 0) return (num / den).ToString(CultureInfo.InvariantCulture);
        return ((double)num / den).ToString("0.####", CultureInfo.InvariantCulture);
    }

    static string Bytes(byte[] b, int tag)
    {
        if (b == null || b.Length == 0) return null;
        if (tag == 0x927C) return "(maker notes, " + b.Length.ToString("#,0", CultureInfo.InvariantCulture) + " bytes)";
        if (XpTags.Contains(tag)) return Encoding.Unicode.GetString(b).TrimEnd('\0');
        int n = b.Length;
        while (n > 0 && b[n - 1] == 0) n--;
        if (n > 0 && n <= 400)
        {
            bool text = true;
            for (int i = 0; i < n && text; i++) text = b[i] >= 32 && b[i] < 127;
            if (text) return Encoding.ASCII.GetString(b, 0, n);
        }
        return "(binary data, " + b.Length.ToString("#,0", CultureInfo.InvariantCulture) + " bytes)";
    }

    // ---- tag names

    static readonly Dictionary<int, string> Tags = MakeTags();

    static Dictionary<int, string> MakeTags()
    {
        object[] t =
        {
            0x00FE, "Subfile type", 0x0100, "Image width", 0x0101, "Image height", 0x0102, "Bits per sample", 0x0103, "Compression",
            0x0106, "Photometric interpretation", 0x010E, "Image description", 0x010F, "Camera maker", 0x0110, "Camera model",
            0x0111, "Strip offsets", 0x0112, "Orientation", 0x0115, "Samples per pixel", 0x0116, "Rows per strip", 0x0117, "Strip byte counts",
            0x011A, "X resolution", 0x011B, "Y resolution", 0x011C, "Planar configuration", 0x0128, "Resolution unit",
            0x0131, "Software", 0x0132, "Date and time", 0x013B, "Artist", 0x013E, "White point", 0x013F, "Primary chromaticities",
            0x0201, "Thumbnail offset", 0x0202, "Thumbnail length", 0x0211, "YCbCr coefficients", 0x0213, "YCbCr positioning",
            0x0214, "Reference black / white", 0x02BC, "XMP packet", 0x4746, "Rating (Windows)", 0x4749, "Rating percent (Windows)",
            0x8298, "Copyright", 0x829A, "Exposure time", 0x829D, "F-number", 0x83BB, "IPTC block", 0x8769, "EXIF block",
            0x8822, "Exposure program", 0x8825, "GPS block", 0x8827, "ISO speed", 0x8830, "Sensitivity type", 0x8832, "Recommended exposure index",
            0x9000, "EXIF version", 0x9003, "Date taken", 0x9004, "Date digitised", 0x9010, "Time zone", 0x9011, "Time zone (taken)",
            0x9012, "Time zone (digitised)", 0x9101, "Components configuration", 0x9102, "Compressed bits per pixel",
            0x9201, "Shutter speed (APEX)", 0x9202, "Aperture (APEX)", 0x9203, "Brightness (APEX)", 0x9204, "Exposure bias",
            0x9205, "Maximum aperture (APEX)", 0x9206, "Subject distance", 0x9207, "Metering mode", 0x9208, "Light source", 0x9209, "Flash",
            0x920A, "Focal length", 0x9214, "Subject area", 0x927C, "Maker notes", 0x9286, "User comment", 0x9290, "Subseconds",
            0x9291, "Subseconds (taken)", 0x9292, "Subseconds (digitised)", 0x9C9B, "Title (Windows)", 0x9C9C, "Comment (Windows)",
            0x9C9D, "Author (Windows)", 0x9C9E, "Keywords (Windows)", 0x9C9F, "Subject (Windows)", 0xA000, "FlashPix version",
            0xA001, "Colour space", 0xA002, "Pixel width", 0xA003, "Pixel height", 0xA004, "Related sound file", 0xA005, "Interoperability block",
            0xA20E, "Focal plane X resolution", 0xA20F, "Focal plane Y resolution", 0xA210, "Focal plane resolution unit",
            0xA215, "Exposure index", 0xA217, "Sensing method", 0xA300, "File source", 0xA301, "Scene type", 0xA302, "CFA pattern",
            0xA401, "Custom rendered", 0xA402, "Exposure mode", 0xA403, "White balance", 0xA404, "Digital zoom",
            0xA405, "Focal length (35 mm)", 0xA406, "Scene capture type", 0xA407, "Gain control", 0xA408, "Contrast", 0xA409, "Saturation",
            0xA40A, "Sharpness", 0xA40C, "Subject distance range", 0xA420, "Unique picture ID", 0xA430, "Camera owner",
            0xA431, "Camera serial number", 0xA432, "Lens specification", 0xA433, "Lens maker", 0xA434, "Lens model",
            0xA435, "Lens serial number", 0xA460, "Composite image", 0xC4A5, "Print image matching", 0xEA1C, "Padding"
        };
        var d = new Dictionary<int, string>();
        for (int i = 0; i + 1 < t.Length; i += 2) d[(int)t[i]] = (string)t[i + 1];
        return d;
    }

    static string TagName(int tag)
    {
        string n;
        return Tags.TryGetValue(tag, out n) ? n : "Tag 0x" + tag.ToString("X4", CultureInfo.InvariantCulture);
    }

    static readonly string[] Gps =
    {
        "GPS version", "Latitude reference", "Latitude", "Longitude reference", "Longitude", "Altitude reference", "Altitude",
        "Time (UTC)", "Satellites", "Status", "Measure mode", "Precision (DOP)", "Speed unit", "Speed", "Direction of movement reference",
        "Direction of movement", "Picture direction reference", "Picture direction", "Map datum", "Destination latitude reference",
        "Destination latitude", "Destination longitude reference", "Destination longitude", "Destination bearing reference",
        "Destination bearing", "Destination distance reference", "Destination distance", "Positioning method", "Area name",
        "Date (UTC)", "Differential correction", "Horizontal error"
    };

    static string GpsName(int tag) { return tag >= 0 && tag < Gps.Length ? Gps[tag] : "GPS tag 0x" + tag.ToString("X4", CultureInfo.InvariantCulture); }
}

// ====================================================================== the fields RetroGleek can write (XMP, Dublin Core)

// Title, description, keywords, author and copyright, as the XMP standard keeps them (dc:title, dc:description,
// dc:subject, dc:creator, dc:rights). Windows Explorer, Lightroom, photo websites and most photo programs read these.
sealed class XmpFields
{
    public string Title = "", Description = "", Author = "", Copyright = "";
    public List<string> Keywords = new List<string>();

    const string NsDc = "http://purl.org/dc/elements/1.1/";
    const string NsRdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    const string NsXml = "http://www.w3.org/XML/1998/namespace";
    static readonly string[] Props = { "title", "description", "subject", "creator", "rights" };

    public static bool CanWrite(string path) { return JpegRotate.IsJpeg(path) || PngRating.IsPng(path); }

    // keywords and authors are typed separated by ";" (as in Windows Explorer)
    public static List<string> SplitList(string s)
    {
        var r = new List<string>();
        foreach (string part in (s ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = Clean(part);
            if (t.Length > 0 && !r.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) r.Add(t);
        }
        return r;
    }

    static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s ?? "") sb.Append(c < ' ' && c != '\n' ? ' ' : c);
        return sb.ToString().Replace("\r", "").Trim();
    }

    public XmpFields Normalized()
    {
        var n = new XmpFields { Title = Clean(Title).Replace("\n", " "), Description = Clean(Description), Copyright = Clean(Copyright).Replace("\n", " ") };
        n.Author = string.Join("; ", SplitList(Author).ToArray());
        n.Keywords = SplitList(string.Join(";", Keywords.ToArray()));
        return n;
    }

    public bool SameAs(XmpFields o)
    {
        if (Title != o.Title || Description != o.Description || Author != o.Author || Copyright != o.Copyright || Keywords.Count != o.Keywords.Count) return false;
        for (int i = 0; i < Keywords.Count; i++) if (Keywords[i] != o.Keywords[i]) return false;
        return true;
    }

    // the fields in an XMP packet (null or unreadable: all empty)
    public static XmpFields Read(string packet)
    {
        var f = new XmpFields();
        if (string.IsNullOrEmpty(packet)) return f;
        XmlDocument doc;
        try { doc = JpegRating.LoadXmp(packet); } catch (IOException) { return f; }
        var nm = new XmlNamespaceManager(doc.NameTable);
        nm.AddNamespace("rdf", NsRdf);
        foreach (XmlElement d in doc.SelectNodes("//rdf:Description", nm))
        {
            foreach (string p in Props)
            {
                List<string> vals = Values(d, p);
                if (vals.Count == 0) continue;
                switch (p)
                {
                    case "title": if (f.Title.Length == 0) f.Title = vals[0]; break;
                    case "description": if (f.Description.Length == 0) f.Description = vals[0]; break;
                    case "rights": if (f.Copyright.Length == 0) f.Copyright = vals[0]; break;
                    case "creator": if (f.Author.Length == 0) f.Author = string.Join("; ", vals.ToArray()); break;
                    default: if (f.Keywords.Count == 0) f.Keywords = vals; break;
                }
            }
        }
        return f.Normalized();
    }

    // a dc property's values: an attribute, plain text, or the items of an rdf:Alt / Bag / Seq (Alt: x-default first)
    static List<string> Values(XmlElement d, string prop)
    {
        var r = new List<string>();
        if (d.HasAttribute(prop, NsDc)) { r.Add(d.GetAttribute(prop, NsDc)); return r; }
        foreach (XmlNode c in d.ChildNodes)
        {
            var e = c as XmlElement;
            if (e == null || e.LocalName != prop || e.NamespaceURI != NsDc) continue;
            XmlElement list = null;
            foreach (XmlNode cc in e.ChildNodes) { var ce = cc as XmlElement; if (ce != null && ce.NamespaceURI == NsRdf) list = ce; }
            if (list == null) { r.Add(e.InnerText); continue; }
            string def = null;
            foreach (XmlNode li in list.ChildNodes)
            {
                var le = li as XmlElement;
                if (le == null || le.LocalName != "li") continue;
                if (list.LocalName == "Alt")
                {
                    if (le.GetAttribute("lang", NsXml) == "x-default") def = le.InnerText;
                    else if (r.Count == 0) r.Add(le.InnerText);
                }
                else r.Add(le.InnerText);
            }
            if (def != null) { r.Clear(); r.Add(def); }
        }
        return r;
    }

    // the XMP packet with these fields (old = the file's packet, or null to make one); every other property is kept.
    // An empty field removes the property. Alternative-language titles and descriptions are replaced by this one.
    public string Apply(string old)
    {
        if (old == null)
            old = "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + NsRdf +
                  "\"><rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        XmlDocument doc = JpegRating.LoadXmp(old);
        var nm = new XmlNamespaceManager(doc.NameTable);
        nm.AddNamespace("rdf", NsRdf);
        XmlNode rdf = doc.SelectSingleNode("//rdf:RDF", nm);
        if (rdf == null) throw new IOException("its XMP block has an unexpected layout, so it was left as it was.");
        XmlNodeList descs = rdf.SelectNodes("rdf:Description", nm);
        foreach (XmlElement d in descs)
            foreach (string p in Props)
            {
                if (d.HasAttribute(p, NsDc)) d.RemoveAttribute(p, NsDc);
                var gone = new List<XmlNode>();
                foreach (XmlNode c in d.ChildNodes) if (c.NodeType == XmlNodeType.Element && c.LocalName == p && c.NamespaceURI == NsDc) gone.Add(c);
                foreach (XmlNode c in gone) d.RemoveChild(c);
            }
        XmlElement target = descs.Count > 0 ? (XmlElement)descs[0] : null;
        if (target == null)
        {
            target = doc.CreateElement("rdf", "Description", NsRdf);
            target.SetAttribute("about", NsRdf, "");
            rdf.AppendChild(target);
        }
        XmpFields f = Normalized();
        if (f.Title.Length > 0) target.AppendChild(List(doc, "title", "Alt", new List<string> { f.Title }));
        if (f.Description.Length > 0) target.AppendChild(List(doc, "description", "Alt", new List<string> { f.Description }));
        if (f.Keywords.Count > 0) target.AppendChild(List(doc, "subject", "Bag", f.Keywords));
        if (f.Author.Length > 0) target.AppendChild(List(doc, "creator", "Seq", SplitList(f.Author)));
        if (f.Copyright.Length > 0) target.AppendChild(List(doc, "rights", "Alt", new List<string> { f.Copyright }));
        return JpegRating.SaveXmp(doc);
    }

    static XmlElement List(XmlDocument doc, string prop, string kind, List<string> items)
    {
        XmlElement e = doc.CreateElement("dc", prop, NsDc);
        XmlElement l = doc.CreateElement("rdf", kind, NsRdf);
        e.AppendChild(l);
        foreach (string s in items)
        {
            XmlElement li = doc.CreateElement("rdf", "li", NsRdf);
            if (kind == "Alt")
            {
                XmlAttribute a = doc.CreateAttribute("xml", "lang", NsXml);
                a.Value = "x-default";
                li.Attributes.Append(a);
            }
            li.InnerText = s;
            l.AppendChild(li);
        }
        return e;
    }

    // Writes the fields into a JPEG or PNG file's XMP. Throws with a readable reason when the file is left as it was.
    public static void Write(string path, XmpFields fields)
    {
        if ((File.GetAttributes(path) & Util.CloudMask) != 0) throw new IOException("cloud-only, not stored on this PC.");
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("the file is read-only.");
        XmpFields want = fields.Normalized();
        Func<string, string> change = delegate (string old) { return want.Apply(old); };
        bool jpeg = JpegRotate.IsJpeg(path);
        Action<string, Image> confirm = delegate (string file, Image img)
        {
            byte[] d = File.ReadAllBytes(file);
            XmpFields back = Read(jpeg ? JpegRating.ReadXmp(d) : PngRating.ReadXmp(d));
            if (!back.SameAs(want)) throw new IOException("the new details could not be confirmed, so nothing was changed.");
        };
        if (jpeg) JpegRating.Rewrite(path, change, null, confirm);
        else if (PngRating.IsPng(path)) PngRating.Rewrite(path, change, null, confirm);
        else throw new IOException("only JPEG and PNG files can be changed by RetroGleek.");
    }
}

// ====================================================================== the Properties window

sealed class MetaForm : Form
{
    public Action<string> FileChanged;                     // after the details were written into the file

    readonly string path;
    readonly Action<Form> darkTitle;
    readonly ListView list;
    readonly TextBox search;
    readonly Label head;
    readonly Button editBtn, xmpBtn, copyBtn;
    List<MetaEntry> entries = new List<MetaEntry>();
    string rawXmp, note;
    bool busy;

    public MetaForm(string file, Action<Form> dark)
    {
        path = file; darkTitle = dark;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9.5f);
        Text = "Properties - " + Path.GetFileName(file) + " - RetroGleek";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(38, 38, 38); ForeColor = Color.Gainsboro;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(Util.S(900), wa.Width * 9 / 10), Math.Min(Util.S(680), wa.Height * 9 / 10));
        MinimumSize = new Size(Util.S(560), Util.S(360));
        KeyPreview = true;
        if (dark != null) HandleCreated += delegate { dark(this); };
        Color boxBg = Color.FromArgb(52, 52, 52);

        list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowGroups = true,
            BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(32, 32, 32), ForeColor = Color.Gainsboro
        };
        Util.Dark(list);
        list.Columns.Add("Detail", Util.S(240));
        list.Columns.Add("Value", Util.S(600));

        head = new Label { Dock = DockStyle.Top, Height = Util.S(44), Padding = new Padding(Util.S(10), Util.S(6), Util.S(10), 0), ForeColor = Color.Gainsboro, Text = "Reading..." };
        var top = new Panel { Dock = DockStyle.Top, Height = Util.S(36), Padding = new Padding(Util.S(10), Util.S(4), Util.S(10), Util.S(4)), BackColor = Color.FromArgb(38, 38, 38) };
        search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = boxBg, ForeColor = Color.White };
        var sLab = new Label { Text = "Search:", Dock = DockStyle.Left, Width = Util.S(60), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver };
        top.Controls.Add(search); top.Controls.Add(sLab);
        search.TextChanged += delegate { Fill(); };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Util.S(46), Padding = new Padding(Util.S(8), Util.S(8), Util.S(8), 0), BackColor = Color.FromArgb(38, 38, 38), WrapContents = false };
        Func<string, Color, Button> button = delegate (string s, Color back)
        {
            var b = new Button { Text = s, AutoSize = true, Height = Util.S(30), FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White, Margin = new Padding(0, 0, Util.S(8), 0) };
            b.FlatAppearance.BorderSize = 0;
            bar.Controls.Add(b);
            return b;
        };
        editBtn = button("Edit title, description, keywords...", Color.FromArgb(60, 130, 230));
        xmpBtn = button("Show the XMP text", boxBg);
        copyBtn = button("Copy all", boxBg);
        Button close = button("Close", boxBg);
        editBtn.Click += delegate { Edit(); };
        xmpBtn.Click += delegate { ShowText("XMP - " + Path.GetFileName(path), rawXmp); };
        copyBtn.Click += delegate { Copy(false); };
        close.Click += delegate { Close(); };
        KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.Control && e.KeyCode == Keys.C && !search.Focused) { Copy(true); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { search.Focus(); search.SelectAll(); e.Handled = true; }
        };

        Controls.Add(list);                                 // fill first, edge-docked after
        Controls.Add(bar);
        Controls.Add(top);
        Controls.Add(head);
        UpdateButtons();
        Shown += delegate { ReadDetails(); };
    }

    void UpdateButtons()
    {
        editBtn.Enabled = !busy && XmpFields.CanWrite(path) && File.Exists(path);
        xmpBtn.Enabled = !busy && rawXmp != null;
        copyBtn.Enabled = !busy && entries.Count > 0;
    }

    void ReadDetails()
    {
        busy = true; UpdateButtons();
        head.Text = Path.GetFileName(path) + "\nReading...";
        string p = path;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<MetaEntry> got = null; string xmp = null, nt = null;
            try { got = MetaReader.Read(p, out xmp, out nt); }
            catch (Exception ex) { nt = "The file could not be read: " + Util.Why(ex); got = new List<MetaEntry>(); }
            Util.Post(this, delegate
            {
                busy = false;
                entries = got; rawXmp = xmp; note = nt;
                head.Text = Path.GetFileName(path) + "\n" + (note ?? entries.Count + " details" + (XmpFields.CanWrite(path) ? "   (title, description, keywords, author and copyright can be edited)" : ""));
                Fill();
                UpdateButtons();
            });
        });
    }

    void Fill()
    {
        string q = search.Text.Trim();
        list.BeginUpdate();
        try
        {
            list.Items.Clear(); list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (MetaEntry e in entries)
            {
                if (q.Length > 0 && e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && e.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                    && e.Group.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ListViewGroup g;
                if (!groups.TryGetValue(e.Group, out g)) { g = new ListViewGroup(e.Group); groups.Add(e.Group, g); list.Groups.Add(g); }
                var it = new ListViewItem(new[] { e.Name, e.Value }, g) { Tag = e };
                list.Items.Add(it);
            }
        }
        finally { list.EndUpdate(); }
    }

    void Copy(bool selectedOnly)
    {
        var sb = new StringBuilder();
        string last = null;
        foreach (ListViewItem it in list.Items)
        {
            if (selectedOnly && list.SelectedItems.Count > 0 && !it.Selected) continue;
            var e = (MetaEntry)it.Tag;
            if (e.Group != last) { if (sb.Length > 0) sb.Append("\r\n"); sb.Append("[").Append(e.Group).Append("]\r\n"); last = e.Group; }
            sb.Append(e.Name).Append(": ").Append(e.Value).Append("\r\n");
        }
        if (sb.Length == 0) return;
        try { Clipboard.SetText(sb.ToString()); head.Text = Path.GetFileName(path) + "\nCopied to the clipboard"; }
        catch (Exception) { }
    }

    void ShowText(string title, string text)
    {
        if (text == null) return;
        using (var f = new Form())
        {
            f.AutoScaleMode = AutoScaleMode.None;
            f.Text = title; f.StartPosition = FormStartPosition.CenterParent; f.ShowInTaskbar = false; f.MinimizeBox = false;
            f.BackColor = Color.FromArgb(38, 38, 38);
            f.ClientSize = new Size(Util.S(760), Util.S(540));
            if (darkTitle != null) f.HandleCreated += delegate { darkTitle(f); };
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(32, 32, 32), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 10f),
                Text = Pretty(text)
            };
            Util.Dark(box);
            f.KeyPreview = true;
            f.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.Controls.Add(box);
            f.Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; };
            f.ShowDialog(this);
        }
    }

    // the XMP indented for reading (as it is, if it can't be parsed)
    static string Pretty(string xmp)
    {
        try
        {
            XmlDocument doc = JpegRating.LoadXmp(xmp);
            var ws = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\r\n" };
            var sb = new StringBuilder();
            var d2 = new XmlDocument { XmlResolver = null };
            d2.LoadXml(doc.OuterXml.Trim());
            using (XmlWriter xw = XmlWriter.Create(sb, ws)) d2.Save(xw);
            return sb.ToString();
        }
        catch (Exception) { return xmp.Replace("\r\n", "\n").Replace("\n", "\r\n"); }
    }

    // ---- editing the five fields

    void Edit()
    {
        if (busy || !XmpFields.CanWrite(path)) return;
        XmpFields cur = XmpFields.Read(rawXmp);
        // older copies that other programs may show instead
        var older = new List<string>();
        var exifCopies = new HashSet<string> { "Image description", "Artist", "Copyright", "Title (Windows)", "Comment (Windows)",
                                               "Author (Windows)", "Keywords (Windows)", "Subject (Windows)" };
        foreach (MetaEntry e in entries)
        {
            string where = e.Group == "EXIF (picture)" && exifCopies.Contains(e.Name) ? "EXIF" : e.Group == "IPTC" ? "IPTC" : null;
            if (where != null && !older.Contains(where)) older.Add(where);
        }
        XmpFields edited = Ask(cur, older);
        if (edited == null) return;
        if (edited.Normalized().SameAs(cur.Normalized())) return;
        busy = true; UpdateButtons();
        head.Text = Path.GetFileName(path) + "\nWriting the details into the file...";
        string p = path;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null;
            try
            {
                var fi = new FileInfo(p);
                long oldSize = fi.Length, oldMod = fi.LastWriteTimeUtc.Ticks;
                XmpFields.Write(p, edited);
                Marks.Rebind(p, oldSize, oldMod);                 // ratings, labels and tags stay with the file
            }
            catch (Exception ex) { err = Util.Why(ex); }
            Util.Post(this, delegate
            {
                busy = false;
                if (err != null)
                {
                    UpdateButtons();
                    head.Text = Path.GetFileName(path) + "\nNothing was changed";
                    MessageBox.Show(this, "The details could not be written into the file: " + err, "Properties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (FileChanged != null) FileChanged(path);
                ReadDetails();
            });
        });
    }

    XmpFields Ask(XmpFields cur, List<string> older)
    {
        using (var f = new Form())
        {
            f.AutoScaleMode = AutoScaleMode.None;
            f.Font = Font;
            f.Text = "Edit the details - " + Path.GetFileName(path);
            f.StartPosition = FormStartPosition.CenterParent; f.ShowInTaskbar = false; f.MinimizeBox = false; f.MaximizeBox = false;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.BackColor = Color.FromArgb(38, 38, 38); f.ForeColor = Color.Gainsboro;
            if (darkTitle != null) f.HandleCreated += delegate { darkTitle(f); };
            int x = Util.S(12), lw = Util.S(100), bw = Util.S(460), y = Util.S(12), rowH = Util.S(30);
            Func<string, string, int, TextBox> field = delegate (string label, string value, int lines)
            {
                f.Controls.Add(new Label { Text = label, Left = x, Top = y + Util.S(3), Width = lw, Height = Util.S(22), ForeColor = Color.Silver });
                var t = new TextBox
                {
                    Left = x + lw, Top = y, Width = bw, Height = lines > 1 ? Util.S(22) * lines : Util.S(24), Multiline = lines > 1,
                    ScrollBars = lines > 1 ? ScrollBars.Vertical : ScrollBars.None, Text = value.Replace("\n", "\r\n"),
                    BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(52, 52, 52), ForeColor = Color.White
                };
                if (lines > 1) t.AcceptsReturn = true;
                f.Controls.Add(t);
                y += (lines > 1 ? t.Height + Util.S(6) : rowH);
                return t;
            };
            TextBox title = field("Title", cur.Title, 1);
            TextBox desc = field("Description", cur.Description, 4);
            TextBox keys = field("Keywords", string.Join("; ", cur.Keywords.ToArray()), 1);
            TextBox author = field("Author", cur.Author, 1);
            TextBox copy = field("Copyright", cur.Copyright, 1);
            string text = "Separate keywords and authors with ;   Leave a box empty to remove that detail.\r\n" +
                          "This changes the file: only its XMP block is rewritten (the picture is copied byte for byte), and the new file is " +
                          "checked before it replaces the old one." +
                          (older.Count > 0 ? "\r\nThis file also has older copies of some details in " + string.Join(" and ", older.ToArray()) +
                                             ", which some programs show instead. RetroGleek leaves those as they are." : "");
            var info = new Label { Text = text, Left = x, Top = y + Util.S(4), Width = lw + bw, Height = Util.S(older.Count > 0 ? 84 : 56), ForeColor = Color.FromArgb(160, 160, 160) };
            f.Controls.Add(info);
            y = info.Bottom + Util.S(8);
            var ok = new Button { Text = "Write into the file", Left = x + lw + bw - Util.S(260), Top = y, Width = Util.S(150), Height = Util.S(30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 130, 230), ForeColor = Color.White, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = x + lw + bw - Util.S(100), Top = y, Width = Util.S(100), Height = Util.S(30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(52, 52, 52), ForeColor = Color.White, DialogResult = DialogResult.Cancel };
            ok.FlatAppearance.BorderSize = 0; cancel.FlatAppearance.BorderSize = 0;
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.ClientSize = new Size(x * 2 + lw + bw, y + Util.S(42));
            if (f.ShowDialog(this) != DialogResult.OK) return null;
            return new XmpFields
            {
                Title = title.Text, Description = desc.Text, Author = author.Text, Copyright = copy.Text,
                Keywords = XmpFields.SplitList(keys.Text)
            };
        }
    }
}
