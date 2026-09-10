using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using CDSI.Agent.Core.Abstractions;

namespace CDSI.Agent.Infrastructure.Security;

public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 2560;
    private const string TargetPrefix = "CDSI.Agent/";

    public Task StoreAsync(
        string key,
        string secret,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        cancellationToken.ThrowIfCancellationRequested();

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > MaximumCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                "Credential exceeds the Windows Credential Manager size limit.");
        }

        var targetPointer = IntPtr.Zero;
        var usernamePointer = IntPtr.Zero;
        var secretPointer = IntPtr.Zero;
        try
        {
            targetPointer = Marshal.StringToCoTaskMemUni(BuildTarget(key));
            usernamePointer = Marshal.StringToCoTaskMemUni("CDSI Agent");
            secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = usernamePointer
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            ZeroAndFree(secretPointer, secretBytes.Length);
            Marshal.FreeCoTaskMem(usernamePointer);
            Marshal.FreeCoTaskMem(targetPointer);
        }
    }

    public Task<bool> ExistsAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredRead(
                BuildTarget(key),
                CredentialTypeGeneric,
                0,
                out var credentialPointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return Task.FromResult(false);
            }

            throw new Win32Exception(error);
        }

        CredFree(credentialPointer);
        return Task.FromResult(true);
    }

    public Task<string?> RetrieveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredRead(
                BuildTarget(key),
                CredentialTypeGeneric,
                0,
                out var credentialPointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(
                credentialPointer);
            var length = checked((int)credential.CredentialBlobSize);
            if (length == 0)
            {
                return Task.FromResult<string?>(string.Empty);
            }

            var secretBytes = new byte[length];
            try
            {
                Marshal.Copy(
                    credential.CredentialBlob,
                    secretBytes,
                    0,
                    secretBytes.Length);
                var secret = Encoding.Unicode.GetString(secretBytes);
                return Task.FromResult<string?>(secret.TrimEnd('\0'));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredDelete(BuildTarget(key), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }

        return Task.CompletedTask;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Credential Manager is only available on Windows.");
        }
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128 ||
            key.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new ArgumentException("Credential key contains invalid characters.", nameof(key));
        }
    }

    private static string BuildTarget(string key)
    {
        return TargetPrefix + key;
    }

    private static void ZeroAndFree(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }

        Marshal.FreeCoTaskMem(pointer);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredWriteW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        ref NativeCredential userCredential,
        uint flags);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredDeleteW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(
        string target,
        uint type,
        uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credentialPointer);
}
