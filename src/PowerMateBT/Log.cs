namespace PowerMateBT;

/// <summary>Small diagnostic log, recreated on each start, in %LOCALAPPDATA%\PowerMateBT.</summary>
internal static class Log
{
    // Declared before Writer: static initializers run in textual order.
    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerMateBT", "PowerMateBT.log");

    static readonly object Lock = new();
    static readonly StreamWriter? Writer = Open();

    public static void Write(string message)
    {
        if (Writer is null)
            return;

        lock (Lock)
            Writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
    }

    static StreamWriter? Open()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            return new StreamWriter(FilePath, append: false) { AutoFlush = true };
        }
        catch (IOException)
        {
            // Logging is optional; never stop the app over it.
            return null;
        }
    }
}
