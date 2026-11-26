using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sunfleck.VisualStudio;

internal static class FleckModelDiscovery
{
    internal static string[] Discover(string root, string configuration, IEnumerable<string> configurations, IEnumerable<string>? projectRoots = null)
    {
        string projectRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nestedProjects = new HashSet<string>((projectRoots ?? Array.Empty<string>())
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(path => !string.Equals(path, projectRoot, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(new[] { ".git", ".vs", "node_modules", "obj" }, StringComparer.OrdinalIgnoreCase);
        foreach (string name in configurations)
        {
            if (configuration.Length != 0 && !string.Equals(name, configuration, StringComparison.OrdinalIgnoreCase))
            {
                excluded.Add(name);
            }
        }
        var pending = new Stack<string>();
        pending.Push(projectRoot);
        int visited = 0;
        while (pending.Count != 0 && visited++ < 512 && files.Count < 128)
        {
            string directory = pending.Pop();
            try
            {
                foreach (string file in Directory.GetFiles(directory, "*.fleckmodel.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(Path.GetFullPath(file));
                    if (files.Count == 128)
                    {
                        break;
                    }
                }
                // Reverse order because the stack visits the last pushed directory first.
                foreach (string child in Directory.GetDirectories(directory).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (!excluded.Contains(Path.GetFileName(child)) && !nestedProjects.Contains(child) &&
                        (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An inaccessible build output must not disable other projects.
            }
        }
        return files.ToArray();
    }

    internal static string[] Configured(string value, string? root)
    {
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in value.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = entry.Trim();
            if (path.Length == 0)
            {
                continue;
            }
            try
            {
                if (!Path.IsPathRooted(path))
                {
                    if (string.IsNullOrEmpty(root))
                    {
                        continue;
                    }
                    path = Path.Combine(root, path);
                }
                files.Add(Path.GetFullPath(path));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            {
                // Keep valid entries when one configured path is malformed.
            }
        }
        return files.ToArray();
    }
}
