using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PocketPDF;

/// <summary>
/// Explorer starts one PocketPDF per selected file, for "Combine PDFs" and for opening. The first one to start on a
/// channel becomes the collector: it listens on a named pipe, and every later one sends it its files and exits. The
/// collector keeps listening while its window is open, so files from a slow Explorer still arrive.
/// </summary>
public sealed class FileCollector : IDisposable
{
    readonly string _name;
    readonly Mutex _mutex;
    readonly CancellationTokenSource _stop = new();

    FileCollector(string name, Mutex mutex) => (_name, _mutex) = (name, mutex);

    /// <summary>
    /// Becomes the collector of the channel, or hands the files to the one already running. Returns null if the files
    /// were handed over, so this process can exit. With no files there is nothing to hand over: this process carries
    /// on by itself, and takes over listening once the collector closes.
    /// </summary>
    public static FileCollector? StartOrHandOver(string channel, IReadOnlyList<string> filePaths)
    {
        // One collector per user and channel: the user's SID keeps other sessions on the same machine apart.
        string name = $"PocketPDF.{channel}.{WindowsIdentity.GetCurrent().User?.Value}";
        var mutex = new Mutex(initiallyOwned: true, name, out bool isFirst);
        if (isFirst) return new FileCollector(name, mutex);
        mutex.Dispose();
        if (filePaths.Count > 0 && filePaths.All(filePath => TrySend(name, filePath))) return null;
        // The collector didn't answer (it may be closing): this process collects instead, starting with its own files.
        return new FileCollector(name, new Mutex(initiallyOwned: false, name));
    }

    /// <summary>Listens for files from other processes until disposed. <paramref name="onFile"/> runs on a background thread.</summary>
    public async Task ListenAsync(Action<string> onFile)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_stop.Token) is { } line) onFile(line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A sender gave up halfway, or another window still has the pipe: try again in a moment, without spinning.
                await Task.Delay(500);
            }
        }
    }

    static bool TrySend(string name, string filePath)
    {
        // Explorer gave this process the right to come to the front; pass it on, so the collector's window can.
        AllowSetForegroundWindow(AnyProcess);
        // The collector handles one sender at a time, so others may find the pipe busy for a moment: retry for a few seconds.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                client.Connect(200);
                using var writer = new StreamWriter(client);
                writer.WriteLine(filePath);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    const int AnyProcess = -1;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (the fallback collector): nothing to release.
        }
        _mutex.Dispose();
    }
}
