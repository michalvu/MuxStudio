using System.Runtime.InteropServices;
using System.Text;

namespace MuxStudio.Core;

/// <summary>Không đọc/ghi được Windows Credential Manager.</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Không có mục để xoá (= <c>keyring.errors.PasswordDeleteError</c>).</summary>
public sealed class PasswordDeleteException(string message) : Exception(message);

/// <summary>Nơi cất bí mật (= backend của <c>keyring</c>): Windows Credential Manager thật, hoặc bộ nhớ trong test.</summary>
public interface ISecretBackend
{
    string? GetPassword(string service, string username);

    void SetPassword(string service, string username, string password);

    /// <summary>Xoá; không có mục → <see cref="PasswordDeleteException"/>.</summary>
    void DeletePassword(string service, string username);
}

/// <summary>Một mục thô trong Credential Manager (kiểu GENERIC).</summary>
public sealed record RawCredential(string UserName, byte[] Blob);

/// <summary>Bảng mục thô của Credential Manager — tách ra để test thuật toán mà không chạm Credential Manager thật.</summary>
public interface ICredentialTable
{
    RawCredential? Read(string target);

    void Write(string target, string userName, byte[] blob);

    void Delete(string target);
}

/// <summary>
/// Bản sao thuật toán <c>keyring.backends.Windows.WinVaultKeyring</c> để đọc/ghi CÙNG chỗ với bản Python
/// (key đã lưu bằng bản Python vẫn đọc được, và ngược lại):
/// mỗi bí mật nằm ở target <c>service</c> nếu UserName khớp, nếu không thì ở target <c>"&lt;username&gt;@&lt;service&gt;"</c>.
/// Ghi mới: mục đang ở target <c>service</c> (của tên khác) được chuyển sang target ghép trước.
/// Dữ liệu ghi dạng UTF-16LE; khi đọc thử UTF-16 rồi UTF-8.
/// </summary>
public sealed class WinVaultBackend(ICredentialTable table) : ISecretBackend
{
    public WinVaultBackend() : this(new NativeCredentialTable())
    {
    }

    private static string CompoundName(string username, string service) => $"{username}@{service}";

    public string? GetPassword(string service, string username)
    {
        var res = Resolve(service, username);
        return res is null ? null : DecodeBlob(res.Blob);
    }

    private RawCredential? Resolve(string service, string? username)
    {
        var res = table.Read(service);
        if (res is null || (!string.IsNullOrEmpty(username) && res.UserName != username))
            res = table.Read(CompoundName(username ?? "None", service));
        return res;
    }

    public void SetPassword(string service, string username, string password)
    {
        var existing = table.Read(service);
        if (existing is not null)
        {
            // lưu lại mật khẩu đang có dưới target ghép
            table.Write(CompoundName(existing.UserName, service), existing.UserName,
                EncodeBlob(DecodeBlob(existing.Blob)));
        }
        table.Write(service, username, EncodeBlob(password));
    }

    public void DeletePassword(string service, string username)
    {
        var deleted = false;
        foreach (var target in new[] { service, CompoundName(username, service) })
        {
            var existing = table.Read(target);
            if (existing is not null && existing.UserName == username)
            {
                deleted = true;
                table.Delete(target);
            }
        }
        if (!deleted)
            throw new PasswordDeleteException(service);
    }

    /// <summary>pywin32-ctypes ghi chuỗi dạng UTF-16LE (không BOM).</summary>
    internal static byte[] EncodeBlob(string s) => Encoding.Unicode.GetBytes(s);

    /// <summary><c>DecodingCredential.value</c>: thử <c>decode('utf-16')</c> (có BOM thì theo BOM, không thì LE), rồi UTF-8.</summary>
    internal static string DecodeBlob(byte[] blob)
    {
        try
        {
            if (blob.Length % 2 != 0)
                throw new DecoderFallbackException("odd length");
            if (blob.Length >= 2 && blob[0] == 0xFF && blob[1] == 0xFE)
                return new UnicodeEncoding(false, false, true).GetString(blob, 2, blob.Length - 2);
            if (blob.Length >= 2 && blob[0] == 0xFE && blob[1] == 0xFF)
                return new UnicodeEncoding(true, false, true).GetString(blob, 2, blob.Length - 2);
            return new UnicodeEncoding(false, false, true).GetString(blob);
        }
        catch (DecoderFallbackException)
        {
            return Py.Utf8Strict.GetString(blob);
        }
    }
}

/// <summary>Gọi thẳng <c>CredReadW</c>/<c>CredWriteW</c>/<c>CredDeleteW</c> của Windows (kiểu GENERIC, persist ENTERPRISE như keyring).</summary>
public sealed partial class NativeCredentialTable : ICredentialTable
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistEnterprise = 3;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);

    public RawCredential? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var p))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ErrorNotFound)
                return null;
            throw new System.ComponentModel.Win32Exception(err);
        }
        try
        {
            var c = Marshal.PtrToStructure<NativeCredential>(p);
            var blob = new byte[c.CredentialBlobSize];
            if (c.CredentialBlobSize > 0)
                Marshal.Copy(c.CredentialBlob, blob, 0, blob.Length);
            var user = c.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(c.UserName) ?? "";
            return new RawCredential(user, blob);
        }
        finally
        {
            CredFree(p);
        }
    }

    public void Write(string target, string userName, byte[] blob)
    {
        var pTarget = Marshal.StringToCoTaskMemUni(target);
        var pUser = Marshal.StringToCoTaskMemUni(userName);
        var pComment = Marshal.StringToCoTaskMemUni("Stored using python-keyring");
        var pBlob = Marshal.AllocCoTaskMem(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, pBlob, blob.Length);
            var c = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = pTarget,
                Comment = pComment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = pBlob,
                Persist = CredPersistEnterprise,
                UserName = pUser,
            };
            if (!CredWrite(ref c, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.FreeCoTaskMem(pTarget);
            Marshal.FreeCoTaskMem(pUser);
            Marshal.FreeCoTaskMem(pComment);
            Marshal.FreeCoTaskMem(pBlob);
        }
    }

    public void Delete(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err != ErrorNotFound)
                throw new System.ComponentModel.Win32Exception(err);
        }
    }
}

/// <summary>
/// Bí mật (API key, tài khoản) — lưu trong Windows Credential Manager. Port <c>core/secrets.py</c>.
/// Không bao giờ nằm trong <c>config.json</c>, không bao giờ in ra log (<see cref="Redactor"/>), che khi hiển thị (<see cref="Mask"/>).
/// </summary>
public static class Secrets
{
    public const string Service = "MuxStudio";

    /// <summary>9 bí mật (docs/SPEC.md mục 2.4): 7 của ba file .bat + tài khoản Titulky.com (bản 1.3.0, TĐ-40).</summary>
    public static readonly IReadOnlyList<string> SecretKeys =
    [
        "SubSourceApiKey",
        "TmdbApiKey",
        "TvdbApiKey",
        "TvdbPin",
        "OsApiKey",
        "OsUsername",
        "OsPassword",
        "TitulkyUsername",
        "TitulkyPassword",
    ];

    private static ISecretBackend? _backend;

    /// <summary>Backend đang dùng (= <c>keyring.get_keyring()</c>). Mặc định: Windows Credential Manager. Test thay bằng bộ nhớ.</summary>
    public static ISecretBackend Backend
    {
        get => _backend ??= new WinVaultBackend();
        set => _backend = value;
    }

    /// <summary>Dùng chung trong chương trình.</summary>
    public static SecretStore Store { get; } = new();

    /// <summary>Bản che để hiển thị: <c>sk_5…a69a</c> (dài &gt; 8 ký tự), <c>••••••</c> (ngắn).</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value.Length <= 8)
            return new string('•', value.Length);
        return $"{value[..4]}…{value[^4..]}";
    }
}

/// <summary>Đọc/ghi bí mật. Mỗi bí mật là một mục <c>MuxStudio / &lt;tên&gt;</c> trong Credential Manager.</summary>
public sealed class SecretStore(string service = Secrets.Service, ISecretBackend? backend = null)
{
    public string ServiceName { get; } = service;

    private ISecretBackend Kr => backend ?? Secrets.Backend;

    private static void Check(string name)
    {
        if (!Secrets.SecretKeys.Contains(name))
            throw new KeyNotFoundException($"Không phải bí mật: {name}");
    }

    public string Get(string name)
    {
        Check(name);
        try
        {
            return Kr.GetPassword(ServiceName, name) ?? "";
        }
        catch (Exception e) when (e is not KeyNotFoundException)
        {
            throw new SecretStoreException($"Không đọc được {name} từ Windows Credential Manager: {e.Message}", e);
        }
    }

    /// <summary>Ghi bí mật. Giá trị rỗng = xoá.</summary>
    public void Set(string name, string? value)
    {
        Check(name);
        value = (value ?? "").Trim();
        if (value.Length == 0)
        {
            Delete(name);
            return;
        }
        try
        {
            Kr.SetPassword(ServiceName, name, value);
        }
        catch (Exception e)
        {
            throw new SecretStoreException($"Không ghi được {name} vào Windows Credential Manager: {e.Message}", e);
        }
    }

    public void Delete(string name)
    {
        Check(name);
        try
        {
            Kr.DeletePassword(ServiceName, name);
        }
        catch (PasswordDeleteException)
        {
            // không có mục này -> coi như đã xoá
        }
        catch (Exception e)
        {
            throw new SecretStoreException($"Không xoá được {name}: {e.Message}", e);
        }
    }

    public bool Has(string name) => Get(name).Length > 0;

    public Dictionary<string, string> AllValues()
    {
        var outp = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in Secrets.SecretKeys)
        {
            string v;
            try
            {
                v = Get(k);
            }
            catch (SecretStoreException)
            {
                v = "";
            }
            if (v.Length > 0)
                outp[k] = v;
        }
        return outp;
    }
}

/// <summary>Thay mọi bí mật đang có trong một dòng log bằng bản che. Dùng cho khung log và file log.</summary>
public sealed class Redactor
{
    private readonly Lock _lock = new();
    private List<string> _vals = [];

    public Redactor(IEnumerable<string?>? values = null) => Update(values ?? []);

    public void Update(IEnumerable<string?> values)
    {
        var vals = values.Where(v => !string.IsNullOrEmpty(v) && v.Length >= 4).Select(v => v!)
            .Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToList();
        lock (_lock)
            _vals = vals;
    }

    /// <summary>Thêm một bí mật mới biết lúc chạy (vd token đăng nhập).</summary>
    public void Add(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            Update([.. _vals, value]);
    }

    public string Redact(string text)
    {
        List<string> vals;
        lock (_lock)
            vals = _vals;
        foreach (var v in vals)
        {
            if (text.Contains(v, StringComparison.Ordinal))
                text = text.Replace(v, v.Length > 8 ? Secrets.Mask(v) : "•••", StringComparison.Ordinal);
        }
        return text;
    }
}
