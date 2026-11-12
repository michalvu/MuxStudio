using MuxStudio.Core.Tests.Support;

namespace MuxStudio.Core.Tests;

/// <summary>Hai ngôn ngữ giao diện: bảng dịch đủ, đúng số chỗ chèn, ghép câu đúng; tiếng Việt không đổi gì. Port <c>tests/test_i18n.py</c>.</summary>
[Collection(GlobalStateCollection.Name)]
public sealed class I18nTests : IDisposable
{
    /// <summary>Chữ tiếng Việt được phép nằm trong bản tiếng Anh (tên ngôn ngữ, lời nhắc song ngữ ở ô chọn ngôn ngữ).</summary>
    private static readonly string[] AllowedVi = ["Tiếng Việt", "Ngôn ngữ", "Có hiệu lực khi mở lại MuxStudio"];

    public I18nTests() => I18n.SetLanguage("vi"); // = fixture vietnamese_ui

    public void Dispose() => I18n.SetLanguage("vi");

    // test_every_displayed_string_has_a_translation: quét mã nguồn Python bằng tools/i18n_extract.py — công cụ của bản
    // Python, chưa port. Bản C# sẽ có bài tương tự khi port giao diện (GĐ5).

    [Fact]
    public void Translations_keep_placeholders_and_have_no_vietnamese_left()
    {
        Assert.True(I18n.EnEntries.Count > 1200);
        foreach (var (vi, en) in I18n.EnEntries)
        {
            Assert.True(I18n.SlotsOk(vi, en), $"{vi} / {en}");
            var rest = en;
            foreach (var a in AllowedVi)
                rest = rest.Replace(a, "", StringComparison.Ordinal);
            Assert.False(I18n.HasVietnamese(rest), $"{vi} / {en}");
            // khoảng trắng / xuống dòng ở hai đầu giữ nguyên (câu được nối với phần khác)
            static bool Sp(string s, bool first) => s.Length > 0 && char.IsWhiteSpace(first ? s[0] : s[^1]);
            Assert.Equal((Sp(vi, true), Sp(vi, false)), (Sp(en, true), Sp(en, false)));
        }
    }

    [Fact]
    public void Vietnamese_is_identity_and_unknown_language_falls_back()
    {
        Assert.Equal("vi", I18n.Language);
        var s = "▶ 1. Quét phụ đề";
        Assert.Same(s, I18n.Tr(s));
        Assert.Null(I18n.Tr(null));
        Assert.Equal("vi", I18n.SetLanguage("fr"));
        Assert.Equal("en", I18n.SetLanguage("EN"));
        Assert.Equal("vi", I18n.SetLanguage(null));
        Assert.Equal("Ready.", I18n.TranslateTo("en", "Sẵn sàng."));
        Assert.Equal("vi", I18n.Language);
    }

    [Theory]
    [InlineData("Lưu cài đặt", "Save settings")]
    [InlineData("▶ 1. Quét phụ đề", "▶ 1. Scan subtitles")] // ký hiệu + số bước ở đầu
    [InlineData("✓ 2. Đổi tên", "✓ 2. Rename")]
    [InlineData("Sang ③ Mux →", "Go to ③ Mux →")] // câu KHÔNG DẤU
    [InlineData("Xong: Kiểm tra công cụ.", "Done: Checking tools.")] // giá trị chèn vào cũng được dịch
    [InlineData("Đang chạy: Lập kế hoạch mux…", "Running: Planning the mux…")]
    [InlineData("  [!] File không có audio track nào.", "  [!] The file has no audio track.")]
    [InlineData("KẾ HOẠCH: 3 sẵn sàng | 0 cần chọn | 1 bỏ qua | 0 lỗi", "PLAN: 3 ready | 0 need choice | 1 skipped | 0 errors")]
    [InlineData("dùng file có sẵn (25 mốc khớp): a.srt", "using existing file (25 matching cues): a.srt")]
    [InlineData("file đã có en, cs → bỏ qua", "file already has en, cs → skipped")] // hai mảnh nối vào nhau
    [InlineData("TỔNG KẾT: 2 phụ đề đã tải | 1 dùng file có sẵn | 0 không tìm được | 3 bỏ qua",
        "SUMMARY: 2 subtitles downloaded | 1 existing files used | 0 not found | 3 skipped")]
    [InlineData("15 dòng · 1 cảnh báo · 2 lỗi", "15 lines · 1 warnings · 2 errors")]
    [InlineData("0 TỐT | 1 CẦN XEM | 0 CÓ VẤN ĐỀ | 0 lỗi/từ chối", "0 GOOD | 1 REVIEW | 0 PROBLEM | 0 errors/rejected")]
    [InlineData("1 thư mục mới, 2 tạo, 2 vào Thùng rác", "1 new folders, 2 created, 2 moved to the Recycle Bin")]
    [InlineData("Cần chọn: có 2 audio tiếng Anh (#1, #2) — tick track muốn xoá",
        "Needs choice: 2 English audio tracks (#1, #2) — tick the tracks to remove")]
    [InlineData("      [v] Khớp thời gian, lệch +0.1s (bước ② sẽ đồng bộ)", "      [v] Timing matches, offset +0.1s (step ② will sync it)")]
    [InlineData("[i] Ràng buộc framerate: BẬT (cho phép bản không khai báo fps).",
        "[i] Framerate constraint: ON (releases without declared fps are allowed).")]
    [InlineData("phim đã có (trong MKV: track 2 SRT) — không tìm", "video already has it (inside the MKV: track 2 SRT) — not searching")]
    [InlineData("Dòng 1\nSẵn sàng.", "Dòng 1\nReady.")] // từng dòng một; dòng không dịch được giữ nguyên
    public void English_translation_of_real_sentences(string vi, string expected)
    {
        I18n.SetLanguage("en");
        Assert.Equal(expected, I18n.Tr(vi));
    }

    [Fact]
    public void File_names_and_untranslatable_text_are_left_alone()
    {
        I18n.SetLanguage("en");
        foreach (var s in new[] { "Phim Thử (2020).mkv", "[YTS.MX] Phim Thử (2020)", "C:\\Phim\\Tên Tập.S01E05.vi.srt",
                     "Show.S01E01.1080p.WEB.mkv", "37% · 3/8", "", "en" })
            Assert.Equal(s, I18n.Tr(s));
        Assert.Equal("Video folder: C:\\Phim Thử", I18n.Tr("Thư mục phim: C:\\Phim Thử")); // chỉ dịch phần nhãn
    }

    [Fact]
    public void Fill_supports_sequential_and_numbered_slots()
    {
        Assert.Equal("a of b", I18n.Fill("{} of {}", ["a", "b"]));
        Assert.Equal("b then a", I18n.Fill("{1} then {0}", ["a", "b"]));
        Assert.True(I18n.SlotsOk("{} và {}", "{1} and {0}"));
        Assert.False(I18n.SlotsOk("{} và {}", "{} only"));
        Assert.False(I18n.SlotsOk("{}", "{0} {1}"));
    }

    // ---- thêm cho bản C#: so với thuật toán dò ngược của bản Python trên ~3500 câu đã ghép giá trị ----
    [Fact]
    public void Matches_python_on_generated_sentences()
    {
        var cases = (List<object?>)PyJson.Loads(File.ReadAllText(RepoPaths.Data("python_i18n_cases.json"), Py.Utf8Strict))!;
        Assert.True(cases.Count > 3000);
        var diffs = new List<string>();
        foreach (var c in cases.Cast<List<object?>>())
        {
            var vi = (string)c[0]!;
            var expected = (string)c[1]!;
            var got = I18n.TranslateTo("en", vi);
            if (got != expected)
                diffs.Add($"\n  vi: {vi}\n  py: {expected}\n  c#: {got}");
        }
        Assert.True(diffs.Count == 0, $"{diffs.Count}/{cases.Count} câu khác bản Python:" + string.Concat(diffs.Take(8)));
    }
}
