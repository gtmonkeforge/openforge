using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenForge.Infrastructure;

// Reads and edits a .env file in place: changed keys keep their line, new keys go at the end, and comments and
// unknown lines are left alone.
public static class DotEnvFile
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
            return values;

        foreach (string line in File.ReadAllLines(path))
        {
            if (Parse(line) is (string key, string value))
                values[key] = value;
        }

        return values;
    }

    private static (string Key, string Value)? Parse(string line)
    {
        string trimmed = line.Trim();

        if (trimmed == "" || trimmed.StartsWith('#') || !trimmed.Contains('='))
            return null;

        if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            trimmed = trimmed[7..].TrimStart();

        int index = trimmed.IndexOf('=');
        string key = trimmed[..index].Trim();
        string value = trimmed[(index + 1)..].Trim();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            value = value[1..^1];

        return key == "" ? null : (key, value);
    }

    private static string Format(string key, string value)
    {
        // Quote anything a shell or systemd's EnvironmentFile would split or misread.
        bool quote = value.Any(c => char.IsWhiteSpace(c) || c is '#' or '"' or '\'' or '$' or '`' or '\\');
        return quote ? $"{key}=\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" : $"{key}={value}";
    }

    // null removes the key.
    public static void Write(string path, IReadOnlyDictionary<string, string?> changes)
    {
        List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : ["# OpenForge settings. The admin panel edits this file; restart the server to apply changes."];
        var handled = new HashSet<string>(StringComparer.Ordinal);

        // Walking backwards finds the line that counts (the last one); earlier duplicates of a changed key are dropped.
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (Parse(lines[i]) is not (string key, _) || !changes.TryGetValue(key, out string? value))
                continue;

            if (value == null || !handled.Add(key))
                lines.RemoveAt(i);
            else
                lines[i] = Format(key, value);

            handled.Add(key);
        }

        foreach ((string key, string? value) in changes)
        {
            if (value != null && !handled.Contains(key))
                lines.Add(Format(key, value));
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

        // It holds passwords and API keys, so only the server's own user may read it.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(temp, path, true);
    }
}
