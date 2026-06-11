using System.Windows.Media;

namespace AgentDock.Services;

/// <summary>
/// Registry of built-in project/group icons available for tab buttons.
/// Uses Segoe MDL2 Assets glyphs — vector, theme-aware, no external assets needed.
/// Each icon carries search <see cref="IconInfo.Keywords"/> so the picker can be filtered by type.
/// </summary>
public static class BuiltInIcons
{
    public record IconInfo(string Name, string Label, string Glyph, string FontFamily, string Keywords = "");

    private const string Mdl2 = "Segoe MDL2 Assets";

    private static readonly IconInfo[] Icons =
    [
        // --- Files & folders ---
        new("folder",     "Folder",        "", Mdl2, "directory files documents"),
        new("newfolder",  "New Folder",    "", Mdl2, "directory create add files"),
        new("library",    "Library",       "", Mdl2, "books docs collection shelf"),
        new("document",   "Document",      "", Mdl2, "page file paper text doc"),
        new("page",       "Page",          "", Mdl2, "document blank sheet file"),
        new("bookmark",   "Bookmark",      "", Mdl2, "save tag favorite ribbon"),
        new("save",       "Save",          "", Mdl2, "disk floppy store"),
        new("copy",       "Copy",          "", Mdl2, "duplicate clipboard"),
        new("cut",        "Cut",           "", Mdl2, "scissors clipboard trim"),
        new("attach",     "Attach",        "", Mdl2, "paperclip clip file"),
        new("print",      "Print",         "", Mdl2, "printer paper output"),
        new("tag",        "Tag",           "", Mdl2, "label price category"),

        // --- Dev & tools ---
        new("code",       "Code",          "", Mdl2, "programming brackets developer source"),
        new("terminal",   "Terminal",      "", Mdl2, "console command prompt shell cli"),
        new("bug",        "Bug",           "", Mdl2, "insect debug issue error defect"),
        new("wrench",     "Wrench",        "", Mdl2, "repair tool fix maintenance"),
        new("settings",   "Settings",      "", Mdl2, "gear configure options preferences cog"),
        new("database",   "Database",      "", Mdl2, "data sql storage db table"),
        new("calculator", "Calculator",    "", Mdl2, "math compute numbers"),
        new("piechart",   "Pie Chart",     "", Mdl2, "analytics graph stats data report"),

        // --- Status & symbols ---
        new("star",       "Star",          "", Mdl2, "favorite rating bookmark"),
        new("starfill",   "Star Filled",   "", Mdl2, "favorite rating bookmark filled"),
        new("heart",      "Heart",         "", Mdl2, "love like favorite"),
        new("heartfill",  "Heart Filled",  "", Mdl2, "love like favorite filled"),
        new("flag",       "Flag",          "", Mdl2, "marker report milestone country"),
        new("pin",        "Pin",           "", Mdl2, "attach tack stick"),
        new("warning",    "Warning",       "", Mdl2, "alert caution danger triangle"),
        new("info",       "Info",          "", Mdl2, "information about details circle"),
        new("help",       "Help",          "", Mdl2, "question support faq"),
        new("accept",     "Check",         "", Mdl2, "checkmark done complete ok tick"),
        new("cancel",     "Close",         "", Mdl2, "x cross exit dismiss"),
        new("add",        "Add",           "", Mdl2, "plus new create"),
        new("remove",     "Remove",        "", Mdl2, "minus subtract"),
        new("more",       "More",          "", Mdl2, "ellipsis options menu dots"),
        new("delete",     "Delete",        "", Mdl2, "trash bin remove garbage"),
        new("bolt",       "Bolt",          "", Mdl2, "lightning power energy fast electric"),

        // --- Security ---
        new("shield",     "Shield",        "", Mdl2, "security protection defense safety"),
        new("lock",       "Lock",          "", Mdl2, "secure password private locked"),
        new("certificate","Certificate",   "", Mdl2, "award credential badge license"),

        // --- Navigation ---
        new("home",       "Home",          "", Mdl2, "house main dashboard start"),
        new("menu",       "Menu",          "", Mdl2, "hamburger navigation bars list"),
        new("search",     "Search",        "", Mdl2, "find magnify zoom lookup"),
        new("filter",     "Filter",        "", Mdl2, "funnel sort refine"),
        new("refresh",    "Refresh",       "", Mdl2, "reload sync update cycle"),
        new("back",       "Back",          "", Mdl2, "arrow previous left return"),
        new("forward",    "Forward",       "", Mdl2, "arrow next right"),
        new("chevrondown","Chevron Down",  "", Mdl2, "arrow expand caret"),
        new("chevronup",  "Chevron Up",    "", Mdl2, "arrow collapse caret"),
        new("link",       "Link",          "", Mdl2, "chain url hyperlink connect"),
        new("share",      "Share",         "", Mdl2, "send distribute social"),

        // --- Communication & people ---
        new("mail",       "Mail",          "", Mdl2, "email envelope message inbox"),
        new("comment",    "Comment",       "", Mdl2, "chat message bubble talk"),
        new("people",     "People",        "", Mdl2, "users team group contacts"),
        new("contact",    "Contact",       "", Mdl2, "person user profile card account"),
        new("phone",      "Phone",         "", Mdl2, "call mobile cell contact"),

        // --- Media ---
        new("camera",     "Camera",        "", Mdl2, "photo picture capture"),
        new("photo",      "Photo",         "", Mdl2, "picture image gallery"),
        new("video",      "Video",         "", Mdl2, "movie film record"),
        new("music",      "Music",         "", Mdl2, "note audio song sound"),
        new("mic",        "Microphone",    "", Mdl2, "voice record audio talk"),
        new("headphone",  "Headphone",     "", Mdl2, "audio music listen sound"),
        new("speaker",    "Speaker",       "", Mdl2, "audio sound output volume"),
        new("play",       "Play",          "", Mdl2, "start media run triangle"),
        new("pause",      "Pause",         "", Mdl2, "stop media halt"),
        new("stop",       "Stop",          "", Mdl2, "square halt end"),
        new("mute",       "Mute",          "", Mdl2, "silent sound off quiet"),
        new("volume",     "Volume",        "", Mdl2, "sound speaker audio loud"),
        new("fullscreen", "Full Screen",   "", Mdl2, "expand maximize zoom"),

        // --- Transfer ---
        new("download",   "Download",      "", Mdl2, "arrow save get import"),
        new("upload",     "Upload",        "", Mdl2, "arrow export send up"),
        new("send",       "Send",          "", Mdl2, "paper plane message submit"),

        // --- Places & travel ---
        new("mappin",     "Map Pin",       "", Mdl2, "location place gps marker"),
        new("web",        "Web",           "", Mdl2, "globe internet browser www site"),
        new("world",      "Globe",         "", Mdl2, "world map international earth global"),
        new("car",        "Car",           "", Mdl2, "vehicle drive transport auto"),
        new("airplane",   "Airplane",      "", Mdl2, "flight travel plane fly trip"),

        // --- Time ---
        new("calendar",   "Calendar",      "", Mdl2, "date schedule event week"),
        new("clock",      "Clock",         "", Mdl2, "time watch schedule"),
        new("stopwatch",  "Stopwatch",     "", Mdl2, "timer time countdown"),

        // --- Shopping ---
        new("shop",       "Shop",          "", Mdl2, "store bag buy retail"),
        new("cart",       "Shopping Cart", "", Mdl2, "buy store basket checkout"),
        new("gift",       "Gift",          "", Mdl2, "present box reward"),

        // --- Connectivity & devices ---
        new("cloud",      "Cloud",         "", Mdl2, "weather storage sky"),
        new("wifi",       "WiFi",          "", Mdl2, "wireless network signal internet"),
        new("bluetooth",  "Bluetooth",     "", Mdl2, "wireless connect pair"),
        new("power",      "Power",         "", Mdl2, "on off shutdown button"),
        new("devices",    "Devices",       "", Mdl2, "hardware monitor screen"),
        new("tablet",     "Tablet",        "", Mdl2, "device ipad slate"),
        new("keyboard",   "Keyboard",      "", Mdl2, "type input keys"),

        // --- Lifestyle & misc ---
        new("rocket",     "Rocket",        "", Mdl2, "launch startup deploy ship space"),
        new("game",       "Game",          "", Mdl2, "controller gaming play xbox"),
        new("paint",      "Paint",         "", Mdl2, "brush highlight art design"),
        new("color",      "Color",         "", Mdl2, "palette paint wheel hue"),
        new("brightness", "Brightness",    "", Mdl2, "sun light display day"),
        new("moon",       "Moon",          "", Mdl2, "night quiet dark sleep"),
        new("leaf",       "Leaf",          "", Mdl2, "nature eco plant green"),
        new("edit",       "Edit",          "", Mdl2, "pencil write modify change"),
        new("view",       "View",          "", Mdl2, "eye visible show preview"),
        // ============================================================
        // Extended set — glyphs verified against segmdl2.ttf by rendering.
        // ============================================================

        // --- Files, docs & editing ---
        new("openfile",    "Open File",     "", Mdl2, "open document load folder"),
        new("clipboard",   "Clipboard",     "", Mdl2, "copy paste board notes"),
        new("archive",     "Archive",       "", Mdl2, "box storage cabinet files drawer"),
        new("sort",        "Sort",          "", Mdl2, "order arrows updown rank arrange"),
        new("align",       "Align Text",    "", Mdl2, "paragraph format justify lines"),
        new("list",        "List",          "", Mdl2, "items rows bullets tasks lines"),
        new("book",        "Book",          "", Mdl2, "read reading pages literature manual"),
        new("text",        "Text",          "", Mdl2, "font type letter format characters"),
        new("pen",         "Pen",           "", Mdl2, "write ink fountain signature author"),
        new("eraser",      "Eraser",        "", Mdl2, "delete clear rubber remove"),

        // --- Dev, hardware & infrastructure ---
        new("chip",        "Chip",          "", Mdl2, "cpu processor hardware silicon"),
        new("server",      "Server",        "", Mdl2, "rack host datacenter backend"),
        new("monitor",     "Monitor",       "", Mdl2, "screen display crt"),
        new("desktop",     "Desktop",       "", Mdl2, "computer pc workstation screen"),
        new("laptop",      "Laptop",        "", Mdl2, "computer notebook portable device"),
        new("usb",         "USB",           "", Mdl2, "port connector drive plug"),
        new("router",      "Router",        "", Mdl2, "modem network gateway internet"),
        new("network",     "Network",       "", Mdl2, "globe nodes connections internet"),
        new("sitemap",     "Sitemap",       "", Mdl2, "hierarchy nodes tree org structure"),
        new("gears",       "Gears",         "", Mdl2, "settings cogs configure system mechanism"),
        new("sliders",     "Sliders",       "", Mdl2, "adjust controls equalizer tune settings"),
        new("vr",          "VR Headset",    "", Mdl2, "goggles reality glasses immersive"),

        // --- Charts & data ---
        new("linechart",   "Line Chart",    "", Mdl2, "graph trend analytics stats line"),
        new("barchart",    "Bar Chart",     "", Mdl2, "graph stats analytics bars column"),
        new("areachart",   "Area Chart",    "", Mdl2, "graph trend filled stats analytics"),
        new("dashboard",   "Dashboard",     "", Mdl2, "gauge speed meter speedometer"),
        new("trending",    "Trending",      "", Mdl2, "growth up increase rising stats"),
        new("signal",      "Signal",        "", Mdl2, "bars cellular reception network mobile"),

        // --- Status & symbols ---
        new("addcircle",   "Add (Circle)",  "", Mdl2, "plus add new create circle"),
        new("removecircle","Remove (Circle)","", Mdl2, "minus subtract remove delete circle"),
        new("like",        "Like",          "", Mdl2, "thumbs up approve favorite positive"),
        new("dislike",     "Dislike",       "", Mdl2, "thumbs down disapprove negative"),
        new("toggle",      "Toggle",        "", Mdl2, "switch on off setting enable"),
        new("magic",       "Magic",         "", Mdl2, "sparkle ai effects wand auto"),
        new("puzzle",      "Puzzle",        "", Mdl2, "extension plugin addon piece module"),
        new("award",       "Award",         "", Mdl2, "badge medal prize ribbon honor"),
        new("target",      "Target",        "", Mdl2, "aim goal focus bullseye objective"),
        new("circle",      "Circle",        "", Mdl2, "shape ring outline round"),
        new("triangle",    "Triangle",      "", Mdl2, "shape arrow up play"),
        new("square",      "Square",        "", Mdl2, "shape box outline stop"),

        // --- Security ---
        new("verified",    "Verified",      "", Mdl2, "shield check secure protected trust"),
        new("key",         "Key",           "", Mdl2, "password access unlock secret credential"),
        new("fingerprint", "Fingerprint",   "", Mdl2, "biometric identity touch scan"),
        new("unlock",      "Unlock",        "", Mdl2, "open padlock access unsecured"),

        // --- Navigation & arrows ---
        new("chevronleft", "Chevron Left",  "", Mdl2, "arrow previous back caret"),
        new("chevronright","Chevron Right", "", Mdl2, "arrow next forward caret"),
        new("arrowup",     "Arrow Up",      "", Mdl2, "increase top rise direction"),
        new("arrowdown",   "Arrow Down",    "", Mdl2, "decrease bottom fall direction"),
        new("navigation",  "Navigation",    "", Mdl2, "compass direction gps locate"),
        new("move",        "Move",          "", Mdl2, "drag reposition arrows pan"),
        new("cursor",      "Cursor",        "", Mdl2, "pointer mouse arrow select click"),
        new("shuffle",     "Shuffle",       "", Mdl2, "random mix crossover arrows"),
        new("touch",       "Touch",         "", Mdl2, "tap finger gesture press"),
        new("redo",        "Redo",          "", Mdl2, "forward repeat again arrow"),
        new("undo",        "Undo",          "", Mdl2, "back revert arrow rotate"),
        new("sync",        "Sync",          "", Mdl2, "refresh reload update circular arrows"),
        new("logout",      "Logout",        "", Mdl2, "exit signout leave door quit"),
        new("history",     "History",       "", Mdl2, "recent time clock past undo"),
        new("map",         "Map",           "", Mdl2, "location navigation atlas directions"),

        // --- Communication & people ---
        new("at",          "At Sign",       "", Mdl2, "mention email handle address"),
        new("adduser",     "Add User",      "", Mdl2, "person add new contact invite member"),
        new("presenter",   "Presenter",     "", Mdl2, "speaker present person talk teach"),
        new("bell",        "Bell",          "", Mdl2, "notification alert reminder ring"),
        new("megaphone",   "Megaphone",     "", Mdl2, "announce broadcast promote marketing"),
        new("broadcast",   "Broadcast",     "", Mdl2, "tower antenna signal radio transmit"),
        new("cast",        "Cast",          "", Mdl2, "wireless screen project stream airplay"),
        new("emoji",       "Emoji",         "", Mdl2, "smiley face mood reaction happy"),
        new("apps",        "Apps",          "", Mdl2, "tiles grid launcher applications dashboard"),
        new("translate",   "Translate",     "", Mdl2, "language localization globe i18n"),

        // --- Media & scanning ---
        new("playlist",    "Playlist",      "", Mdl2, "queue songs music list tracks"),
        new("barcode",     "Barcode",       "", Mdl2, "scan upc product code lines"),
        new("scan",        "Scan",          "", Mdl2, "scanner barcode read qr capture"),
        new("qrcode",      "QR Code",       "", Mdl2, "scan barcode code square matrix"),
        new("rewind",      "Rewind",        "", Mdl2, "back media previous reverse"),
        new("fastforward", "Fast Forward",  "", Mdl2, "forward media next skip"),

        // --- Places & travel ---
        new("train",       "Train",         "", Mdl2, "rail subway metro transit commute"),
        new("bus",         "Bus",           "", Mdl2, "transit vehicle public transport coach"),
        new("boat",        "Boat",          "", Mdl2, "ship ferry cruise water sail"),
        new("building",    "Building",      "", Mdl2, "office company corporate city tower"),
        new("bank",        "Bank",          "", Mdl2, "institution museum government finance columns"),
        new("walk",        "Walk",          "", Mdl2, "pedestrian walking directions person"),
        new("briefcase",   "Briefcase",     "", Mdl2, "work job business bag portfolio"),

        // --- Time & shopping ---
        new("schedule",    "Schedule",      "", Mdl2, "calendar time appointment plan"),
        new("pos",         "Point of Sale", "", Mdl2, "register checkout cashier sales till"),

        // --- Devices ---
        new("smartphone",  "Smartphone",    "", Mdl2, "phone mobile cell device handset"),
        new("battery",     "Battery",       "", Mdl2, "power charge energy level"),

        // --- Lifestyle, nature & misc ---
        new("idea",        "Idea",          "", Mdl2, "lightbulb tip hint suggestion bright"),
        new("education",   "Education",     "", Mdl2, "school graduation cap learn student"),
        new("drop",        "Drop",          "", Mdl2, "water droplet liquid"),
        new("fire",        "Fire",          "", Mdl2, "flame hot heat trending burn"),
        new("coffee",      "Coffee",        "", Mdl2, "cup drink break cafe tea"),
        new("pill",        "Pill",          "", Mdl2, "medicine drug health pharmacy capsule"),
        new("thermometer", "Thermometer",   "", Mdl2, "temperature heat weather climate"),
        new("weather",     "Weather",       "", Mdl2, "sunrise sunset horizon forecast climate"),
        new("ruler",       "Ruler",         "", Mdl2, "measure scale dimension design"),
        new("cube",        "Cube",          "", Mdl2, "3d box package model shape"),
        new("layers",      "Layers",        "", Mdl2, "stack levels overlay sheets"),
        new("grid",        "Grid",          "", Mdl2, "table cells layout tiles matrix"),
        new("checklist",   "Checklist",     "", Mdl2, "tasks todo list checkboxes done"),
    ];

    /// <summary>
    /// All available built-in icons.
    /// </summary>
    public static IReadOnlyList<IconInfo> All => Icons;

    /// <summary>
    /// Returns icons whose name, label or keywords match the query (case-insensitive;
    /// every whitespace-separated term must match). A blank query returns every icon.
    /// </summary>
    public static IEnumerable<IconInfo> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Icons;

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return Icons.Where(icon =>
        {
            var haystack = $"{icon.Name} {icon.Label} {icon.Keywords}";
            return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// Tries to find a built-in icon by name. Case-insensitive.
    /// </summary>
    public static IconInfo? Find(string name)
    {
        foreach (var icon in Icons)
        {
            if (icon.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return icon;
        }
        return null;
    }

    /// <summary>
    /// Checks if a name refers to a built-in icon.
    /// </summary>
    public static bool IsBuiltIn(string name) => Find(name) != null;

    /// <summary>
    /// The default icon used when no icon is configured or discovered.
    /// </summary>
    public static IconInfo Default => Icons[0]; // folder
}
