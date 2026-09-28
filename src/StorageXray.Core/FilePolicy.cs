namespace StorageXray.Core;

public static class FilePolicy
{
    public const FileAttributes UnavailableAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
    private static readonly HashSet<string> ProtectedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin",
        "System Volume Information", "Recovery", "$WINDOWS.~BT", "$WinREAgent", "WindowsApps",
        "steamapps", "Epic Games", "XboxGames", "GOG Galaxy", ".git", ".svn", "node_modules",
        ".venv", "venv", "StorageXray"
    };
    private static readonly HashSet<string> PersonalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".tif", ".tiff", ".raw",
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".m4v", ".mp3", ".wav", ".flac", ".m4a", ".ogg",
        ".pdf", ".txt", ".md", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".rtf",
        ".zip", ".7z", ".rar", ".tar", ".gz", ".iso", ".epub", ".psd", ".blend"
    };
    public static string[] Segments(string path) => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
    public static bool IsPersonalFile(string path, FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReadOnly | UnavailableAttributes)) != 0) return false;
        if (IsCloudPath(path)) return false;
        if (Segments(path).Any(ProtectedSegments.Contains)) return false;
        return PersonalExtensions.Contains(System.IO.Path.GetExtension(path));
    }
    public static string Category(string path)
    {
        string[] segments = Segments(path);
        if (segments.Any(s => s.Equals("steamapps", StringComparison.OrdinalIgnoreCase) || s.Equals("XboxGames", StringComparison.OrdinalIgnoreCase) || s.Equals("Epic Games", StringComparison.OrdinalIgnoreCase))) return "Games";
        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".m4v" => "Videos",
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".heic" or ".bmp" or ".raw" or ".tif" or ".tiff" => "Photos",
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" => "Audio",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".iso" => "Archives",
            ".pdf" or ".txt" or ".md" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".csv" or ".ppt" or ".pptx" or ".rtf" or ".epub" => "Documents",
            ".exe" or ".dll" or ".msi" or ".sys" => "Apps & system",
            _ => "Other"
        };
    }
    public static string Explain(string path)
    {
        var segments = Segments(path);
        bool Has(string name) => segments.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (Has("shadercache")) return "Game shader cache. Your game client manages this; rebuilding it may cause stutter. Review it in the game client.";
        if (Has("workshop")) return "Steam Workshop content, usually mods. Manage subscriptions in Steam so they are not downloaded again.";
        if (Has("steamapps") || Has("XboxGames") || Has("Epic Games")) return "Installed game content. Use your game launcher to uninstall or move it; deleting individual files can break the game.";
        if (Has("Windows")) return "Windows operating-system files. Use Windows Storage settings for supported cleanup.";
        if (Has("AppData")) return "App settings, caches, and sometimes saved games. StorageXray does not include this folder in cleanup plans.";
        if (Has("Program Files") || Has("Program Files (x86)") || Has("ProgramData")) return "Installed applications or their shared data. Use the application's uninstaller or storage controls.";
        if (Has("Downloads")) return "Downloaded files. Older archives can be worth reviewing, but age alone does not make a file disposable.";
        if (Has("OneDrive") || Has("Dropbox") || Has("Google Drive")) return "Cloud-synced folder. Changing local files may change cloud copies too. Use your sync provider's controls.";
        if (Has("node_modules") || Has(".git") || Has(".venv")) return "Developer project data. This is excluded from cleanup suggestions.";
        return "Size is the total readable file content inside this folder. Review files before removing anything; large does not mean unnecessary.";
    }
    public static bool IsCloudPath(string path) => Segments(path).Any(s => s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) || s.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) || s.Equals("Google Drive", StringComparison.OrdinalIgnoreCase));
    public static void CheckAncestors(string path)
    {
        string? current = System.IO.Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            var attr = File.GetAttributes(current);
            if ((attr & UnavailableAttributes) != 0)
                throw new IOException("Linked or cloud-only paths are excluded: " + current);
            current = System.IO.Path.GetDirectoryName(current);
        }
    }
    public static void ValidateUnchanged(FileRecord file)
    {
        // Cheap exclusions first; still recheck ancestors before hashing/recycling.
        if (!IsPersonalFile(file.Path, file.Attributes))
            throw new IOException("This file is excluded from cleanup: " + file.Name);
        CheckAncestors(file.Path);
        var current = new FileInfo(file.Path);
        if (!current.Exists || current.Length != file.Size || current.LastWriteTimeUtc.Ticks != file.ModifiedTicks)
            throw new IOException("File changed since the scan. Rescan before cleaning: " + file.Name);
        if (!IsPersonalFile(file.Path, current.Attributes) || IsCloudPath(file.Path))
            throw new IOException("This file is excluded from cleanup: " + file.Name);
    }
}
