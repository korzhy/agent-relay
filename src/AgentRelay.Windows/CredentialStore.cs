using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentRelay.Windows;

public interface ICredentialStore
{
    byte[]? Read(string target);
    void Write(string target, byte[] credential);
    void Delete(string target);
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public byte[]? Read(string target)
    {
        EnsureWindows();
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, $"Could not read Windows credential '{target}'.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            if (bytes.Length > 0)
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            }
            return bytes;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string target, byte[] credential)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(credential);
        var blob = Marshal.AllocCoTaskMem(credential.Length);
        try
        {
            if (credential.Length > 0) Marshal.Copy(credential, 0, blob, credential.Length);
            var native = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)credential.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "antigravity"
            };
            if (!CredWrite(ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Could not write Windows credential '{target}'.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string target)
    {
        EnsureWindows();
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error, $"Could not delete Windows credential '{target}'.");
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
