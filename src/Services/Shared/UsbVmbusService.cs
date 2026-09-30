using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public static class UsbVmbusService
    {
        // This dictionary represents user intent, not an established tunnel.
        public static ConcurrentDictionary<string, string> ActiveTunnels { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, TunnelOperation> Operations =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> OperationGates =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, long> DesiredGenerations =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> OwnedBindings =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> CleanupFailures =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object DesiredStateLock = new();
        private static readonly CancellationTokenSource ShutdownCts = new();
        private static readonly object WatchdogLock = new();
        private static Task? WatchdogTask;
        private static readonly object RecoveryLock = new();
        private static readonly Dictionary<string, Task> RecoveryTasks =
            new(StringComparer.OrdinalIgnoreCase);
        private static long NextGeneration;
        private static int ShutdownStarted;

        // Match the maximum current VMBusPipe byte-mode window so pipe reads
        // can aggregate packets for one full window before crossing the bridge.
        // Pipe writes are still split by VmbusApi.WriteAll at 16 KiB.
        private const int ProxyBufSize = 1024 * 1024;
        private const int TcpBufferSize = 1024 * 1024;
        private const int PipeConnectTimeoutSeconds = 30;
        private const int CommandTimeoutSeconds = 30;
        private const int PumpJoinTimeoutSeconds = 5;
        private const int UsbIpControlTraceBytes = 64;
        private static readonly bool TraceEnabled =
            Environment.GetEnvironmentVariable("EXHV_USB_TRACE") == "1";
        private const int UsbIpAttachedPollMilliseconds = 500;
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
        private static readonly object LogLock = new();
        private static readonly JsonSerializerOptions UsbIpJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private enum TunnelExitReason
        {
            Stopped,
            Faulted
        }

        private readonly record struct UsbIpCommandResult(
            bool Succeeded,
            int ExitCode,
            string StandardOutput,
            string StandardError);

        private sealed class TunnelOperation : IDisposable
        {
            private readonly object ResourceLock = new();
            private nint _pipeHostToGuest;
            private nint _pipeGuestToHost;
            private nint _statusPipe;
            private Socket? _tcp;
            private Thread? _pipeToTcp;
            private Thread? _tcpToPipe;
            private Thread? _statusReader;
            private bool _resourcesClosed;

            public TunnelOperation(string busId, string vmName, Guid vmId, long generation)
            {
                BusId = busId;
                VmName = vmName;
                VmId = vmId;
                Generation = generation;
                Cts = CancellationTokenSource.CreateLinkedTokenSource(ShutdownCts.Token);
                Established = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public string BusId { get; }
            public string VmName { get; }
            public Guid VmId { get; }
            public long Generation { get; }
            public CancellationTokenSource Cts { get; }
            public TaskCompletionSource<bool> Established { get; }
            public Task? RunTask { get; set; }
            public UsbGuestHealth GuestHealth { get; } = new();
            private int _isEstablished;

            public bool IsEstablished => Volatile.Read(ref _isEstablished) != 0 &&
                GuestHealth.IsReady(Environment.TickCount64) && !Cts.IsCancellationRequested;

            public void SetEstablished(bool value) =>
                Volatile.Write(ref _isEstablished, value ? 1 : 0);

            public nint PipeHostToGuest
            {
                get { lock (ResourceLock) return _pipeHostToGuest; }
            }

            public nint PipeGuestToHost
            {
                get { lock (ResourceLock) return _pipeGuestToHost; }
            }

            public Socket? Tcp
            {
                get { lock (ResourceLock) return _tcp; }
            }

            public nint StatusPipe { get { lock (ResourceLock) return _statusPipe; } }
            public void SetStatusPipe(nint pipe)
            {
                lock (ResourceLock)
                {
                    if (_resourcesClosed || Cts.IsCancellationRequested) VmbusApi.CloseHandle(pipe);
                    else _statusPipe = pipe;
                }
            }
            public void SetStatusReader(Thread thread) { lock (ResourceLock) _statusReader = thread; }

            public void SetPipeHandles(nint hostToGuest, nint guestToHost)
            {
                bool discard;
                lock (ResourceLock)
                {
                    discard = _resourcesClosed || Cts.IsCancellationRequested;
                    if (!discard)
                    {
                        _pipeHostToGuest = hostToGuest;
                        _pipeGuestToHost = guestToHost;
                    }
                }

                if (discard)
                {
                    VmbusApi.CloseHandle(hostToGuest);
                    VmbusApi.CloseHandle(guestToHost);
                }
            }

            public void SetTcp(Socket tcp)
            {
                bool discard;
                lock (ResourceLock)
                {
                    discard = _resourcesClosed || Cts.IsCancellationRequested;
                    if (!discard) _tcp = tcp;
                }

                if (discard) tcp.Dispose();
            }

            public void SetPumps(Thread pipeToTcp, Thread tcpToPipe)
            {
                lock (ResourceLock)
                {
                    _pipeToTcp = pipeToTcp;
                    _tcpToPipe = tcpToPipe;
                }
            }

            public void RequestStop()
            {
                try { Cts.Cancel(); } catch { }
                StopIo();
            }

            public void StopIo()
            {
                SetEstablished(false);
                nint pipeHostToGuest;
                nint pipeGuestToHost;
                nint statusPipe;
                Socket? tcp;
                lock (ResourceLock)
                {
                    pipeHostToGuest = _pipeHostToGuest;
                    pipeGuestToHost = _pipeGuestToHost;
                    statusPipe = _statusPipe;
                    tcp = _tcp;
                }

                if (statusPipe != nint.Zero)
                {
                    try { VmbusApi.CancelIoEx(statusPipe, nint.Zero); } catch { }
                }

                if (pipeHostToGuest != nint.Zero)
                {
                    try { VmbusApi.CancelIoEx(pipeHostToGuest, nint.Zero); } catch { }
                }

                if (pipeGuestToHost != nint.Zero)
                {
                    try { VmbusApi.CancelIoEx(pipeGuestToHost, nint.Zero); } catch { }
                }

                if (tcp != null)
                {
                    try { tcp.Shutdown(SocketShutdown.Both); } catch { }
                    try { tcp.Dispose(); } catch { }
                }
            }

            public void JoinPumps()
            {
                Thread? pipeToTcp;
                Thread? tcpToPipe;
                Thread? statusReader;
                lock (ResourceLock)
                {
                    pipeToTcp = _pipeToTcp;
                    tcpToPipe = _tcpToPipe;
                    statusReader = _statusReader;
                }

                JoinPump(pipeToTcp, $"{BusId}:pipe-to-tcp");
                JoinPump(tcpToPipe, $"{BusId}:tcp-to-pipe");
                JoinPump(statusReader, $"{BusId}:guest-status");
            }

            private static void JoinPump(Thread? thread, string label)
            {
                if (thread == null || !thread.IsAlive) return;
                if (!thread.Join(TimeSpan.FromSeconds(PumpJoinTimeoutSeconds)))
                    Log($"Tunnel pump did not stop before timeout: {label}");
            }

            public void CloseResources()
            {
                nint pipeHostToGuest;
                nint pipeGuestToHost;
                nint statusPipe;
                Socket? tcp;
                lock (ResourceLock)
                {
                    if (_resourcesClosed) return;
                    _resourcesClosed = true;
                    pipeHostToGuest = _pipeHostToGuest;
                    _pipeHostToGuest = nint.Zero;
                    pipeGuestToHost = _pipeGuestToHost;
                    _pipeGuestToHost = nint.Zero;
                    statusPipe = _statusPipe;
                    _statusPipe = nint.Zero;
                    tcp = _tcp;
                    _tcp = null;
                }

                VmbusApi.CloseHandle(pipeHostToGuest);
                VmbusApi.CloseHandle(pipeGuestToHost);
                VmbusApi.CloseHandle(statusPipe);
                try { tcp?.Dispose(); } catch { }
            }

            public void Dispose()
            {
                CloseResources();
                Cts.Dispose();
            }
        }

        public static void EnsureWatchdogStarted()
        {
            if (Volatile.Read(ref ShutdownStarted) != 0) return;
            lock (WatchdogLock)
            {
                if (WatchdogTask == null)
                    WatchdogTask = Task.Run(() => WatchdogLoopAsync(ShutdownCts.Token));
            }
        }

        public static void SetDesiredTunnel(string busId, string vmName)
        {
            if (Volatile.Read(ref ShutdownStarted) != 0) return;
            lock (DesiredStateLock)
            {
                if (ShutdownStarted != 0) return;
                ActiveTunnels[busId] = vmName;
                DesiredGenerations[busId] = Interlocked.Increment(ref NextGeneration);
            }
        }

        public static void ClearDesiredTunnel(string busId)
        {
            lock (DesiredStateLock)
            {
                ActiveTunnels.TryRemove(busId, out _);
                DesiredGenerations[busId] = Interlocked.Increment(ref NextGeneration);
            }
        }

        private static bool TryGetDesired(string busId, string vmName, out long generation)
        {
            lock (DesiredStateLock)
            {
                generation = 0;
                if (!ActiveTunnels.TryGetValue(busId, out var current) ||
                    !current.Equals(vmName, StringComparison.OrdinalIgnoreCase))
                    return false;
                return DesiredGenerations.TryGetValue(busId, out generation);
            }
        }

        private static bool IsStillDesired(TunnelOperation operation)
        {
            if (operation.Cts.IsCancellationRequested) return false;
            lock (DesiredStateLock)
            {
                return ActiveTunnels.TryGetValue(operation.BusId, out var vmName) &&
                    vmName.Equals(operation.VmName, StringComparison.OrdinalIgnoreCase) &&
                    DesiredGenerations.TryGetValue(operation.BusId, out var generation) &&
                    generation == operation.Generation;
            }
        }

        private static SemaphoreSlim GateFor(string busId) =>
            OperationGates.GetOrAdd(busId, _ => new SemaphoreSlim(1, 1));

        public static async Task<bool> AutoRecoverTunnel(string busId, string vmName)
        {
            // UI commands also enter this service. Gate owners and their awaited
            // helpers must not capture the dispatcher: OnExit waits for cleanup
            // on that thread, and cleanup needs the same per-device gate.
            if (Volatile.Read(ref ShutdownStarted) != 0) return false;

            var gate = GateFor(busId);
            TunnelOperation operation;
            while (true)
            {
                TunnelOperation? oldOperation = null;
                await gate.WaitAsync(ShutdownCts.Token).ConfigureAwait(false);
                try
                {
                    if (!TryGetDesired(busId, vmName, out var generation)) return false;
                    if (Operations.TryGetValue(busId, out var current))
                    {
                        // UI assignment and watchdog can arrive concurrently.
                        // The same desired generation shares one establishment;
                        // never cancel it or report failure just because it exists.
                        if (current.Generation == generation &&
                            current.VmName.Equals(vmName, StringComparison.OrdinalIgnoreCase) &&
                            !current.Cts.IsCancellationRequested)
                        {
                            operation = current;
                            break;
                        }
                        oldOperation = current;
                        oldOperation.RequestStop();
                    }
                    else
                    {
                        var target = await FindRunningVmAsync(vmName, ShutdownCts.Token).ConfigureAwait(false);
                        ShutdownCts.Token.ThrowIfCancellationRequested();
                        if (target == null)
                        {
                            Log($"Tunnel start skipped: VM is not running: {vmName}");
                            return false;
                        }

                        operation = new TunnelOperation(busId, vmName, target.Id, generation);
                        if (!Operations.TryAdd(busId, operation))
                        {
                            operation.Dispose();
                            continue;
                        }
                        try { operation.RunTask = Task.Run(() => RunAndCleanupAsync(operation)); }
                        catch
                        {
                            Operations.TryRemove(busId, out _);
                            operation.Dispose();
                            throw;
                        }
                        break;
                    }
                }
                finally { gate.Release(); }
                if (oldOperation != null) await WaitForOperationAsync(oldOperation).ConfigureAwait(false);
            }

            try
            {
                return await operation.Established.Task.WaitAsync(
                    TimeSpan.FromSeconds(PipeConnectTimeoutSeconds + 10)).ConfigureAwait(false) &&
                    IsStillDesired(operation) && operation.IsEstablished;
            }
            catch (TimeoutException)
            {
                Log($"Tunnel establish timeout: {busId} -> {vmName}");
                operation.RequestStop();
                return false;
            }
            catch (OperationCanceledException) { return false; }
        }

        private static async Task RunAndCleanupAsync(TunnelOperation operation)
        {
            bool bindingOwnedByOperation = false;
            try
            {
                if (!IsStillDesired(operation)) return;

                // Never unbind an existing owner speculatively.  A stale binding from
                // another process is not ours to revoke.  Only a binding recorded as
                // owned by this process may be removed before a retry.
                if (OwnedBindings.ContainsKey(operation.BusId) &&
                    !await UnbindOwnedBindingAsync(operation.BusId))
                    throw new InvalidOperationException("owned usbipd binding could not be released");

                if (!IsStillDesired(operation)) return;

                var device = await FindUsbDeviceAsync(operation.BusId, operation.Cts.Token);
                if (!IsUsbIpDeviceAssignable(device.State))
                    throw new InvalidOperationException(
                        $"usbipd device is not available for ownership (state={device.State})");

                // Shared means usbipd has already bound the physical device.
                // Reuse that binding; only undo a bind created by this operation.
                if (IsUsbIpNotSharedState(device.State))
                {
                    bool bound = await RunUsbIpCommandAsync(
                        new[] { "bind", "--busid", operation.BusId }, operation.Cts.Token);
                    if (!bound) throw new InvalidOperationException("usbipd bind failed");
                    bindingOwnedByOperation = true;
                    OwnedBindings[operation.BusId] = 0;
                }
                CleanupFailures.TryRemove(operation.BusId, out _);

                if (!IsStillDesired(operation)) return;

                var reason = await RunTunnelAsync(operation);
                if (reason == TunnelExitReason.Faulted)
                    Log($"Tunnel stopped because the data path failed: {operation.BusId}");
            }
            catch (OperationCanceledException)
            {
                Log($"Tunnel operation cancelled: {operation.BusId}");
            }
            catch (Exception ex)
            {
                operation.Established.TrySetResult(false);
                Log($"Tunnel establishment failed for {operation.BusId}: {ex.Message}");
            }
            finally
            {
                operation.SetEstablished(false);
                // Only undo a bind that this process successfully acquired. This avoids
                // taking ownership away from an unrelated usbipd client.
                if (bindingOwnedByOperation || OwnedBindings.ContainsKey(operation.BusId))
                    await UnbindOwnedBindingAsync(operation.BusId);

                if (Operations.TryGetValue(operation.BusId, out var current) &&
                    ReferenceEquals(current, operation))
                    Operations.TryRemove(operation.BusId, out _);

                operation.Established.TrySetResult(false);
                operation.Dispose();
            }
        }

        private static async Task<TunnelExitReason> RunTunnelAsync(TunnelOperation operation)
        {
            var completion = new TaskCompletionSource<TunnelExitReason>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>[]? connectTasks = null;

            using var cancellationRegistration = operation.Cts.Token.Register(() =>
            {
                completion.TrySetResult(TunnelExitReason.Stopped);
                operation.StopIo();
            });

            try
            {
                Log($"Tunnel: offering bidirectional VMBusPipe pair {operation.VmId} for {operation.BusId}");
                var pairId = Guid.NewGuid().ToString("N");
                operation.SetPipeHandles(
                    VmbusApi.Offer(operation.VmId, operation.BusId, pairId, "h2g"), nint.Zero);
                var guestToHost = VmbusApi.Offer(operation.VmId, operation.BusId, pairId, "g2h");
                operation.SetPipeHandles(operation.PipeHostToGuest, guestToHost);
                operation.SetStatusPipe(VmbusApi.Offer(operation.VmId, operation.BusId, pairId, "status"));
                if (operation.Cts.IsCancellationRequested) throw new OperationCanceledException(operation.Cts.Token);

                connectTasks = new[]
                {
                    Task.Run(() => VmbusApi.Connect(operation.PipeHostToGuest)),
                    Task.Run(() => VmbusApi.Connect(operation.PipeGuestToHost)),
                    Task.Run(() => VmbusApi.Connect(operation.StatusPipe))
                };
                bool[] connected = await Task.WhenAll(connectTasks).WaitAsync(
                    TimeSpan.FromSeconds(PipeConnectTimeoutSeconds));
                if (connected.Any(value => !value))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "VMBusPipe connect failed");
                if (operation.Cts.IsCancellationRequested) throw new OperationCanceledException(operation.Cts.Token);

                StartGuestStatusReader(operation, () =>
                {
                    if (completion.TrySetResult(TunnelExitReason.Faulted)) operation.StopIo();
                });

                var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    Blocking = true
                };
                operation.SetTcp(tcp);

                Log($"Tunnel: connecting usbipd TCP endpoint for {operation.BusId}");
                bool tcpConnected = false;
                for (var attempt = 0; attempt < 10 && !operation.Cts.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await tcp.ConnectAsync(IPAddress.Loopback, 3240, operation.Cts.Token);
                        tcpConnected = true;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (SocketException ex)
                    {
                        Log($"usbipd TCP connect attempt {attempt + 1} failed for {operation.BusId}: {ex.SocketErrorCode}");
                        await Task.Delay(500, operation.Cts.Token);
                    }
                }
                if (!tcpConnected) throw new IOException("usbipd TCP endpoint is unavailable");

                var tcpHandle = tcp.SafeHandle.DangerousGetHandle();
                LogApiFailure(VmbusApi.SetAckFrequency(tcpHandle, 1), operation.BusId, "ACK frequency");
                LogApiFailure(VmbusApi.SetNoDelay(tcpHandle), operation.BusId, "TCP_NODELAY");
                LogApiFailure(VmbusApi.SetSendBuffer(tcpHandle, TcpBufferSize), operation.BusId, "send buffer");
                LogApiFailure(VmbusApi.SetReceiveBuffer(tcpHandle, TcpBufferSize), operation.BusId, "receive buffer");

                // The two VMBusPipe offers are directional.  h2g is written by
                // the host and read by the guest; g2h is written by the guest
                // and read by the host.  Keep the TCP bridge on the matching
                // side of each pipe.
                var tcpToGuest = StartNativePump(operation, tcpHandle, false,
                    operation.PipeHostToGuest, true, "TCP_TO_VMBUSPIPE_H2G",
                    () => { if (completion.TrySetResult(TunnelExitReason.Faulted)) operation.StopIo(); });
                var guestToTcp = StartNativePump(operation, operation.PipeGuestToHost, true,
                    tcpHandle, false, "VMBUSPIPE_G2H_TO_TCP",
                    () => { if (completion.TrySetResult(TunnelExitReason.Faulted)) operation.StopIo(); });
                operation.SetPumps(tcpToGuest, guestToTcp);

                // Host Attached only proves export. The matching guest must
                // also confirm its UDE import and started PnP device tree.
                if (!await WaitForUsbIpAttachedAsync(operation.BusId, operation.Cts.Token))
                    throw new InvalidOperationException(
                        "usbipd did not confirm that the device was attached");

                var readyDeadline = Environment.TickCount64 + PipeConnectTimeoutSeconds * 1000L;
                while (!operation.GuestHealth.IsReady(Environment.TickCount64))
                {
                    if (completion.Task.IsCompleted || Environment.TickCount64 >= readyDeadline)
                        throw new IOException("Guest did not confirm a started USB device");
                    await Task.Delay(100, operation.Cts.Token);
                }
                if (completion.Task.IsCompleted || operation.Cts.IsCancellationRequested)
                    throw new IOException("USB bridge stopped during guest readiness confirmation");
                operation.SetEstablished(true);
                operation.Established.TrySetResult(true);
                Log($"Tunnel established (guest-confirmed): {operation.BusId} -> {operation.VmName}");
                while (!completion.Task.IsCompleted)
                {
                    await Task.WhenAny(completion.Task, Task.Delay(1000));
                    if (operation.GuestHealth.IsExpired(Environment.TickCount64))
                    {
                        Log($"Guest heartbeat expired: {operation.BusId}");
                        completion.TrySetResult(TunnelExitReason.Faulted);
                        operation.StopIo();
                    }
                }
                return await completion.Task;
            }
            catch
            {
                operation.Established.TrySetResult(false);
                throw;
            }
            finally
            {
                operation.StopIo();
                if (connectTasks != null)
                {
                    foreach (var connectTask in connectTasks)
                    {
                        if (connectTask.IsCompleted) continue;
                        try { await connectTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch { Log($"VMBusPipe connect worker did not stop promptly: {operation.BusId}"); }
                    }
                }
                operation.JoinPumps();
                operation.CloseResources();
            }
        }

        private static unsafe void StartGuestStatusReader(TunnelOperation operation, Action onFault)
        {
            var thread = new Thread(() =>
            {
                var record = new byte[UsbGuestHealth.RecordBytes];
                try
                {
                    while (!operation.Cts.IsCancellationRequested)
                    {
                        var offset = 0;
                        fixed (byte* buffer = record)
                        {
                            while (offset < record.Length)
                            {
                                int read = VmbusApi.Read(operation.StatusPipe, buffer + offset, record.Length - offset);
                                if (read <= 0) return;
                                offset += read;
                            }
                        }
                        if (!operation.GuestHealth.Update(record, Environment.TickCount64))
                            throw new IOException("Invalid or replayed guest health record");
                    }
                }
                catch (Exception ex) { Log($"Guest status failed for {operation.BusId}: {ex.Message}"); }
                finally { onFault(); }
            }) { IsBackground = true, Name = $"UsbGuestStatus_{operation.BusId}" };
            operation.SetStatusReader(thread);
            thread.Start();
        }

        private static unsafe Thread StartNativePump(TunnelOperation operation,
            nint input, bool inputIsPipe, nint output, bool outputIsPipe,
            string label, Action onFault)
        {
            var thread = new Thread(() =>
            {
                void* buffer = NativeMemory.AlignedAlloc(ProxyBufSize, 4096);
                if (buffer == null)
                {
                    onFault();
                    return;
                }

                var tracedBytes = 0;
                try
                {
                    while (!operation.Cts.IsCancellationRequested)
                    {
                        int received = inputIsPipe
                            ? VmbusApi.Read(input, buffer, ProxyBufSize)
                            : VmbusApi.Recv(input, buffer, ProxyBufSize);
                        if (received <= 0)
                        {
                            if (received < 0)
                                Log($"{label} read failed for {operation.BusId}: win32={-received}");
                            break;
                        }

                        TraceControlBytes(label, operation.BusId, buffer, received, ref tracedBytes);
                        bool sent = outputIsPipe
                            ? VmbusApi.WriteAll(output, buffer, received)
                            : SendSocketAll(output, buffer, received);
                        if (!sent) break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"{label} failed for {operation.BusId}: {ex.Message}");
                }
                finally
                {
                    NativeMemory.AlignedFree(buffer);
                    onFault();
                }
            })
            { IsBackground = true, Name = $"UsbTunnel_{label}_{operation.BusId}" };
            thread.Start();
            return thread;
        }

        private static unsafe void TraceControlBytes(string label, string busId,
            void* buffer, int length, ref int tracedBytes)
        {
            if (!TraceEnabled || tracedBytes >= UsbIpControlTraceBytes) return;

            int count = Math.Min(length, UsbIpControlTraceBytes - tracedBytes);
            if (count <= 0) return;

            var bytes = new byte[count];
            Marshal.Copy((nint)buffer, bytes, 0, count);
            string hex = Convert.ToHexString(bytes);
            int offset = tracedBytes;
            tracedBytes += count;
            Log($"TRACE {label} bus={busId} offset={offset} bytes={count} hex={hex}");
        }

        private static unsafe bool SendSocketAll(nint socket, void* buffer, int length)
        {
            var cursor = (byte*)buffer;
            var remaining = length;
            while (remaining > 0)
            {
                int sent = VmbusApi.Send(socket, cursor, remaining);
                if (sent <= 0) return false;
                cursor += sent;
                remaining -= sent;
            }
            return true;
        }

        public static async Task<bool> StopTunnelAsync(string busId)
        {
            ClearDesiredTunnel(busId);

            if (Operations.TryGetValue(busId, out var operation))
                operation.RequestStop();

            if (operation != null)
                await WaitForOperationAsync(operation).ConfigureAwait(false);

            return await UnbindOwnedBindingAsync(busId).ConfigureAwait(false);
        }

        public static bool IsTunnelEstablished(string busId, string vmName) =>
            Operations.TryGetValue(busId, out var operation) &&
            operation.VmName.Equals(vmName, StringComparison.OrdinalIgnoreCase) &&
            operation.IsEstablished;

        public static bool IsGuestConnectionUnhealthy(string busId) =>
            Operations.TryGetValue(busId, out var operation) &&
            operation.GuestHealth.HasError(Environment.TickCount64);

        public static async Task StopAllTunnelsAsync()
        {
            if (Interlocked.Exchange(ref ShutdownStarted, 1) != 0) return;
            try { ShutdownCts.Cancel(); } catch { }

            var busIds = new HashSet<string>(ActiveTunnels.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var busId in Operations.Keys) busIds.Add(busId);
            foreach (var busId in OwnedBindings.Keys) busIds.Add(busId);

            lock (DesiredStateLock)
            {
                foreach (var busId in ActiveTunnels.Keys)
                    DesiredGenerations[busId] = Interlocked.Increment(ref NextGeneration);
                ActiveTunnels.Clear();
            }

            if (WatchdogTask != null)
            {
                try { await WatchdogTask.ConfigureAwait(false); }
                catch (Exception ex) { Log($"Watchdog shutdown failed: {ex.Message}"); }
            }

            // Recovery tasks are intentionally tracked: an unobserved watchdog
            // task must not be able to create a new operation after shutdown.
            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var operation in Operations.Values) operation.RequestStop();
                var waits = Operations.Values
                    .Where(operation => operation.RunTask != null)
                    .Select(operation => WaitForOperationAsync(operation))
                    .Concat(SnapshotRecoveryTasks());
                try { await Task.WhenAll(waits).ConfigureAwait(false); } catch { }
                if (Operations.IsEmpty && SnapshotRecoveryTasks().Length == 0) break;
            }

            foreach (var busId in busIds)
                await UnbindOwnedBindingAsync(busId).ConfigureAwait(false);
        }

        public static bool IsTunnelCleanupFailed(string busId) =>
            CleanupFailures.ContainsKey(busId);

        private static async Task<bool> UnbindOwnedBindingAsync(string busId)
        {
            var gate = GateFor(busId);
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!OwnedBindings.ContainsKey(busId))
                {
                    CleanupFailures.TryRemove(busId, out _);
                    return true;
                }

                var result = await RunUsbIpCommandWithOutputAsync(
                    new[] { "unbind", "--busid", busId }, CancellationToken.None).ConfigureAwait(false);
                // usbipd can report that the busid does not exist when the device
                // has already been detached during tunnel teardown.  That is the
                // desired end state, so make this cleanup operation idempotent.
                bool unbound = result.Succeeded || IsAlreadyUnbound(result);
                if (unbound)
                {
                    OwnedBindings.TryRemove(busId, out _);
                    CleanupFailures.TryRemove(busId, out _);
                    if (!result.Succeeded)
                        Log($"Owned usbipd binding was already absent: {busId}");
                }
                else
                {
                    CleanupFailures[busId] = 0;
                    Log($"Owned usbipd binding could not be removed: {busId}");
                }
                return unbound;
            }
            finally { gate.Release(); }
        }

        private static async Task WaitForOperationAsync(TunnelOperation operation)
        {
            if (operation.RunTask == null) return;
            try { await operation.RunTask.WaitAsync(TimeSpan.FromSeconds(CommandTimeoutSeconds + 5)).ConfigureAwait(false); }
            catch (TimeoutException) { Log($"Tunnel operation did not stop before timeout: {operation.BusId}"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Tunnel operation ended with cleanup error: {ex.Message}"); }
        }

        public static async Task WatchdogLoopAsync(CancellationToken globalCt)
        {
            while (!globalCt.IsCancellationRequested)
            {
                foreach (var entry in ActiveTunnels.ToArray())
                {
                    if (!Operations.ContainsKey(entry.Key))
                        StartRecoveryTask(entry.Key, entry.Value);
                }

                try { await Task.Delay(RetryInterval, globalCt); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static async Task RecoverWithLoggingAsync(string busId, string vmName)
        {
            try
            {
                await AutoRecoverTunnel(busId, vmName);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Watchdog recovery failed for {busId} -> {vmName}: {ex.Message}");
            }
        }

        private static void StartRecoveryTask(string busId, string vmName)
        {
            Task recoveryTask;
            lock (RecoveryLock)
            {
                if (ShutdownStarted != 0 || RecoveryTasks.ContainsKey(busId)) return;
                recoveryTask = Task.Run(() => RecoverWithLoggingAsync(busId, vmName));
                RecoveryTasks[busId] = recoveryTask;
            }

            _ = recoveryTask.ContinueWith(completed =>
            {
                lock (RecoveryLock)
                {
                    if (RecoveryTasks.TryGetValue(busId, out var current) &&
                        ReferenceEquals(current, completed))
                        RecoveryTasks.Remove(busId);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static Task[] SnapshotRecoveryTasks()
        {
            lock (RecoveryLock) return RecoveryTasks.Values.ToArray();
        }

        public static async Task<List<UsbTargetVm>> GetRunningVMsAsync()
        {
            var response = await WmiApi.QueryAsync(
                "SELECT Name, ElementName FROM Msvm_ComputerSystem WHERE EnabledState = 2 AND Name <> ElementName",
                obj => new UsbTargetVm
                {
                    Name = obj["ElementName"]?.ToString() ?? string.Empty,
                    Id = Guid.TryParse(obj["Name"]?.ToString(), out var id) ? id : Guid.Empty
                }, WmiScope.HyperV).ConfigureAwait(false);

            if (!response.Success)
                throw new InvalidOperationException($"Hyper-V VM query failed: {response.Error}");

            return (response.Data ?? new List<UsbTargetVm>())
                .Where(vm => !string.IsNullOrWhiteSpace(vm.Name) && vm.Id != Guid.Empty)
                .ToList();
        }

        public static bool IsUsbIpDeviceAssignable(string? state) =>
            IsUsbIpNotSharedState(state) || IsUsbIpSharedState(state);

        public static bool IsUsbIpDeviceAttached(string? state) =>
            string.Equals(state, "Attached", StringComparison.OrdinalIgnoreCase);

        private static bool IsUsbIpNotSharedState(string? state) =>
            string.Equals(state, "Not shared", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Allowed", StringComparison.OrdinalIgnoreCase);

        private static bool IsUsbIpSharedState(string? state) =>
            string.Equals(state, "Shared", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "Shared (forced)", StringComparison.OrdinalIgnoreCase);

        private static async Task<bool> WaitForUsbIpAttachedAsync(
            string busId, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(PipeConnectTimeoutSeconds);
            string lastState = "not observed";

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var stdout = await RunUsbIpReadCommandAsync(
                        new[] { "list" }, Encoding.UTF8, ct);
                    var device = ParseUsbIpList(stdout)
                        .FirstOrDefault(item => item.BusId.Equals(
                            busId, StringComparison.OrdinalIgnoreCase));
                    lastState = device?.State ?? "not present";
                    if (device != null && IsUsbIpDeviceAttached(device.State))
                        return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastState = $"query failed: {ex.Message}";
                }

                await Task.Delay(UsbIpAttachedPollMilliseconds, ct);
            }

            Log($"usbipd did not report Attached for {busId}; last state: {lastState}");
            return false;
        }

        private static async Task<UsbTargetVm?> FindRunningVmAsync(string vmName, CancellationToken operationToken)
        {
            var vms = await GetRunningVMsAsync().WaitAsync(operationToken).ConfigureAwait(false);
            return vms.FirstOrDefault(vm =>
                vm.Name.Equals(vmName, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class UsbIpStateResponse
        {
            public List<UsbIpStateDevice> Devices { get; set; } = new();
        }

        private sealed class UsbIpStateDevice
        {
            public string? BusId { get; set; }
            public string? Description { get; set; }
            public string? InstanceId { get; set; }
            public string? StubInstanceId { get; set; }
        }

        private static async Task<string> RunUsbIpReadCommandAsync(
            IReadOnlyList<string> arguments,
            Encoding outputEncoding,
            CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(CommandTimeoutSeconds));

            var psi = new ProcessStartInfo(ResolveUsbIpdExecutable())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException(
                    $"Unable to start usbipd {string.Join(' ', arguments)}");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"usbipd {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr.Trim()}");
            return stdout;
        }

        public static Task<List<UsbDevice>> GetUsbIpDevicesAsync(CancellationToken ct = default) => Task.Run(async () =>
        {
            var stdout = await RunUsbIpReadCommandAsync(
                new[] { "list" }, Encoding.UTF8, ct);

            var list = ParseUsbIpList(stdout);

            // usbipd list is a human-readable table and does not expose the
            // original PnP InstanceId. State JSON does, and its BusId lets us
            // associate the exact physical device even when VID/PID is shared.
            var stateByBusId = new Dictionary<string, UsbIpStateDevice>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var stateJson = await RunUsbIpReadCommandAsync(
                    new[] { "state" }, Encoding.UTF8, ct);
                var state = JsonSerializer.Deserialize<UsbIpStateResponse>(
                    stateJson, UsbIpJsonOptions);
                foreach (var stateDevice in state?.Devices ?? new List<UsbIpStateDevice>())
                {
                    if (!string.IsNullOrWhiteSpace(stateDevice.BusId))
                        stateByBusId[stateDevice.BusId] = stateDevice;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep list-based discovery working with older usbipd versions;
                // enrichment below is deliberately conservative when state JSON
                // is unavailable.
                Debug.WriteLine($"[ExHyperV-USB] usbipd state enrichment unavailable: {ex.Message}");
            }

            for (var index = 0; index < list.Count; index++)
            {
                var device = list[index];
                if (!stateByBusId.TryGetValue(device.BusId, out var stateDevice) ||
                    string.IsNullOrWhiteSpace(stateDevice.InstanceId))
                    continue;

                list[index] = new UsbDevice
                {
                    BusId = device.BusId,
                    VidPid = device.VidPid,
                    Name = device.Name,
                    InstanceId = stateDevice.InstanceId.Trim(),
                    Description = string.IsNullOrWhiteSpace(stateDevice.Description)
                        ? device.Description
                        : stateDevice.Description.Trim(),
                    State = device.State
                };
            }

            // Keep the Windows name and bus-reported description separate, both
            // from the exact original PnP root identified by usbipd state.
            Dictionary<string, UsbPnpMetadata> usbMetadata;
            try
            {
                var allPnpDevices = Win32Api.GetAllDevices();
                var pnpByInstanceId = allPnpDevices
                    .Where(device => !string.IsNullOrWhiteSpace(device.InstanceId))
                    .ToDictionary(device => device.InstanceId,
                        StringComparer.OrdinalIgnoreCase);
                var presentCandidatesByVidPid = allPnpDevices
                    .Where(device => device.IsPresent)
                    .Select(device => new
                    {
                        Device = device,
                        VidPid = GetUsbVidPidKey(device.InstanceId)
                    })
                    .Where(item => item.VidPid != null)
                    .GroupBy(item => item.VidPid!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key,
                        group => group.Select(item => item.Device).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                var pnpChildrenByParent = allPnpDevices
                    .Where(device => !string.IsNullOrWhiteSpace(device.ParentInstanceId))
                    .GroupBy(device => device.ParentInstanceId,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key,
                        group => group.ToList(),
                        StringComparer.OrdinalIgnoreCase);
                var connectedCountsByVidPid = list
                    .GroupBy(device => device.VidPid,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(),
                        StringComparer.OrdinalIgnoreCase);

                usbMetadata = new Dictionary<string, UsbPnpMetadata>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var device in list)
                {
                    stateByBusId.TryGetValue(device.BusId, out var stateDevice);
                    var roots = FindExactPnpRoots(
                        stateDevice?.InstanceId,
                        stateDevice?.StubInstanceId,
                        pnpByInstanceId);
                    var usedFallbackRoot = false;

                    // Older usbipd versions may not support `state`. Do not
                    // reintroduce the old VID/PID cross-device bug in fallback:
                    // enrich only when both usbipd and the present PnP tree give
                    // us one unambiguous candidate.
                    if (roots.Count == 0 &&
                        connectedCountsByVidPid.TryGetValue(device.VidPid, out var count) &&
                        count == 1 &&
                        presentCandidatesByVidPid.TryGetValue(device.VidPid, out var candidates))
                    {
                        var fallbackRoot = FindUnambiguousFallbackRoot(candidates);
                        if (fallbackRoot != null)
                        {
                            roots.Add(fallbackRoot);
                            usedFallbackRoot = true;
                        }
                    }

                    if (roots.Count == 0) continue;

                    usbMetadata[device.BusId] = BuildUsbPnpMetadata(
                        roots, stateDevice?.InstanceId, pnpChildrenByParent,
                        usedFallbackRoot);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] PnP USB name enrichment failed: {ex.Message}");
                usbMetadata = new Dictionary<string, UsbPnpMetadata>(StringComparer.OrdinalIgnoreCase);
            }

            for (var index = 0; index < list.Count; index++)
            {
                var device = list[index];
                try
                {
                    stateByBusId.TryGetValue(device.BusId, out var stateDevice);
                    usbMetadata.TryGetValue(device.BusId, out var metadata);

                    list[index] = new UsbDevice
                    {
                        BusId = device.BusId,
                        VidPid = device.VidPid,
                        Name = GetUsbSystemName(metadata, device),
                        BusReportedDescription = metadata?.BusReportedDescription ?? string.Empty,
                        InstanceId = stateDevice?.InstanceId?.Trim() ??
                            metadata?.RootDevice.InstanceId ?? device.InstanceId,
                        Description = device.Description,
                        State = device.State,
                        DeviceType = metadata?.DeviceType ?? UsbDeviceType.Unknown
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExHyperV-USB] USB name enrichment skipped for {device.BusId}: {ex.Message}");
                }
            }

            return list;
        }, ct);

        private static List<UsbDevice> ParseUsbIpList(string stdout)
        {
            var list = new List<UsbDevice>();
            foreach (var rawLine in stdout.Split(new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                var match = Regex.Match(line,
                    @"^(?<bus>[0-9]+(?:-[0-9]+)+(?:\.[0-9]+)?)\s+(?<vid>[0-9a-fA-F]{4}:[0-9a-fA-F]{4})\s+(?<rest>.+)$");
                if (!match.Success) continue;

                var rest = match.Groups["rest"].Value.Trim();
                var stateMatch = Regex.Match(rest,
                    @"^(?<description>.*?)(?:\s{2,})(?<state>Not shared|Allowed|Shared(?: \(forced\))?|Attached|Busy|Unavailable|Incompatible hub|No userspace driver)\s*$",
                    RegexOptions.IgnoreCase);
                var description = stateMatch.Success
                    ? stateMatch.Groups["description"].Value.Trim()
                    : rest;
                var state = stateMatch.Success
                    ? stateMatch.Groups["state"].Value.Trim()
                    : "Unknown";
                list.Add(new UsbDevice
                {
                    BusId = match.Groups["bus"].Value,
                    VidPid = match.Groups["vid"].Value.ToUpperInvariant(),
                    Name = string.Empty,
                    Description = description,
                    State = state
                });
            }

            return list;
        }

        private sealed record UsbPnpMetadata(
            PciDeviceInfo RootDevice,
            string? SystemName,
            string? BusReportedDescription,
            UsbDeviceType DeviceType);

        private static List<PciDeviceInfo> FindExactPnpRoots(
            string? instanceId,
            string? stubInstanceId,
            IReadOnlyDictionary<string, PciDeviceInfo> pnpByInstanceId)
        {
            var roots = new List<PciDeviceInfo>();
            AddExactRoot(instanceId);
            AddExactRoot(stubInstanceId);

            // A device attached through usbipd can have a phantom original
            // node and a present stub node. Prefer the present node when a
            // caller needs a single root, while retaining the exact original
            // node for product metadata.
            return roots
                .OrderByDescending(root => root.IsPresent)
                .ThenBy(root => string.Equals(root.InstanceId, instanceId?.Trim(),
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            void AddExactRoot(string? candidateId)
            {
                var normalizedId = candidateId?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedId) ||
                    !pnpByInstanceId.TryGetValue(normalizedId, out var root) ||
                    roots.Any(existing => existing.InstanceId.Equals(root.InstanceId,
                        StringComparison.OrdinalIgnoreCase)))
                    return;
                roots.Add(root);
            }
        }

        private static string? GetUsbVidPidKey(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return null;
            var match = Regex.Match(instanceId,
                @"^(?:USB|HID)\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})(?:&|\\|$)",
                RegexOptions.IgnoreCase);
            return match.Success
                ? match.Groups["vid"].Value.ToUpperInvariant() + ":" +
                  match.Groups["pid"].Value.ToUpperInvariant()
                : null;
        }

        private static PciDeviceInfo? FindUnambiguousFallbackRoot(
            IReadOnlyList<PciDeviceInfo> candidates)
        {
            var usbParents = candidates
                .Where(device => IsUsbParentNode(device))
                .ToList();
            if (usbParents.Count == 1) return usbParents[0];
            if (usbParents.Count > 1) return null;
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static bool IsUsbParentNode(PciDeviceInfo device) =>
            device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) &&
            !device.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

        private static UsbPnpMetadata BuildUsbPnpMetadata(
            IReadOnlyList<PciDeviceInfo> roots,
            string? originalInstanceId,
            IReadOnlyDictionary<string, List<PciDeviceInfo>> childrenByParent,
            bool allowFallbackRoot)
        {
            var root = roots[0];

            // The original root is the only node whose description represents
            // the USB device itself. A stub node and its function children can
            // describe the transport or one interface rather than the product.
            // Keep the original InstanceId association exact. A different node is
            // used for product metadata only when the caller proved it unique.
            var originalRoot = !string.IsNullOrWhiteSpace(originalInstanceId)
                ? roots.FirstOrDefault(candidate => candidate.InstanceId.Equals(
                    originalInstanceId.Trim(), StringComparison.OrdinalIgnoreCase))
                : root;
            // If the exact original node is temporarily missing, roots has already
            // been restricted to an unambiguous USB parent by the caller. It is safe
            // to use that node as a lower-confidence metadata source.
            var metadataRoot = originalRoot ??
                (allowFallbackRoot ? roots.FirstOrDefault(IsUsbParentNode) : null);
            var systemName = string.IsNullOrWhiteSpace(metadataRoot?.SystemName)
                ? metadataRoot?.FriendlyName?.Trim()
                : metadataRoot.SystemName.Trim();
            var deviceType = ClassifyUsbDevice(metadataRoot ?? root, childrenByParent);

            return new UsbPnpMetadata(root, systemName,
                metadataRoot?.BusReportedDescription?.Trim(), deviceType);
        }

        private static string GetUsbSystemName(UsbPnpMetadata? metadata, UsbDevice device)
        {
            // Generic names are still valid Windows names. Do not discard them
            // or replace them with a child function's name or the USB description.
            return !string.IsNullOrWhiteSpace(metadata?.SystemName)
                ? metadata.SystemName
                : device.Description?.Trim() ?? string.Empty;
        }

        private static UsbDeviceType ClassifyUsbDevice(
            PciDeviceInfo root,
            IReadOnlyDictionary<string, List<PciDeviceInfo>> childrenByParent)
        {
            // 名称只用于显示，不据品牌/型号或名称中的关键词猜产品形态。
            // 原始节点被 usbipd 替换后，不用离线功能树推断当前功能。
            if (!root.IsPresent) return UsbDeviceType.Unknown;
            var type = UsbDeviceType.Unknown;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var interfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<PciDeviceInfo>();

            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var device = pending.Dequeue();
                if (!device.IsPresent || !visited.Add(device.InstanceId)) continue;
                type |= InferUsbDeviceType(device);

                var usbInterface = Regex.Match(device.InstanceId,
                    @"^USB\\[^\\]+&MI_(?<id>[0-9A-F]{2})(?:&|\\|$)", RegexOptions.IgnoreCase);
                if (usbInterface.Success) interfaces.Add(usbInterface.Groups["id"].Value);

                // 蓝牙适配器下的无线设备、USB Hub 下的独立设备不是它自身的功能。
                if (device.Class.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                    device.Service.Equals("BTHUSB", StringComparison.OrdinalIgnoreCase) ||
                    device.Service.Equals("USBHUB", StringComparison.OrdinalIgnoreCase) ||
                    device.Service.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!childrenByParent.TryGetValue(device.InstanceId, out var children))
                    continue;

                foreach (var child in children)
                {
                    if (!string.IsNullOrWhiteSpace(child.InstanceId) && !IsUsbParentNode(child))
                        pending.Enqueue(child);
                }
            }

            if (interfaces.Count > 1) type |= UsbDeviceType.Composite;
            return type;
        }

        private static UsbDeviceType InferUsbDeviceType(PciDeviceInfo device)
        {
            var type = device.Class.ToUpperInvariant() switch
            {
                "KEYBOARD" => UsbDeviceType.Keyboard,
                "MOUSE" => UsbDeviceType.Mouse,
                "CAMERA" or "IMAGE" => UsbDeviceType.Camera,
                "MEDIA" or "AUDIOENDPOINT" => UsbDeviceType.Audio,
                "DISKDRIVE" or "CDROM" or "SCSIADAPTER" or "HDC" => UsbDeviceType.Storage,
                "NET" => UsbDeviceType.Network,
                "PRINTER" => UsbDeviceType.Printer,
                "PORTS" => UsbDeviceType.Serial,
                "SMARTCARDREADER" => UsbDeviceType.SmartCard,
                "BLUETOOTH" => UsbDeviceType.Bluetooth,
                "HIDCLASS" => UsbDeviceType.HumanInterface,
                _ => UsbDeviceType.Unknown
            };
            type |= device.Service.ToUpperInvariant() switch
            {
                "USBCCGP" => UsbDeviceType.Composite,
                "KBDHID" => UsbDeviceType.Keyboard,
                "MOUHID" => UsbDeviceType.Mouse,
                "USBVIDEO" => UsbDeviceType.Camera,
                "USBAUDIO" or "USBAUDIO2" => UsbDeviceType.Audio,
                "USBSTOR" or "UASPSTOR" => UsbDeviceType.Storage,
                "RNDISMP" or "USB8023" => UsbDeviceType.Network,
                "USBPRINT" => UsbDeviceType.Printer,
                "USBSER" => UsbDeviceType.Serial,
                "BTHUSB" => UsbDeviceType.Bluetooth,
                "HIDUSB" => UsbDeviceType.HumanInterface,
                _ => UsbDeviceType.Unknown
            };
            foreach (var id in device.CompatibleIds)
            {
                if (id.Equals(@"USB\COMPOSITE", StringComparison.OrdinalIgnoreCase))
                    type |= UsbDeviceType.Composite;
                var usbClass = Regex.Match(id, @"^USB\\Class_(?<id>[0-9A-F]{2})(?:&|$)",
                    RegexOptions.IgnoreCase);
                type |= usbClass.Groups["id"].Value.ToUpperInvariant() switch
                {
                    "01" => UsbDeviceType.Audio,
                    "03" => UsbDeviceType.HumanInterface,
                    "06" or "0E" => UsbDeviceType.Camera,
                    "07" => UsbDeviceType.Printer,
                    "08" => UsbDeviceType.Storage,
                    "0B" => UsbDeviceType.SmartCard,
                    _ => UsbDeviceType.Unknown
                };
                if (id.Equals(@"HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase))
                    type |= UsbDeviceType.GameController;
            }
            return type;
        }

        private static async Task<UsbDevice> FindUsbDeviceAsync(
            string busId, CancellationToken ct)
        {
            var device = (await GetUsbIpDevicesAsync(ct))
                .FirstOrDefault(item => item.BusId.Equals(busId,
                    StringComparison.OrdinalIgnoreCase));
            return device ?? throw new InvalidOperationException(
                $"USB device is no longer present: {busId}");
        }

        private static bool IsAlreadyUnbound(UsbIpCommandResult result)
        {
            if (result.Succeeded) return false;

            var error = result.StandardError.Trim();
            return error.Contains("no device with busid", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("device is not shared", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<bool> RunUsbIpCommandAsync(
            IReadOnlyList<string> arguments, CancellationToken ct) =>
            (await RunUsbIpCommandWithOutputAsync(arguments, ct).ConfigureAwait(false)).Succeeded;

        private static async Task<UsbIpCommandResult> RunUsbIpCommandWithOutputAsync(
            IReadOnlyList<string> arguments, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(CommandTimeoutSeconds));

            try
            {
                var psi = new ProcessStartInfo(ResolveUsbIpdExecutable())
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.Default,
                    StandardErrorEncoding = System.Text.Encoding.Default
                };
                foreach (var argument in arguments) psi.ArgumentList.Add(argument);

                using var process = Process.Start(psi);
                if (process == null) return new(false, -1, string.Empty, string.Empty);
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    try { process.WaitForExit(5000); } catch { }
                    Log($"usbipd command timed out or was cancelled: {string.Join(' ', arguments)}");
                    return new(false, -1, string.Empty, string.Empty);
                }
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                var ok = process.ExitCode == 0;
                Log($"usbipd {string.Join(' ', arguments)} exit={process.ExitCode} ok={ok} stdout={stdout.Trim()} stderr={stderr.Trim()}");
                return new(ok, process.ExitCode, stdout, stderr);
            }
            catch (Exception ex)
            {
                Log($"usbipd command failed: {string.Join(' ', arguments)} error={ex.Message}");
                return new(false, -1, string.Empty, ex.Message);
            }
        }

        private static string ResolveUsbIpdExecutable()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "usbipd-win", "usbipd.exe")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "usbipd.exe";
        }

        public static bool IsUsbipdInstalled()
        {
            try
            {
                using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\usbipd");
                if (service != null) return true;
            }
            catch { }

            return File.Exists(ResolveUsbIpdExecutable());
        }

        private static void LogApiFailure(ApiResponse response, string busId, string operation)
        {
            if (!response.Success)
                Log($"TCP tuning failed ({operation}) for {busId}: {response.Error} ({response.Code})");
        }

        private static void Log(string message)
        {
            string line = $"[ExHyperV-USB] [{DateTime.Now:HH:mm:ss.fff}] {message}";
            Debug.WriteLine(line);
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrWhiteSpace(desktop))
                {
                    lock (LogLock)
                        File.AppendAllText(Path.Combine(desktop, "ExHyperV-USB-bridge.log"),
                            line + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
