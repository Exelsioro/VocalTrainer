using System.IO;

namespace VocalTrainer;

public static class AppDataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocalTrainer");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string Recordings => Path.Combine(Root, "Recordings");
    public static string Songs => Path.Combine(Root, "Songs");
}
