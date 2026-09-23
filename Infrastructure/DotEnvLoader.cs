internal static class DotEnvLoader
{
    internal static void Load(string baseDirectory, string workingDirectory, bool testing)
    {
        // Tests must never read developer credentials, including output-directory copies.
        if (testing)
            return;
        string path = Path.Combine(baseDirectory, ".env");
        if (!File.Exists(path))
            path = Path.Combine(workingDirectory, ".env");
        if (!File.Exists(path))
            return;

        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            int separator = trimmed.IndexOf('=');
            if (separator < 1)
                continue;
            string key = trimmed[..separator].Trim();
            if (key.Length == 0)
                continue;
            string value = trimmed[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') ||
                (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            // An explicitly empty process value also wins on platforms that retain it.
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
