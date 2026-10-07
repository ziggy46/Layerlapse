using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Layerlapse.Core.Credentials;

/// <summary>
/// Windows Credential Manager, as a generic credential named "Layerlapse:printer:&lt;id&gt;".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Name => "Windows Credential Manager";

    public bool IsPersistent => true;

    public Task<string?> GetAsync(string printerId, CancellationToken cancellationToken = default) =>
        Task.Run<string?>(() =>
        {
            if (!CredReadW(Target(printerId), CredTypeGeneric, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorNotFound ? null : throw Failure("read the access code from", error);
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CredFree(pointer);
            }
        }, cancellationToken);

    public Task SaveAsync(string printerId, string accessCode, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var blob = Encoding.Unicode.GetBytes(accessCode);
            var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
            try
            {
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = Target(printerId),
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = handle.AddrOfPinnedObject(),
                    Persist = CredPersistLocalMachine,
                    UserName = "bblp",
                };
                if (!CredWriteW(ref credential, 0))
                {
                    throw Failure("save the access code to", Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                handle.Free();
            }
        }, cancellationToken);

    public Task RemoveAsync(string printerId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!CredDeleteW(Target(printerId), CredTypeGeneric, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound)
                {
                    throw Failure("remove the access code from", error);
                }
            }
        }, cancellationToken);

    private static string Target(string printerId) => $"{CredentialStores.ServiceName}:printer:{printerId}";

    private static CredentialStoreException Failure(string action, int error) =>
        new($"Could not {action} Windows Credential Manager ({new Win32Exception(error).Message}).");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
