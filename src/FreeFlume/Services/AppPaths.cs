// FreeFlume — data/config/download locations (Windows).
// Author: velkadyne
// Mirrors upstream apppaths.h: org namespace "velkadyne". On Windows, Qt's
// GenericDataLocation maps to %LOCALAPPDATA%.

using System;
using System.IO;

namespace FreeFlume.Services;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\velkadyne\FreeFlume — holds the SQLite database and settings.</summary>
    public static string DataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "velkadyne", "FreeFlume");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string DatabaseFile() => Path.Combine(DataDir(), "freeflume.db");

    public static string SettingsFile() => Path.Combine(DataDir(), "settings.json");

    /// <summary>%USERPROFILE%\Downloads\FreeFlume (default download folder).</summary>
    public static string DownloadsDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FreeFlume");
}
