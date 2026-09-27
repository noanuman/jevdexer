namespace Jevdexer.Core.Search;

/// <summary>
/// The tag list: what each app is labeled with, and the words that map a query onto a tag offline
/// (query layer 2). Bump <see cref="Version"/> on any change: apps whose labels carry an
/// older version are labeled again. `Meaning` reads after "Is this app used for ..." or, starting
/// with "a"/"an", after "Is this app ...".
/// </summary>
public static class TagCatalog
{
    public const string Version = "2026-09-27.2";

    public static readonly TagList List = new(Version, new Dictionary<string, TagInfo>
    {
        ["game"] = new("a video game people play", ["game", "games", "gaming", "video games", "play"]),
        ["game_launcher"] = new("a launcher or store used to install and start games", ["launcher", "game store", "game library"]),
        ["shooter"] = new("a shooter game (FPS or third-person)", ["shooter", "fps", "shooting game", "battle royale"]),
        ["strategy"] = new("a strategy game", ["strategy", "rts", "moba", "tactics"]),
        ["sandbox"] = new("a sandbox or building game", ["sandbox", "building game", "crafting"]),
        ["rpg"] = new("a role-playing game", ["rpg", "role playing"]),
        ["racing"] = new("a racing game", ["racing", "driving game", "cars"]),
        ["sports"] = new("a sports game", ["sports game", "football", "soccer"]),
        ["puzzle_casual"] = new("a puzzle or casual game", ["puzzle", "casual game", "solitaire", "cards"]),
        ["video_editing"] = new("editing videos", ["video editing", "video editor", "edit video", "cut video"]),
        ["photo_editing"] = new("editing photos or images", ["photo editing", "photo editor", "edit photos", "image editor"]),
        ["illustration"] = new("drawing, illustration or graphic design", ["drawing", "draw", "illustration", "graphic design", "paint"]),
        ["modelling_3d"] = new("3D modelling, animation or a game engine", ["3d", "modelling", "modeling", "animation", "game engine"]),
        ["audio_production"] = new("producing or editing music and audio", ["music production", "daw", "audio editing", "make music"]),
        ["screen_recording"] = new("recording the screen", ["screen recording", "record screen", "screen recorder", "capture screen"]),
        ["streaming"] = new("live streaming", ["streaming", "stream", "live stream", "broadcast"]),
        ["video_player"] = new("watching videos", ["video player", "watch video", "movies", "play video"]),
        ["music_player"] = new("listening to music", ["music player", "music", "listen", "songs"]),
        ["office_documents"] = new("writing documents", ["documents", "write", "word processor", "letter", "essay"]),
        ["spreadsheets"] = new("spreadsheets", ["spreadsheet", "excel", "tables", "budget"]),
        ["presentations"] = new("presentations", ["presentation", "slides", "slideshow"]),
        ["notes"] = new("taking notes", ["notes", "note taking", "notepad"]),
        ["pdf"] = new("reading or editing PDFs", ["pdf", "pdf reader"]),
        ["email"] = new("email", ["email", "mail", "inbox"]),
        ["calendar_tasks"] = new("calendar, to-do lists or planning", ["calendar", "todo", "to do", "tasks", "planner"]),
        ["meetings"] = new("meeting notes or scheduling meetings", ["meeting", "meetings", "meeting notes"]),
        ["ide"] = new("writing software in an IDE", ["ide", "programming", "coding", "development"]),
        ["code_editor"] = new("editing code or text files", ["code editor", "text editor", "edit code"]),
        ["terminal"] = new("a command line or terminal", ["terminal", "command line", "shell", "console", "cmd"]),
        ["database"] = new("working with databases", ["database", "sql"]),
        ["version_control"] = new("version control such as Git", ["git", "version control"]),
        ["api_client"] = new("testing APIs or networks", ["api", "http client", "tunnel", "network"]),
        ["virtualization"] = new("virtual machines or containers", ["virtual machine", "vm", "docker", "containers"]),
        ["browser"] = new("browsing the web", ["browser", "web", "internet", "browse"]),
        ["messaging"] = new("chatting or messaging people", ["chat", "messaging", "messenger", "message"]),
        ["video_calls"] = new("video or voice calls", ["video call", "voice call", "call"]),
        ["social"] = new("social media", ["social media", "social"]),
        ["ai_assistant"] = new("an AI assistant or chatbot", ["ai", "assistant", "chatbot", "llm"]),
        ["file_manager"] = new("managing files and folders", ["files", "file manager", "explorer", "folders"]),
        ["archive"] = new("compressing or extracting archives", ["zip", "unzip", "archive", "rar", "extract"]),
        ["backup_sync"] = new("backup or cloud file sync", ["backup", "cloud", "sync", "cloud storage"]),
        ["security"] = new("antivirus, security or passwords", ["antivirus", "security", "passwords", "vpn"]),
        ["hardware"] = new("controlling hardware, drivers, peripherals or RGB lighting", ["drivers", "rgb", "mouse", "keyboard", "gpu", "hardware"]),
        ["system_tool"] = new("a Windows system or administration tool", ["system", "settings", "admin", "control panel"]),
        ["electronics"] = new("electronics or circuit design", ["circuit", "electronics", "spice", "pcb"]),
        ["science_math"] = new("science, math or typesetting (LaTeX)", ["math", "latex", "science", "calculator"]),
        ["anti_cheat"] = new("anti-cheat software for games", ["anti cheat", "anticheat"]),
        ["camera"] = new("taking photos or video with a webcam or camera", ["camera", "webcam", "selfie"]),
        ["clock"] = new("clocks, alarms, timers or stopwatches", ["clock", "alarm", "timer", "stopwatch"]),
        ["weather"] = new("weather forecasts", ["weather", "forecast"]),
        ["news"] = new("reading news", ["news", "headlines"]),
        ["voice_recording"] = new("recording voice or audio", ["voice recorder", "record audio", "record voice", "microphone"]),
    });

    /// <summary>The yes/no question asked about an app for one tag.</summary>
    public static string Question(string meaning) =>
        meaning.StartsWith("a ", StringComparison.Ordinal) || meaning.StartsWith("an ", StringComparison.Ordinal)
            ? $"Is this app {meaning}?"
            : $"Is this app used for {meaning}?";
}
