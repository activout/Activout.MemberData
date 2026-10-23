using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Activout.MemberData.Cli;

/// <summary>Get/set/delete one named secret. Implementations never log or echo values.</summary>
internal interface ICredentialStore
{
    string Name { get; }
    string? Get(string key);
    void Set(string key, string value);
    bool Delete(string key);
}

/// <summary>The secure store is missing or locked (for example a headless Linux host without a Secret Service).</summary>
internal sealed class CredentialStoreUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The access token plus safe metadata, stored together as one secret.</summary>
internal sealed record StoredToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("imported_at")] DateTimeOffset? ImportedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt)
{
    public const string Key = "access-token";

    public string Serialize() => JsonSerializer.Serialize(this);

    public static StoredToken? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<StoredToken>(value);
        }
        catch (JsonException)
        {
            throw new CredentialStoreUnavailableException(
                "The stored credential is not in the expected format; run 'member auth logout' and import again");
        }
    }

    /// <summary>Never print the token itself.</summary>
    public override string ToString() => $"StoredToken(imported {ImportedAt:O}, expires {ExpiresAt:O})";
}

internal static class CredentialSources
{
    public const string Keychain = "keychain";
    public const string Environment = "environment";
    public const string SourceVariable = "MEMBERDATA_CREDENTIAL_SOURCE";
    public const string TokenVariable = "MEMBERDATA_ACCESS_TOKEN";
    public const string ExpiresVariable = "MEMBERDATA_TOKEN_EXPIRES_AT";
}

/// <summary>
/// Read-only credentials from environment variables. Only used when explicitly selected; never a fallback.
/// Environment variables can leak to child processes, crash dumps and process listings.
/// </summary>
internal sealed class EnvironmentCredentialStore(Func<string, string?> getEnv) : ICredentialStore
{
    public string Name => "environment variables (explicit opt-in)";

    public string? Get(string key)
    {
        if (key != StoredToken.Key) return null;
        var token = getEnv(CredentialSources.TokenVariable);
        if (string.IsNullOrWhiteSpace(token)) return null;
        DateTimeOffset? expires = DateTimeOffset.TryParse(getEnv(CredentialSources.ExpiresVariable),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal,
            out var e) ? e : null;
        return new StoredToken(token.Trim(), null, expires).Serialize();
    }

    public void Set(string key, string value) => throw new MemberUsageException(
        $"The environment credential source is read-only; set {CredentialSources.TokenVariable} yourself");

    public bool Delete(string key) => throw new MemberUsageException(
        $"The environment credential source is read-only; unset {CredentialSources.TokenVariable} yourself");
}

/// <summary>
/// The operating system's secure store: macOS Keychain, Windows Credential Manager or the Linux Secret Service
/// (through <c>secret-tool</c>, secrets on stdin/stdout only). There is no fallback to plaintext.
/// </summary>
internal sealed class KeychainCredentialStore : ICredentialStore
{
    private const string Service = "Activout.MemberData";

    public string Name => OperatingSystem.IsMacOS() ? "macOS Keychain"
        : OperatingSystem.IsWindows() ? "Windows Credential Manager"
        : OperatingSystem.IsLinux() ? "Linux Secret Service (secret-tool)"
        : "unsupported platform";

    public string? Get(string key)
    {
        if (OperatingSystem.IsMacOS()) return MacKeychain.Get(Service, key);
        if (OperatingSystem.IsWindows()) return WindowsCredentials.Get(Service + ":" + key);
        if (OperatingSystem.IsLinux()) return SecretTool.Get(Service, key);
        throw new CredentialStoreUnavailableException($"No secure credential store on {RuntimeInformation.OSDescription}");
    }

    public void Set(string key, string value)
    {
        if (OperatingSystem.IsMacOS()) MacKeychain.Set(Service, key, value);
        else if (OperatingSystem.IsWindows()) WindowsCredentials.Set(Service + ":" + key, value);
        else if (OperatingSystem.IsLinux()) SecretTool.Set(Service, key, value);
        else throw new CredentialStoreUnavailableException($"No secure credential store on {RuntimeInformation.OSDescription}");
    }

    public bool Delete(string key)
    {
        if (OperatingSystem.IsMacOS()) return MacKeychain.Delete(Service, key);
        if (OperatingSystem.IsWindows()) return WindowsCredentials.Delete(Service + ":" + key);
        if (OperatingSystem.IsLinux()) return SecretTool.Delete(Service, key);
        throw new CredentialStoreUnavailableException($"No secure credential store on {RuntimeInformation.OSDescription}");
    }

    private static class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ErrSecSuccess = 0;
        private const int ErrSecItemNotFound = -25300;

        // The SecKeychain* generic password calls are deprecated but remain the simplest stable C API for the
        // login keychain; SecItem* would need CFDictionary marshalling for the same result.
        [DllImport(Security)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);

        [DllImport(Security)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, uint passwordLength, byte[] passwordData, IntPtr item);

        [DllImport(Security)]
        private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attrList, uint length,
            byte[] data);

        [DllImport(Security)]
        private static extern int SecKeychainItemDelete(IntPtr item);

        [DllImport(Security)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        public static string? Get(string service, string account)
        {
            var status = Find(service, account, out var length, out var data, out var item);
            if (status == ErrSecItemNotFound) return null;
            Check(status, "read");
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero) CFRelease(item);
            }
        }

        public static void Set(string service, string account, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var status = Find(service, account, out _, out var data, out var item);
            if (status == ErrSecSuccess)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                try
                {
                    Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes), "update");
                }
                finally
                {
                    CFRelease(item);
                }

                return;
            }

            if (status != ErrSecItemNotFound) Check(status, "read");
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)bytes.Length,
                bytes, IntPtr.Zero), "store");
        }

        public static bool Delete(string service, string account)
        {
            var status = Find(service, account, out _, out var data, out var item);
            if (status == ErrSecItemNotFound) return false;
            Check(status, "read");
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try
            {
                Check(SecKeychainItemDelete(item), "delete");
                return true;
            }
            finally
            {
                CFRelease(item);
            }
        }

        private static int Find(string service, string account, out uint length, out IntPtr data, out IntPtr item)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            return SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out length,
                out data, out item);
        }

        private static void Check(int status, string action)
        {
            if (status != ErrSecSuccess)
                throw new CredentialStoreUnavailableException(
                    $"macOS Keychain could not {action} the credential (OSStatus {status}); is the login keychain unlocked?");
        }
    }

    private static class WindowsCredentials
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
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
            public string? UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
        private static extern bool CredWrite(ref Credential credential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        public static string? Get(string target)
        {
            if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound) return null;
                throw new CredentialStoreUnavailableException($"Windows Credential Manager read failed (error {error})");
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static void Set(string target, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new Credential
                {
                    Type = CredTypeGeneric, TargetName = target, CredentialBlobSize = bytes.Length,
                    CredentialBlob = blob, Persist = CredPersistLocalMachine, UserName = Environment.UserName,
                };
                if (!CredWrite(ref credential, 0))
                    throw new CredentialStoreUnavailableException(
                        $"Windows Credential Manager write failed (error {Marshal.GetLastWin32Error()})");
            }
            finally
            {
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
            }
        }

        public static bool Delete(string target)
        {
            if (CredDelete(target, CredTypeGeneric, 0)) return true;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return false;
            throw new CredentialStoreUnavailableException($"Windows Credential Manager delete failed (error {error})");
        }
    }

    /// <summary>libsecret's secret-tool. The secret only passes through stdin (store) and stdout (lookup).</summary>
    private static class SecretTool
    {
        public static string? Get(string service, string account)
        {
            var (code, stdout, stderr) = Run(null, "lookup", "service", service, "account", account);
            if (code == 0) return stdout;
            // secret-tool exits 1 with no output when nothing matches.
            if (code == 1 && string.IsNullOrWhiteSpace(stderr)) return null;
            throw Unavailable(stderr);
        }

        public static void Set(string service, string account, string value)
        {
            var (code, _, stderr) = Run(value, "store", "--label=Activout.MemberData " + account, "service", service,
                "account", account);
            if (code != 0) throw Unavailable(stderr);
        }

        public static bool Delete(string service, string account)
        {
            var existed = Get(service, account) != null;
            var (code, _, stderr) = Run(null, "clear", "service", service, "account", account);
            if (code != 0 && existed) throw Unavailable(stderr);
            return existed;
        }

        private static (int Code, string Stdout, string Stderr) Run(string? stdin, params string[] args)
        {
            var info = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            Process process;
            try
            {
                process = Process.Start(info) ?? throw Unavailable("could not start secret-tool");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new CredentialStoreUnavailableException(
                    "secret-tool (libsecret-tools) is not installed. Install it, or use --credential-source environment " +
                    "on headless hosts at your own risk", e);
            }

            using (process)
            {
                if (stdin != null) process.StandardInput.Write(stdin);
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, stdout, stderr);
            }
        }

        private static CredentialStoreUnavailableException Unavailable(string detail) => new(
            "The Linux Secret Service is unavailable (headless hosts usually have none unlocked): " + detail.Trim() +
            ". Use --credential-source environment at your own risk.");
    }
}
