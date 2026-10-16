using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DCx.Core.Security;

public sealed class WindowsCredentialVault
{
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public void Save(string targetName, string secret)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > 5_120)
        {
            throw new ArgumentException("The credential exceeds the Windows generic credential limit.", nameof(secret));
        }

        var blob = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetName,
                CredentialBlobSize = secretBytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the credential.");
            }
        }
        finally
        {
            if (secretBytes.Length > 0)
            {
                Marshal.Copy(new byte[secretBytes.Length], 0, blob, secretBytes.Length);
                Array.Clear(secretBytes);
            }

            Marshal.FreeCoTaskMem(blob);
        }
    }

    public string? Read(string targetName)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (!CredRead(targetName, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public bool Delete(string targetName)
    {
        EnsureWindows();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        if (CredDelete(targetName, GenericCredential, 0))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new Win32Exception(error, "Windows Credential Manager could not delete the credential.");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
