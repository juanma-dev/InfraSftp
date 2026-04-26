using CommunityToolkit.Mvvm.ComponentModel;

namespace InfraSftp.Models;

public partial class FileNode : ObservableObject
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }

    // True for entries from a local-disk listing, false for SFTP entries. Used
    // by the context menu to hide Windows-only actions (Reveal in Explorer)
    // when the row belongs to a remote tab.
    public bool IsLocalFile { get; set; }

    // Multi-select state. Toggled by the panel click handler (plain/Ctrl/Shift)
    // and read by bulk operations. Reset implicitly on every directory listing
    // because each refresh creates fresh FileNode instances.
    [ObservableProperty] private bool _isSelected;

    // True while this row is the source of an active "Cut" clipboard. Bound to
    // a row class that dims the row, mirroring Windows Explorer's faint-icon
    // affordance. Cleared when the cut completes, the clipboard is replaced,
    // or the listing is refreshed (new FileNode instances default to false).
    [ObservableProperty] private bool _isCut;

    // On-demand folder size (#13). null = not computed yet, -1 = computing now,
    // ≥0 = computed total in bytes. SizeDisplay reads this for directories so
    // the UI just updates when the recursive walk finishes. ChangedNotification
    // for SizeDisplay is forwarded by the partial method below.
    [ObservableProperty] private long? _computedSize;
    partial void OnComputedSizeChanged(long? value) => OnPropertyChanged(nameof(SizeDisplay));

    public string Icon
    {
        get
        {
            if (Name == "..") return "⬆";
            if (IsDirectory) return "📁";
            return GetFileIcon(Name);
        }
    }

    // Hex colour paired with Icon. Bound via HexToBrushConverter → TextBlock.Foreground.
    // Monochrome glyphs rendered through a tinting path will pick this up; native colour-emoji
    // rendering on Windows keeps its own colours, so the worst case is "still looks fine".
    public string IconColor
    {
        get
        {
            if (Name == "..") return "#9CA3AF";      // neutral gray (parent-dir arrow)
            if (IsDirectory)  return "#F59E0B";      // amber — folders
            return GetFileIconColor(Name);
        }
    }

    public string SizeDisplay
    {
        get
        {
            if (Name == "..") return "";
            // Directory size only shows once the user has explicitly asked for it.
            if (IsDirectory)
            {
                if (ComputedSize == null) return "";
                if (ComputedSize == -1)  return "…";   // walk in progress
                return FormatSize(ComputedSize.Value);
            }
            return FormatSize(Size);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public string ModifiedDisplay
    {
        get
        {
            if (Name == "..") return "";
            if (LastModified == default) return "";
            var now = DateTime.Now;
            if (LastModified.Date == now.Date) return LastModified.ToString("HH:mm");
            if (LastModified.Year == now.Year) return LastModified.ToString("MMM dd");
            return LastModified.ToString("yyyy-MM-dd");
        }
    }

    // Extension → glyph mapping. Consistent across local and remote panels
    // (an SFTP server does not expose real OS icons). Uses Unicode emoji so it
    // renders without any image atlas on Windows/macOS/Linux.
    private static string GetFileIcon(string name)
    {
        var lower = name.ToLowerInvariant();

        // Special filenames (no extension, or ambiguous)
        if (lower == "dockerfile" || lower.StartsWith("dockerfile.") || lower.StartsWith("docker-compose")) return "🐳";
        if (lower == "makefile" || lower == "gnumakefile" || lower == "cmakelists.txt") return "🔨";
        if (lower.StartsWith("readme")) return "📖";
        if (lower.StartsWith("license") || lower.StartsWith("licence") || lower == "copying") return "⚖";
        if (lower == ".gitignore" || lower == ".gitattributes" || lower == ".gitmodules" || lower == ".gitkeep") return "🔀";
        if (lower == ".env" || lower.StartsWith(".env.")) return "🔐";
        if (lower == ".editorconfig" || lower == ".prettierrc" || lower == ".eslintrc") return "⚙";

        var ext = Path.GetExtension(lower);
        return ext switch
        {
            // Office documents
            ".doc" or ".docx" or ".odt" or ".rtf" or ".pages" => "📘",
            ".xls" or ".xlsx" or ".xlsm" or ".ods" or ".csv" or ".tsv" or ".numbers" => "📗",
            ".ppt" or ".pptx" or ".odp" or ".key" => "📙",
            ".pdf" => "📕",

            // Plain text / docs
            ".txt" or ".log" or ".out" => "📝",
            ".md" or ".markdown" or ".rst" or ".adoc" => "📖",

            // Images
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"
                or ".svg" or ".ico" or ".tif" or ".tiff" or ".heic" or ".avif" or ".raw" => "🖼",

            // Video
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".webm" or ".wmv"
                or ".flv" or ".m4v" or ".mpg" or ".mpeg" or ".3gp" => "🎬",

            // Audio
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac"
                or ".opus" or ".wma" or ".mid" or ".midi" => "🎵",

            // Archives
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2"
                or ".xz" or ".tgz" or ".tbz" or ".tbz2" or ".zst" or ".iso" or ".cab" => "📦",

            // Shell / scripts
            ".sh" or ".bash" or ".zsh" or ".fish" or ".ksh" => "🐚",
            ".ps1" or ".psm1" or ".psd1" => "💠",
            ".bat" or ".cmd" => "⚡",

            // Programming languages
            ".py" or ".pyw" or ".pyi" or ".ipynb" => "🐍",
            ".rb" or ".erb" or ".gemspec" => "💎",
            ".go" or ".mod" or ".sum" => "🐹",
            ".rs" => "🦀",
            ".java" or ".class" or ".jar" or ".gradle" => "☕",
            ".php" => "🐘",
            ".cs" or ".csproj" or ".sln" or ".razor" => "🔷",
            ".c" or ".h" => "⚙",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" => "⚙",
            ".js" or ".mjs" or ".cjs" or ".jsx" => "🟨",
            ".ts" or ".tsx" => "🟦",
            ".swift" => "🕊",
            ".kt" or ".kts" => "🅺",
            ".lua" => "🌙",
            ".r" or ".rmd" => "📊",
            ".pl" or ".pm" => "🐪",
            ".dart" => "🎯",
            ".scala" or ".sbt" => "📐",
            ".elm" => "🌳",
            ".clj" or ".cljs" => "🔵",
            ".ex" or ".exs" => "💧",

            // Web
            ".html" or ".htm" or ".xhtml" => "🌐",
            ".css" or ".scss" or ".sass" or ".less" or ".styl" => "🎨",
            ".vue" or ".svelte" or ".astro" => "🟢",

            // Data / config
            ".json" or ".jsonc" or ".json5" => "📋",
            ".yaml" or ".yml" => "📋",
            ".xml" or ".xsd" or ".xsl" or ".xaml" or ".axaml" => "📄",
            ".toml" or ".ini" or ".conf" or ".config" or ".cfg" or ".properties" => "⚙",
            ".sql" or ".db" or ".sqlite" or ".sqlite3" => "🗄",

            // Executables & installers
            ".exe" or ".msi" => "🚀",
            ".app" or ".dmg" => "🍎",
            ".deb" or ".rpm" or ".apk" or ".snap" or ".flatpak" or ".appimage" => "📥",
            ".dll" or ".so" or ".dylib" => "🧩",

            // Security / keys
            ".pem" or ".crt" or ".cer" or ".pub" or ".pfx" or ".p12" or ".asc" or ".gpg" or ".ppk" => "🔑",

            // Fonts
            ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" => "🔤",

            // Design / 3D
            ".psd" or ".ai" or ".xd" or ".fig" or ".sketch" => "🎨",
            ".blend" or ".obj" or ".fbx" or ".stl" or ".gltf" or ".glb" or ".dae" => "🧊",

            // Ebooks
            ".epub" or ".mobi" or ".azw" or ".azw3" => "📚",

            // Misc
            ".torrent" => "🌊",
            ".bak" or ".tmp" or ".swp" or ".swo" or ".cache" or ".lock" => "🕒",

            _ => "📄"
        };
    }

    // Colour palette paired with GetFileIcon. Kept in sync with the glyph categories so
    // both switches evolve together (a new extension added in one must be added here too).
    private static string GetFileIconColor(string name)
    {
        var lower = name.ToLowerInvariant();

        if (lower == "dockerfile" || lower.StartsWith("dockerfile.") || lower.StartsWith("docker-compose")) return "#2496ED";
        if (lower == "makefile" || lower == "gnumakefile" || lower == "cmakelists.txt") return "#A855F7";
        if (lower.StartsWith("readme")) return "#38BDF8";
        if (lower.StartsWith("license") || lower.StartsWith("licence") || lower == "copying") return "#EAB308";
        if (lower == ".gitignore" || lower == ".gitattributes" || lower == ".gitmodules" || lower == ".gitkeep") return "#F97316";
        if (lower == ".env" || lower.StartsWith(".env.")) return "#EAB308";
        if (lower == ".editorconfig" || lower == ".prettierrc" || lower == ".eslintrc") return "#9CA3AF";

        var ext = Path.GetExtension(lower);
        return ext switch
        {
            ".doc" or ".docx" or ".odt" or ".rtf" or ".pages" => "#2563EB",
            ".xls" or ".xlsx" or ".xlsm" or ".ods" or ".csv" or ".tsv" or ".numbers" => "#16A34A",
            ".ppt" or ".pptx" or ".odp" or ".key" => "#EA580C",
            ".pdf" => "#DC2626",

            ".txt" or ".log" or ".out" => "#9CA3AF",
            ".md" or ".markdown" or ".rst" or ".adoc" => "#38BDF8",

            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"
                or ".svg" or ".ico" or ".tif" or ".tiff" or ".heic" or ".avif" or ".raw" => "#EC4899",

            ".mp4" or ".avi" or ".mkv" or ".mov" or ".webm" or ".wmv"
                or ".flv" or ".m4v" or ".mpg" or ".mpeg" or ".3gp" => "#F43F5E",

            ".mp3" or ".wav" or ".flac" or ".ogg" or ".m4a" or ".aac"
                or ".opus" or ".wma" or ".mid" or ".midi" => "#8B5CF6",

            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2"
                or ".xz" or ".tgz" or ".tbz" or ".tbz2" or ".zst" or ".iso" or ".cab" => "#D97706",

            ".sh" or ".bash" or ".zsh" or ".fish" or ".ksh" => "#4ADE80",
            ".ps1" or ".psm1" or ".psd1" => "#38BDF8",
            ".bat" or ".cmd" => "#FACC15",

            ".py" or ".pyw" or ".pyi" or ".ipynb" => "#3B82F6",
            ".rb" or ".erb" or ".gemspec" => "#EF4444",
            ".go" or ".mod" or ".sum" => "#06B6D4",
            ".rs" => "#F97316",
            ".java" or ".class" or ".jar" or ".gradle" => "#EA580C",
            ".php" => "#8B5CF6",
            ".cs" or ".csproj" or ".sln" or ".razor" => "#A855F7",
            ".c" or ".h" => "#60A5FA",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" => "#3B82F6",
            ".js" or ".mjs" or ".cjs" or ".jsx" => "#FACC15",
            ".ts" or ".tsx" => "#3B82F6",
            ".swift" => "#F97316",
            ".kt" or ".kts" => "#A855F7",
            ".lua" => "#2563EB",
            ".r" or ".rmd" => "#3B82F6",
            ".pl" or ".pm" => "#06B6D4",
            ".dart" => "#06B6D4",
            ".scala" or ".sbt" => "#DC2626",
            ".elm" => "#22D3EE",
            ".clj" or ".cljs" => "#16A34A",
            ".ex" or ".exs" => "#A855F7",

            ".html" or ".htm" or ".xhtml" => "#F97316",
            ".css" or ".scss" or ".sass" or ".less" or ".styl" => "#3B82F6",
            ".vue" or ".svelte" or ".astro" => "#22C55E",

            ".json" or ".jsonc" or ".json5" => "#EAB308",
            ".yaml" or ".yml" => "#EF4444",
            ".xml" or ".xsd" or ".xsl" or ".xaml" or ".axaml" => "#F97316",
            ".toml" or ".ini" or ".conf" or ".config" or ".cfg" or ".properties" => "#9CA3AF",
            ".sql" or ".db" or ".sqlite" or ".sqlite3" => "#F59E0B",

            ".exe" or ".msi" => "#3B82F6",
            ".app" or ".dmg" => "#E5E7EB",
            ".deb" or ".rpm" or ".apk" or ".snap" or ".flatpak" or ".appimage" => "#DC2626",
            ".dll" or ".so" or ".dylib" => "#A855F7",

            ".pem" or ".crt" or ".cer" or ".pub" or ".pfx" or ".p12" or ".asc" or ".gpg" or ".ppk" => "#EAB308",

            ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" => "#EC4899",

            ".psd" or ".ai" or ".xd" or ".fig" or ".sketch" => "#F472B6",
            ".blend" or ".obj" or ".fbx" or ".stl" or ".gltf" or ".glb" or ".dae" => "#06B6D4",

            ".epub" or ".mobi" or ".azw" or ".azw3" => "#8B5CF6",

            ".torrent" => "#06B6D4",
            ".bak" or ".tmp" or ".swp" or ".swo" or ".cache" or ".lock" => "#6B7280",

            _ => "#9CA3AF"
        };
    }
}
