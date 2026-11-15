namespace MistyStep.App;

/// <summary>Command-line switches from the spec. M4 handles --minimized, --portable and --log-level debug.</summary>
internal sealed record AppOptions(bool Minimized, bool Portable, bool DebugLogging)
{
    public static AppOptions Parse(string[] args)
    {
        var minimized = false;
        var portable = false;
        var debug = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--minimized":
                    minimized = true;
                    break;
                case "--portable":
                    portable = true;
                    break;
                case "--log-level":
                    if (i + 1 < args.Length && args[i + 1].Equals("debug", StringComparison.OrdinalIgnoreCase)) debug = true;
                    i++;
                    break;
            }
        }

        return new AppOptions(minimized, portable, debug);
    }
}
