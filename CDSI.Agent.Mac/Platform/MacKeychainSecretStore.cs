using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CDSI.Agent.Core.Abstractions;

namespace CDSI.Agent.Mac.Platform;

public sealed class MacKeychainSecretStore : ISecretStore
{
    public const string ServiceName = "com.cdsi.beacon";

    private readonly IMacKeychainApi _keychain;

    public MacKeychainSecretStore()
        : this(new SecurityFrameworkKeychainApi())
    {
    }

    internal MacKeychainSecretStore(IMacKeychainApi keychain)
    {
        _keychain = keychain ?? throw new ArgumentNullException(nameof(keychain));
    }

    public Task StoreAsync(
        string key,
        string secret,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        cancellationToken.ThrowIfCancellationRequested();

        _keychain.Store(ServiceName, key, secret);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_keychain.Exists(ServiceName, key));
    }

    public Task<string?> RetrieveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_keychain.Retrieve(ServiceName, key));
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        _keychain.Delete(ServiceName, key);
        return Task.CompletedTask;
    }

    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128 ||
            key.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Credential key contains invalid characters.",
                nameof(key));
        }
    }
}

public sealed class MacKeychainException : InvalidOperationException
{
    internal MacKeychainException(string operation, int status, string? detail)
        : base(CreateMessage(operation, status, detail))
    {
        Status = status;
    }

    public int Status { get; }

    private static string CreateMessage(
        string operation,
        int status,
        string? detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
        return $"macOS Keychain operation '{operation}' failed with OSStatus {status}{suffix}";
    }
}

internal interface IMacKeychainApi
{
    void Store(string service, string account, string secret);

    bool Exists(string service, string account);

    string? Retrieve(string service, string account);

    void Delete(string service, string account);
}

internal sealed class SecurityFrameworkKeychainApi : IMacKeychainApi
{
    private const int Success = 0;
    private const int DuplicateItem = -25299;
    private const int ItemNotFound = -25300;

    public void Store(string service, string account, string secret)
    {
        EnsureMacOS();
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var valueData = OwnedCoreFoundationObject.CreateData(secretBytes);
            using var attributes = CreateItemQuery(service, account);
            attributes.SetValue(
                SecurityFrameworkConstants.ValueData,
                valueData.Handle);

            var status = NativeMethods.SecItemAdd(attributes.Handle, IntPtr.Zero);
            if (status != DuplicateItem)
            {
                ThrowUnlessSuccess("store", status);
                return;
            }

            using var query = CreateItemQuery(service, account);
            using var updates = CoreFoundationDictionary.Create();
            updates.SetValue(
                SecurityFrameworkConstants.ValueData,
                valueData.Handle);
            status = NativeMethods.SecItemUpdate(query.Handle, updates.Handle);

            // The item may have been removed between Add and Update.
            if (status == ItemNotFound)
            {
                status = NativeMethods.SecItemAdd(attributes.Handle, IntPtr.Zero);
            }

            ThrowUnlessSuccess("store", status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public bool Exists(string service, string account)
    {
        EnsureMacOS();
        using var query = CreateItemQuery(service, account);
        query.SetValue(
            SecurityFrameworkConstants.MatchLimit,
            SecurityFrameworkConstants.MatchLimitOne);
        query.SetValue(
            SecurityFrameworkConstants.ReturnAttributes,
            CoreFoundationConstants.BooleanTrue);

        var status = NativeMethods.SecItemCopyMatching(
            query.Handle,
            out var result);
        if (result != IntPtr.Zero)
        {
            NativeMethods.CFRelease(result);
        }

        if (status == ItemNotFound)
        {
            return false;
        }

        ThrowUnlessSuccess("check", status);
        return true;
    }

    public string? Retrieve(string service, string account)
    {
        EnsureMacOS();
        using var query = CreateItemQuery(service, account);
        query.SetValue(
            SecurityFrameworkConstants.MatchLimit,
            SecurityFrameworkConstants.MatchLimitOne);
        query.SetValue(
            SecurityFrameworkConstants.ReturnData,
            CoreFoundationConstants.BooleanTrue);

        var status = NativeMethods.SecItemCopyMatching(
            query.Handle,
            out var result);
        if (status == ItemNotFound)
        {
            return null;
        }

        ThrowUnlessSuccess("retrieve", status);
        using var resultData = OwnedCoreFoundationObject.FromOwned(result);

        var length = checked((int)NativeMethods.CFDataGetLength(resultData.Handle));
        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[length];
        try
        {
            var bytePointer = NativeMethods.CFDataGetBytePtr(resultData.Handle);
            if (bytePointer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "macOS Keychain returned an invalid secret payload.");
            }

            Marshal.Copy(bytePointer, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(string service, string account)
    {
        EnsureMacOS();
        using var query = CreateItemQuery(service, account);
        var status = NativeMethods.SecItemDelete(query.Handle);
        if (status != ItemNotFound)
        {
            ThrowUnlessSuccess("delete", status);
        }
    }

    private static CoreFoundationDictionary CreateItemQuery(
        string service,
        string account)
    {
        var query = CoreFoundationDictionary.Create();
        try
        {
            query.SetValue(
                SecurityFrameworkConstants.Class,
                SecurityFrameworkConstants.ClassGenericPassword);
            using var serviceValue = OwnedCoreFoundationObject.CreateString(service);
            using var accountValue = OwnedCoreFoundationObject.CreateString(account);
            query.SetValue(
                SecurityFrameworkConstants.AttributeService,
                serviceValue.Handle);
            query.SetValue(
                SecurityFrameworkConstants.AttributeAccount,
                accountValue.Handle);
            return query;
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    private static void ThrowUnlessSuccess(string operation, int status)
    {
        if (status == Success)
        {
            return;
        }

        throw new MacKeychainException(
            operation,
            status,
            NativeMethods.CopySecurityErrorMessage(status));
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "macOS Keychain is only available on macOS.");
        }
    }
}

internal sealed class CoreFoundationDictionary : IDisposable
{
    private IntPtr _handle;

    private CoreFoundationDictionary(IntPtr handle)
    {
        _handle = handle;
    }

    public IntPtr Handle => _handle != IntPtr.Zero
        ? _handle
        : throw new ObjectDisposedException(nameof(CoreFoundationDictionary));

    public static CoreFoundationDictionary Create()
    {
        var handle = NativeMethods.CFDictionaryCreateMutable(
            IntPtr.Zero,
            0,
            CoreFoundationConstants.TypeDictionaryKeyCallbacks,
            CoreFoundationConstants.TypeDictionaryValueCallbacks);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Unable to allocate a Core Foundation dictionary.");
        }

        return new CoreFoundationDictionary(handle);
    }

    public void SetValue(IntPtr key, IntPtr value)
    {
        if (key == IntPtr.Zero || value == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "A required macOS framework constant is unavailable.");
        }

        NativeMethods.CFDictionarySetValue(Handle, key, value);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            NativeMethods.CFRelease(handle);
        }
    }
}

internal sealed class OwnedCoreFoundationObject : IDisposable
{
    private IntPtr _handle;

    private OwnedCoreFoundationObject(IntPtr handle)
    {
        _handle = handle != IntPtr.Zero
            ? handle
            : throw new InvalidOperationException(
                "Unable to allocate a Core Foundation object.");
    }

    public IntPtr Handle => _handle != IntPtr.Zero
        ? _handle
        : throw new ObjectDisposedException(nameof(OwnedCoreFoundationObject));

    public static OwnedCoreFoundationObject CreateString(string value) =>
        new(NativeMethods.CFStringCreateWithCString(
            IntPtr.Zero,
            value,
            NativeMethods.Utf8Encoding));

    public static OwnedCoreFoundationObject CreateData(byte[] value) =>
        new(NativeMethods.CFDataCreate(
            IntPtr.Zero,
            value,
            value.Length));

    public static OwnedCoreFoundationObject FromOwned(IntPtr handle) => new(handle);

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            NativeMethods.CFRelease(handle);
        }
    }
}

internal static class SecurityFrameworkConstants
{
    private static readonly Lazy<IntPtr> SecurityHandle = new(() =>
        NativeLibrary.Load(NativeMethods.SecurityFramework));

    public static IntPtr Class => ReadPointer("kSecClass");

    public static IntPtr ClassGenericPassword =>
        ReadPointer("kSecClassGenericPassword");

    public static IntPtr AttributeService => ReadPointer("kSecAttrService");

    public static IntPtr AttributeAccount => ReadPointer("kSecAttrAccount");

    public static IntPtr ValueData => ReadPointer("kSecValueData");

    public static IntPtr ReturnData => ReadPointer("kSecReturnData");

    public static IntPtr ReturnAttributes => ReadPointer("kSecReturnAttributes");

    public static IntPtr MatchLimit => ReadPointer("kSecMatchLimit");

    public static IntPtr MatchLimitOne => ReadPointer("kSecMatchLimitOne");

    private static IntPtr ReadPointer(string symbol)
    {
        var address = NativeLibrary.GetExport(SecurityHandle.Value, symbol);
        return Marshal.ReadIntPtr(address);
    }
}

internal static class CoreFoundationConstants
{
    private static readonly Lazy<IntPtr> CoreFoundationHandle = new(() =>
        NativeLibrary.Load(NativeMethods.CoreFoundation));

    public static IntPtr BooleanTrue => ReadPointer("kCFBooleanTrue");

    public static IntPtr TypeDictionaryKeyCallbacks =>
        NativeLibrary.GetExport(
            CoreFoundationHandle.Value,
            "kCFTypeDictionaryKeyCallBacks");

    public static IntPtr TypeDictionaryValueCallbacks =>
        NativeLibrary.GetExport(
            CoreFoundationHandle.Value,
            "kCFTypeDictionaryValueCallBacks");

    private static IntPtr ReadPointer(string symbol)
    {
        var address = NativeLibrary.GetExport(CoreFoundationHandle.Value, symbol);
        return Marshal.ReadIntPtr(address);
    }
}

internal static class NativeMethods
{
    internal const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    internal const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    internal const uint Utf8Encoding = 0x08000100;

    [DllImport(SecurityFramework)]
    internal static extern int SecItemAdd(
        IntPtr attributes,
        IntPtr result);

    [DllImport(SecurityFramework)]
    internal static extern int SecItemCopyMatching(
        IntPtr query,
        out IntPtr result);

    [DllImport(SecurityFramework)]
    internal static extern int SecItemUpdate(
        IntPtr query,
        IntPtr attributesToUpdate);

    [DllImport(SecurityFramework)]
    internal static extern int SecItemDelete(IntPtr query);

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecCopyErrorMessageString(
        int status,
        IntPtr reserved);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFDictionaryCreateMutable(
        IntPtr allocator,
        nint capacity,
        IntPtr keyCallbacks,
        IntPtr valueCallbacks);

    [DllImport(CoreFoundation)]
    internal static extern void CFDictionarySetValue(
        IntPtr dictionary,
        IntPtr key,
        IntPtr value);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        uint encoding);

    [DllImport(CoreFoundation)]
    internal static extern nint CFStringGetLength(IntPtr value);

    [DllImport(CoreFoundation)]
    internal static extern nint CFStringGetMaximumSizeForEncoding(
        nint length,
        uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CFStringGetCString(
        IntPtr value,
        IntPtr buffer,
        nint bufferSize,
        uint encoding);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFDataCreate(
        IntPtr allocator,
        byte[] bytes,
        nint length);

    [DllImport(CoreFoundation)]
    internal static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    internal static extern void CFRelease(IntPtr value);

    internal static string? CopySecurityErrorMessage(int status)
    {
        var message = SecCopyErrorMessageString(status, IntPtr.Zero);
        if (message == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var stringLength = CFStringGetLength(message);
            var maximumSize = CFStringGetMaximumSizeForEncoding(
                stringLength,
                Utf8Encoding);
            if (maximumSize < 0 || maximumSize >= int.MaxValue)
            {
                return null;
            }

            var bufferSize = checked((int)maximumSize + 1);
            var buffer = Marshal.AllocCoTaskMem(bufferSize);
            try
            {
                return CFStringGetCString(
                    message,
                    buffer,
                    bufferSize,
                    Utf8Encoding)
                    ? Marshal.PtrToStringUTF8(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
        finally
        {
            CFRelease(message);
        }
    }
}
