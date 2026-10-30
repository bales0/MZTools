namespace MZTools.Tests;

// Shared by legacy and portable runners, including builds whose artifacts live
// outside the repository. Prefer the copied fixture, then the original corpus.
internal static class TestMediaPaths
{
    internal static string Resolve(string folder, string name)
    {
        string copied = Path.Combine(AppContext.BaseDirectory, folder, name);
        if (File.Exists(copied)) return copied;
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "MZTools.sln")))
                    return Path.Combine(directory.FullName, "MZTool.Tests", folder, name);
        return copied;
    }
}
