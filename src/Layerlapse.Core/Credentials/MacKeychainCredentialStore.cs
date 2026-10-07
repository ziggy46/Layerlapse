using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Layerlapse.Core.Credentials;

/// <summary>
/// macOS login keychain, as a generic password with service "Layerlapse" and the printer id as account.
/// Uses the SecKeychain API: deprecated but still supported, and unlike the data protection keychain
/// it does not need a signed app with keychain entitlements.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecUserCanceled = -128;
    private const int ErrSecAuthFailed = -25293;

    private static readonly byte[] Service = Encoding.UTF8.GetBytes(CredentialStores.ServiceName);

    public string Name => "macOS Keychain";

    public bool IsPersistent => true;

    public Task<string?> GetAsync(string printerId, CancellationToken cancellationToken = default) =>
        Task.Run<string?>(() =>
        {
            var account = Encoding.UTF8.GetBytes(printerId);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                out var length, out var data, out var item);
            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            Check(status, "read the access code from");
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                CFRelease(item);
            }
        }, cancellationToken);

    public Task SaveAsync(string printerId, string accessCode, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var account = Encoding.UTF8.GetBytes(printerId);
            var password = Encoding.UTF8.GetBytes(accessCode);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                out _, out var data, out var item);
            if (status == ErrSecSuccess)
            {
                try
                {
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                    Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password), "update the access code in");
                }
                finally
                {
                    CFRelease(item);
                }

                return;
            }

            if (status != ErrSecItemNotFound)
            {
                Check(status, "read the access code from");
            }

            Check(SecKeychainAddGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                (uint)password.Length, password, out var added), "save the access code to");
            CFRelease(added);
        }, cancellationToken);

    public Task RemoveAsync(string printerId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var account = Encoding.UTF8.GetBytes(printerId);
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                out _, out var data, out var item);
            if (status == ErrSecItemNotFound)
            {
                return;
            }

            Check(status, "read the access code from");
            try
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                Check(SecKeychainItemDelete(item), "remove the access code from");
            }
            finally
            {
                CFRelease(item);
            }
        }, cancellationToken);

    private static void Check(int status, string action)
    {
        if (status == ErrSecSuccess)
        {
            return;
        }

        var reason = status switch
        {
            ErrSecUserCanceled or ErrSecAuthFailed => "access was not allowed",
            _ => $"error {status}",
        };
        throw new CredentialStoreException($"Could not {action} the macOS Keychain ({reason}).");
    }

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr cf);
}
