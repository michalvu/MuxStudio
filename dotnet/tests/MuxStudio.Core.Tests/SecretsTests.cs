using System.Text;

namespace MuxStudio.Core.Tests;

/// <summary>Keyring giả: test KHÔNG BAO GIỜ chạm vào Windows Credential Manager thật (= <c>MemoryKeyring</c> của conftest.py).</summary>
public sealed class MemorySecretBackend : ISecretBackend
{
    public Dictionary<(string Service, string User), string> Store { get; } = [];

    public string? GetPassword(string service, string username) => Store.GetValueOrDefault((service, username));

    public void SetPassword(string service, string username, string password) => Store[(service, username)] = password;

    public void DeletePassword(string service, string username)
    {
        if (!Store.Remove((service, username)))
            throw new PasswordDeleteException("không có");
    }
}

/// <summary>Bảng Credential Manager giả trong bộ nhớ (target → mục thô).</summary>
public sealed class FakeCredentialTable : ICredentialTable
{
    public Dictionary<string, RawCredential> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RawCredential? Read(string target) => Items.GetValueOrDefault(target);

    public void Write(string target, string userName, byte[] blob) => Items[target] = new RawCredential(userName, blob);

    public void Delete(string target) => Items.Remove(target);
}

/// <summary>Bí mật: keyring, che khi hiển thị, không ra log. Port <c>tests/test_secrets.py</c>.</summary>
public class SecretsTests
{
    [Fact]
    public void Store_roundtrip()
    {
        var kr = new MemorySecretBackend();
        var st = new SecretStore(backend: kr);
        Assert.Equal("", st.Get("TmdbApiKey"));
        st.Set("TmdbApiKey", "  0123456789abcdef0123456789abcdef  ");
        Assert.Equal("0123456789abcdef0123456789abcdef", st.Get("TmdbApiKey"));
        Assert.Equal("0123456789abcdef0123456789abcdef", kr.Store[("MuxStudio", "TmdbApiKey")]);
        Assert.True(st.Has("TmdbApiKey"));
        st.Set("TmdbApiKey", ""); // rỗng = xoá
        Assert.False(st.Has("TmdbApiKey"));
        st.Delete("TmdbApiKey"); // xoá lần nữa không lỗi
    }

    [Fact]
    public void Only_known_secret_names() =>
        Assert.Throws<KeyNotFoundException>(() => new SecretStore(backend: new MemorySecretBackend()).Set("MinVerifyVotes", "20"));

    [Fact]
    public void All_values()
    {
        var st = new SecretStore(backend: new MemorySecretBackend());
        st.Set("OsUsername", "nguoidung");
        st.Set("OsPassword", "matkhau");
        Assert.Equal(new Dictionary<string, string> { ["OsUsername"] = "nguoidung", ["OsPassword"] = "matkhau" }, st.AllValues());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("abc", "•••")]
    [InlineData("matkhau1", "••••••••")]
    [InlineData("demo-key-0000111122223333444455556666", "demo…6666")]
    public void Mask(string v, string m) => Assert.Equal(m, Secrets.Mask(v));

    [Fact]
    public void Redactor_hides_every_secret_in_logs()
    {
        const string key = "demo-key-aaaabbbbccccddddeeee9999";
        var r = new Redactor([key, "matkhau", "ab"]);
        var line = $"GET /x?api_key={key} user=matkhau ab";
        var o = r.Redact(line);
        Assert.DoesNotContain(key, o, StringComparison.Ordinal);
        Assert.DoesNotContain("matkhau", o, StringComparison.Ordinal);
        Assert.Contains("demo…9999", o, StringComparison.Ordinal);
        Assert.EndsWith(" ab", o, StringComparison.Ordinal); // quá ngắn (< 4) -> không thay (tránh làm hỏng log)
    }

    // ---------- thêm cho bản C#: thuật toán WinVaultKeyring (tương thích key bản Python đã lưu) ----------

    [Fact]
    public void WinVault_reads_entries_written_by_python_keyring()
    {
        // Bản Python lưu TmdbApiKey trước rồi SubSourceApiKey: mục cũ bị chuyển sang target ghép "TmdbApiKey@MuxStudio".
        var t = new FakeCredentialTable();
        t.Write("MuxStudio", "SubSourceApiKey", Encoding.Unicode.GetBytes("ss-key-1234"));
        t.Write("TmdbApiKey@MuxStudio", "TmdbApiKey", Encoding.Unicode.GetBytes("tmdb-key-5678"));
        var st = new SecretStore(backend: new WinVaultBackend(t));
        Assert.Equal("ss-key-1234", st.Get("SubSourceApiKey"));
        Assert.Equal("tmdb-key-5678", st.Get("TmdbApiKey"));
        Assert.Equal("", st.Get("OsApiKey"));
    }

    [Fact]
    public void WinVault_set_moves_previous_entry_to_compound_target()
    {
        var t = new FakeCredentialTable();
        var kr = new WinVaultBackend(t);
        kr.SetPassword("MuxStudio", "TmdbApiKey", "a-key");
        kr.SetPassword("MuxStudio", "TvdbApiKey", "b-key");
        Assert.Equal("TvdbApiKey", t.Items["MuxStudio"].UserName);
        Assert.Equal("TmdbApiKey", t.Items["TmdbApiKey@MuxStudio"].UserName);
        Assert.Equal("a-key", Encoding.Unicode.GetString(t.Items["TmdbApiKey@MuxStudio"].Blob));
        Assert.Equal("a-key", kr.GetPassword("MuxStudio", "TmdbApiKey"));
        Assert.Equal("b-key", kr.GetPassword("MuxStudio", "TvdbApiKey"));
    }

    [Fact]
    public void WinVault_delete_only_matching_user_and_raises_when_missing()
    {
        var t = new FakeCredentialTable();
        var kr = new WinVaultBackend(t);
        kr.SetPassword("MuxStudio", "TmdbApiKey", "a-key");
        kr.SetPassword("MuxStudio", "TvdbApiKey", "b-key");
        kr.DeletePassword("MuxStudio", "TmdbApiKey");
        Assert.Null(kr.GetPassword("MuxStudio", "TmdbApiKey"));
        Assert.Equal("b-key", kr.GetPassword("MuxStudio", "TvdbApiKey"));
        Assert.Throws<PasswordDeleteException>(() => kr.DeletePassword("MuxStudio", "TmdbApiKey"));
        // SecretStore nuốt lỗi "không có"
        new SecretStore(backend: kr).Delete("TmdbApiKey");
    }

    [Fact]
    public void WinVault_blob_decodes_utf16_then_utf8()
    {
        Assert.Equal("Việt", WinVaultBackend.DecodeBlob(Encoding.Unicode.GetBytes("Việt")));
        Assert.Equal("abc", WinVaultBackend.DecodeBlob("abc"u8.ToArray())); // lẻ byte -> không phải UTF-16 -> UTF-8
        Assert.Equal("ab", WinVaultBackend.DecodeBlob([0xFF, 0xFE, (byte)'a', 0, (byte)'b', 0]));
    }
}
