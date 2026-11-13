using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MuxStudio.Core.Ui;

namespace MuxStudio.App.Views;

/// <summary>Thẻ "Giới thiệu" (port gui/about_view.py): tác giả, phiên bản, bản quyền, ghi nhận phần mềm / dịch vụ của bên khác. Bấm đúp một dòng
/// để mở trang chủ.</summary>
public partial class AboutView : UserControl, IStepView
{
    private readonly MainWindow _app;
    private readonly List<string> _urls = [];

    public AboutView(MainWindow app)
    {
        _app = app;
        InitializeComponent();
        TitleText.Text = AppInfo.Title;
        AuthorText.Text = L.T($"Tác giả: {AboutUi.Author}");
        CopyrightText.Text = L.T($"{AboutUi.Copyright}. Giữ mọi quyền với phần mã của MuxStudio.");
        WhatText.Text = L.T("Tải phụ đề → đồng bộ phụ đề → dọn track và mux MKV, đặt tên chuẩn Jellyfin.");
        NoteText.Text = L.T(AboutUi.OwnershipNote);
        AttrText.Text = string.Join("\n", AboutUi.Attributions);
        var table = new Table(CreditGrid,
        [
            new Col("owner", "Bản quyền thuộc về", 260, Stretch: true), new Col("lic", "Giấy phép", 230, Stretch: true),
            new Col("use", "MuxStudio dùng để", 280, Stretch: true), new Col("url", "Trang chủ", 250, Name: true),
        ], tree: "Phần mềm / dịch vụ", treeWidth: 300, single: true)
        {
            OnDouble = (iid, _) => Open(iid),
            Tip = (iid, _) => iid.StartsWith('c') ? "Bấm đúp để mở trang chủ" : null,
        };
        var n = 0;
        foreach (var kind in new[] { AboutUi.Bundled, AboutUi.Tool, AboutUi.Service })
        {
            var kid = "k_" + kind;
            table.Add(kid, new Dictionary<string, string>(StringComparer.Ordinal), "head", text: AboutUi.KindTitles[kind]);
            foreach (var c in AboutUi.ByKind(kind))
            {
                table.Add("c" + n.ToString(CultureInfo.InvariantCulture), new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["owner"] = c.Owner, ["lic"] = c.License, ["use"] = c.Use, ["url"] = c.Url,
                }, text: c.Name, parent: kid);
                _urls.Add(c.Url);
                n++;
            }
        }
    }

    public FrameworkElement Root => this;

    private void Open(string iid)
    {
        if (iid.StartsWith('c') && int.TryParse(iid.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i < _urls.Count)
            Shell.Open(_urls[i]);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, AboutUi.AboutLines().Select(L.T)));
            _app.Log("ok", "Đã chép nội dung trang Giới thiệu vào bộ nhớ tạm.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // bo nho tam dang bi chuong trinh khac giu
        }
    }
}
