using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Player.App.Platform;

// One player per user, elevation level and interactive session. No media bytes
// cross this channel: bounded UTF-8 paths are dispatched to the existing UI.
internal sealed class SingleInstance : IDisposable
{
    private const int MaximumBytes = 131072;
    private readonly Mutex _lifetime;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listener;
    internal bool IsPrimary { get; }

    internal SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.User!.Value)))[..24];
        _pipeName = $"mpv.win.Player.{user}.{Process.GetCurrentProcess().SessionId}.{elevated}";
        _lifetime = new Mutex(false, @"Local\" + _pipeName, out bool created);
        IsPrimary = created;
    }

    internal void Listen(Func<string?, bool> dispatch)
    {
        if (!IsPrimary || _listener is not null) throw new InvalidOperationException("Activation listener already owned.");
        CancellationToken stop = _stop.Token;
        _listener = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop);
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    request.CancelAfter(TimeSpan.FromSeconds(5));
                    byte[] header = new byte[4];
                    await pipe.ReadExactlyAsync(header, request.Token);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < 0 || length > MaximumBytes) continue;
                    byte[] payload = new byte[length];
                    await pipe.ReadExactlyAsync(payload, request.Token);
                    string? path = length == 0 ? null : new UTF8Encoding(false, true).GetString(payload);
                    if (path is not null && (!Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0)) continue;
                    bool accepted = dispatch(path);
                    await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, request.Token);
                    await pipe.FlushAsync(request.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    App.WriteStartupFailure(ex, "Activation request failed");
                    // Avoid a tight retry loop if the pipe namespace is unavailable.
                    try { await Task.Delay(250, stop); } catch (OperationCanceledException) { }
                }
            }
        });
    }

    internal async Task ForwardAsync(string? path)
    {
        byte[] payload = path is null ? [] : Encoding.UTF8.GetBytes(path);
        if (payload.Length > MaximumBytes) throw new ArgumentException("The file path is too long.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        // The foreground launch process may grant its foreground permission to
        // the existing player. Windows still decides whether focus is allowed.
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server)) AllowSetForegroundWindow(server);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.WriteAsync(payload, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        byte[] response = new byte[1];
        await pipe.ReadExactlyAsync(response, timeout.Token);
        if (response[0] != 1) throw new IOException("mpv.win is closing. Please open the file again after it exits.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lifetime.Dispose();
        // Dispose the source only after its asynchronous pipe operations finish.
        if (_listener is { } listener) _ = listener.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        else _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
