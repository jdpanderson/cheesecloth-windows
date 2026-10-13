using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Cheesecloth.Tray;

// The CLI is the daemon's local API client. It checks the pipe server and
// API version before sending a request. Keep those checks in one place.
internal sealed class DaemonClient
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private readonly string executable = Path.Combine(AppContext.BaseDirectory, "cheesecloth.exe");

    public async Task<JsonElement> CallAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        start.ArgumentList.Add("--json");
        foreach (var address in ServiceAddressArguments()) start.ArgumentList.Add(address);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start cheesecloth.exe.");
        var seconds = arguments[0] is "status" or "peers" or "pending" ? 15 : 90;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            _ = await error;
            if (process.ExitCode != 0)
                throw DaemonException.FromCliError(stdout);
            using var document = JsonDocument.Parse(stdout);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new DaemonException("transport_error", $"The daemon did not answer within {seconds} seconds.");
        }
    }

    private static IEnumerable<string> ServiceAddressArguments()
    {
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\cheesecloth");
        var command = service?.GetValue("ImagePath") as string;
        if (string.IsNullOrWhiteSpace(command)) yield break;
        var address = CommandLineToArgvW(command, out var count);
        if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the service command.");
        string[] parts;
        try
        {
            parts = new string[count];
            for (var i = 0; i < count; i++)
                parts[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(address, i * IntPtr.Size)) ?? "";
        }
        finally { LocalFree(address); }
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if ((part is "--state-dir" or "--socket") && i + 1 < parts.Length)
            {
                yield return part;
                yield return parts[++i];
            }
            else if (part.StartsWith("--state-dir=", StringComparison.Ordinal) || part.StartsWith("--socket=", StringComparison.Ordinal))
                yield return part;
        }
    }
}

internal sealed class DaemonException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static DaemonException FromCliError(string stdout)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var error = document.RootElement.Get("error");
            var code = error.Get("code");
            var message = error.Get("message");
            if (code.ValueKind == JsonValueKind.String && message.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(code.GetString()))
                return new DaemonException(code.GetString()!, message.GetString()!);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The cheesecloth command returned an invalid error document.", ex);
        }
        throw new InvalidDataException("The cheesecloth command returned an invalid error document.");
    }
}

internal static class Json
{
    public static JsonElement Get(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;

    public static string Text(this JsonElement value, string name) => value.Get(name).Text();

    public static string Text(this JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "On",
        JsonValueKind.False => "Off",
        _ => "",
    };

    public static int Number(this JsonElement value, string name) =>
        value.Get(name).ValueKind == JsonValueKind.Number ? value.Get(name).GetInt32() : 0;

    public static bool Flag(this JsonElement value, string name) => value.Get(name).ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Items(this JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray()) yield return item;
    }

    public static string ShortId(string id) => id.Length <= 16 ? id : $"{id[..12]}…{id[^4..]}";

    public static string Age(int seconds) => seconds < 90 ? $"{seconds}s ago" :
        seconds < 5400 ? $"{seconds / 60}m ago" :
        seconds < 129600 ? $"{seconds / 3600}h ago" : $"{seconds / 86400}d ago";
}
