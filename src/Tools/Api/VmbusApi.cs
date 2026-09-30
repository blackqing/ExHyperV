using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ExHyperV.Tools;

// Host-side VMBusPipe and local USBIP TCP endpoint operations.
public static class VmbusApi
{
    static VmbusApi()
    {
        try
        {
            using var _ = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        catch { }
    }

    public const uint SIO_TCP_SET_ACK_FREQUENCY = 0x98000017;
    public const int IPPROTO_TCP = 6;
    public const int TCP_NODELAY = 0x0001;
    public const int SOL_SOCKET = 0xFFFF;
    public const int SO_SNDBUF = 0x1001;
    public const int SO_RCVBUF = 0x1002;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint PipeOfferFlags = 0x11;
    internal const int UserDefinedBytes = 112;
    internal const int PipeWindowBytes = 1024 * 1024;
    internal const int PipeByteWriteBytes = 16 * 1024;

    // Private interface type for ExHyperV USB proxy channels.
    internal static readonly Guid InterfaceType =
        Guid.Parse("45784879-7065-7256-564D-425553504950");

    private static readonly object PipeApiLock = new();
    private static nint PipeApiModule;
    private static ServerOfferChannelExDelegate? OfferChannelEx;
    private static ServerConnectPipeDelegate? ConnectPipe;

    public static ApiResponse SetAckFrequency(nint handle, int frequency)
    {
        uint bytesReturned;
        int ret = VmbusNative.WSAIoctl(handle, SIO_TCP_SET_ACK_FREQUENCY,
            ref frequency, 4, nint.Zero, 0, out bytesReturned, nint.Zero, nint.Zero);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail("WSAIoctl SIO_TCP_SET_ACK_FREQUENCY failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    public static ApiResponse SetNoDelay(nint handle)
    {
        int opt = 1;
        int ret = VmbusNative.setsockopt(handle, IPPROTO_TCP, TCP_NODELAY, ref opt, 4);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail("setsockopt TCP_NODELAY failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    public static ApiResponse SetSendBuffer(nint handle, int size)
    {
        int ret = VmbusNative.setsockopt(handle, SOL_SOCKET, SO_SNDBUF, ref size, 4);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail("setsockopt SO_SNDBUF failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    public static ApiResponse SetReceiveBuffer(nint handle, int size)
    {
        int ret = VmbusNative.setsockopt(handle, SOL_SOCKET, SO_RCVBUF, ref size, 4);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail("setsockopt SO_RCVBUF failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    public static unsafe int Recv(nint socket, void* buffer, int length)
    {
        int received = VmbusNative.recv(socket, buffer, length, 0);
        return received >= 0 ? received : -Marshal.GetLastWin32Error();
    }

    public static unsafe int Send(nint socket, void* buffer, int length)
        => VmbusNative.send(socket, buffer, length, 0);

    internal static nint Offer(Guid vmId, string busId, string pairId, string direction)
    {
        EnsurePipeApiLoaded();
        var offer = new ServerOfferEx
        {
            Version = 1,
            Size = (uint)Marshal.SizeOf<ServerOfferEx>(),
            VmGuid = vmId,
            InterruptLatencyInMilliseconds = 0,
            InterfaceType = InterfaceType,
            InterfaceInstance = Guid.NewGuid(),
            InterfaceRevision = 1,
            MmioMegabytes = 0,
            Flags = (ushort)PipeOfferFlags,
            UserDefined = new byte[UserDefinedBytes]
        };

        byte[] busBytes = Encoding.ASCII.GetBytes(
            $"{busId ?? string.Empty}|{pairId ?? string.Empty}|{direction ?? string.Empty}");
        Array.Copy(busBytes, offer.UserDefined, Math.Min(busBytes.Length, UserDefinedBytes - 1));

        nint name = Marshal.StringToHGlobalUni($"ExHyperV USB VMBusPipe {direction}");
        try
        {
            offer.Name = name;
            uint status = OfferChannelEx!(ref offer, GenericRead | GenericWrite, 0, out nint pipe);
            if (status != 0 || pipe == nint.Zero || pipe == new nint(-1))
            {
                if (pipe != nint.Zero) CloseHandle(pipe);
                throw new Win32Exception(unchecked((int)status),
                    $"VmbusPipeServerOfferChannelEx failed (0x{status:X8})");
            }

            return pipe;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    internal static bool Connect(nint pipeHandle)
    {
        EnsurePipeApiLoaded();
        return ConnectPipe!(pipeHandle, nint.Zero);
    }

    internal static unsafe int Read(nint pipe, void* buffer, int length)
    {
        if (!ReadFile(pipe, buffer, (uint)length, out uint transferred, nint.Zero))
            return -Marshal.GetLastWin32Error();
        return (int)transferred;
    }

    internal static unsafe bool WriteAll(nint pipe, void* buffer, int length)
    {
        byte* cursor = (byte*)buffer;
        int remaining = length;
        while (remaining > 0)
        {
            uint request = (uint)Math.Min(remaining, PipeByteWriteBytes);
            if (!WriteFile(pipe, cursor, request, out uint transferred, nint.Zero) || transferred == 0)
                return false;
            cursor += transferred;
            remaining -= (int)transferred;
        }
        return true;
    }

    internal static void CloseHandle(nint handle)
    {
        if (handle != nint.Zero && handle != new nint(-1))
            _ = VmbusPipeNative.CloseHandle(handle);
    }

    private static void EnsurePipeApiLoaded()
    {
        if (OfferChannelEx != null && ConnectPipe != null) return;
        lock (PipeApiLock)
        {
            if (OfferChannelEx != null && ConnectPipe != null) return;

            string path = Path.Combine(Environment.SystemDirectory, "vmbuspipe.dll");
            nint module = NativeLibrary.Load(path);
            try
            {
                PipeApiModule = module;
                OfferChannelEx = Load<ServerOfferChannelExDelegate>("VmbusPipeServerOfferChannelEx");
                ConnectPipe = Load<ServerConnectPipeDelegate>("VmbusPipeServerConnectPipe");
            }
            catch
            {
                OfferChannelEx = null;
                ConnectPipe = null;
                PipeApiModule = nint.Zero;
                NativeLibrary.Free(module);
                throw;
            }
        }
    }

    private static T Load<T>(string name) where T : Delegate
    {
        nint proc = NativeLibrary.GetExport(PipeApiModule, name);
        return Marshal.GetDelegateForFunctionPointer<T>(proc);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ServerOfferChannelExDelegate(
        ref ServerOfferEx offer, uint openMode, uint pipeMode, out nint pipeHandle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ServerConnectPipeDelegate(nint pipeHandle, nint overlapped);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServerOfferEx
    {
        public uint Version;
        public uint Size;
        public Guid VmGuid;
        public uint InterruptLatencyInMilliseconds;
        public Guid InterfaceType;
        public Guid InterfaceInstance;
        public uint InterfaceRevision;
        public ushort MmioMegabytes;
        public ushort Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = UserDefinedBytes)]
        public byte[] UserDefined;
        public nint Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(
        nint hFile, void* lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(
        nint hFile, void* lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(nint hFile, nint lpOverlapped);

    private static class VmbusPipeNative
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}

internal static class VmbusNative
{
    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern unsafe int recv(nint s, void* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern unsafe int send(nint s, void* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int setsockopt(nint s, int level, int optname,
        ref int optval, int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSAIoctl(nint s, uint dwIoControlCode,
        ref int lpvInBuffer, uint cbInBuffer, nint lpvOutBuffer, uint cbOutBuffer,
        out uint lpcbBytesReturned, nint lpOverlapped, nint lpCompletionRoutine);
}
