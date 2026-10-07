using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Layerlapse.Core.Credentials;

/// <summary>
/// Linux Secret Service (GNOME Keyring, KWallet, ...) through libsecret. Stores each code with the
/// attribute serial=&lt;id&gt; under the schema "app.layerlapse.PrinterAccessCode".
/// Uses the GHashTable ("v") variants of the libsecret calls, which can be called without varargs.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LibSecretCredentialStore : ICredentialStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    private const string SchemaName = "app.layerlapse.PrinterAccessCode";
    private const string AttributeName = "printer";

    private static readonly Lazy<Native?> Natives = new(Native.TryLoad);

    public string Name => "the system keyring (Secret Service)";

    public bool IsPersistent => true;

    /// <summary>Whether libsecret and GLib can be loaded. A Secret Service daemon may still be missing.</summary>
    public static bool IsAvailable() => Natives.Value is not null;

    public Task<string?> GetAsync(string printerId, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithAttributes<string?>(printerId, (native, schema, attributes) =>
        {
            var result = secret_password_lookupv_sync(schema, attributes, IntPtr.Zero, out var error);
            ThrowIfError(error, "read the access code from");
            if (result == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUTF8(result);
            }
            finally
            {
                secret_password_free(result);
            }
        }), cancellationToken);

    public Task SaveAsync(string printerId, string accessCode, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithAttributes<object?>(printerId, (native, schema, attributes) =>
        {
            var ok = secret_password_storev_sync(
                schema, attributes, null, $"Layerlapse printer {printerId}", accessCode, IntPtr.Zero, out var error);
            ThrowIfError(error, "save the access code to");
            return ok ? null : throw new CredentialStoreException("Could not save the access code to the system keyring.");
        }), cancellationToken);

    public Task RemoveAsync(string printerId, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithAttributes<object?>(printerId, (native, schema, attributes) =>
        {
            _ = secret_password_clearv_sync(schema, attributes, IntPtr.Zero, out var error);
            ThrowIfError(error, "remove the access code from");
            return null;
        }), cancellationToken);

    private static T WithAttributes<T>(string printerId, Func<Native, IntPtr, IntPtr, T> action)
    {
        var native = Natives.Value ?? throw new CredentialStoreException("libsecret is not installed, so access codes cannot be saved.");
        var key = Marshal.StringToCoTaskMemUTF8(AttributeName);
        var value = Marshal.StringToCoTaskMemUTF8(printerId);
        var table = g_hash_table_new(native.StrHash, native.StrEqual);
        try
        {
            g_hash_table_insert(table, key, value);
            return action(native, native.Schema, table);
        }
        finally
        {
            g_hash_table_unref(table);
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static void ThrowIfError(IntPtr error, string action)
    {
        if (error == IntPtr.Zero)
        {
            return;
        }

        // GError { GQuark domain; gint code; gchar *message; }
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
        g_error_free(error);
        throw new CredentialStoreException($"Could not {action} the system keyring ({message}).");
    }

    /// <summary>Loaded native entry points plus a SecretSchema that lives for the process.</summary>
    private sealed class Native
    {
        private Native(IntPtr strHash, IntPtr strEqual, IntPtr schema)
        {
            StrHash = strHash;
            StrEqual = strEqual;
            Schema = schema;
        }

        public IntPtr StrHash { get; }

        public IntPtr StrEqual { get; }

        public IntPtr Schema { get; }

        public static Native? TryLoad()
        {
            if (!NativeLibrary.TryLoad(LibSecret, out _) || !NativeLibrary.TryLoad(LibGlib, out var glib))
            {
                return null;
            }

            if (!NativeLibrary.TryGetExport(glib, "g_str_hash", out var strHash)
                || !NativeLibrary.TryGetExport(glib, "g_str_equal", out var strEqual))
            {
                return null;
            }

            return new Native(strHash, strEqual, CreateSchema());
        }

        // struct SecretSchema {
        //   const gchar *name; SecretSchemaFlags flags;
        //   SecretSchemaAttribute attributes[32];   // { const gchar *name; SecretSchemaAttributeType type; }
        //   gint reserved; gpointer reserved1..reserved7;
        // }
        private static IntPtr CreateSchema()
        {
            var pointer = IntPtr.Size;
            var attributeSize = pointer * 2; // name pointer + enum, padded
            var attributesOffset = pointer * 2; // name + flags (padded)
            var reservedOffset = attributesOffset + (32 * attributeSize);
            var size = reservedOffset + pointer + (7 * pointer);

            var schema = Marshal.AllocHGlobal(size);
            for (var i = 0; i < size; i++)
            {
                Marshal.WriteByte(schema, i, 0);
            }

            Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
            Marshal.WriteInt32(schema, pointer, 0); // SECRET_SCHEMA_NONE
            Marshal.WriteIntPtr(schema, attributesOffset, Marshal.StringToCoTaskMemUTF8(AttributeName));
            Marshal.WriteInt32(schema, attributesOffset + pointer, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
            return schema;
        }
    }

    [DllImport(LibSecret)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool secret_password_storev_sync(
        IntPtr schema, IntPtr attributes,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);

    [DllImport(LibSecret)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(LibGlib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr keyEqualFunc);

    [DllImport(LibGlib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(LibGlib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(LibGlib)]
    private static extern void g_error_free(IntPtr error);
}
