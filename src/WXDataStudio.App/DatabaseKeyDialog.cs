using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WXDataStudio.App;

public sealed record DatabaseKeyInput(string Value, bool IsRawHex);

public static class DatabaseKeyDialog
{
    public static DatabaseKeyInput? Show(Window owner, bool rawHexDefault)
    {
        var window = new Window
        {
            Title = "数据库密钥（仅本次会话）",
            Owner = owner,
            Width = 460,
            Height = 270,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "密钥仅保存在当前进程内存中，不写入日志、仓库或快照。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var kind = new ComboBox
        {
            SelectedIndex = rawHexDefault ? 1 : 0,
            Margin = new Thickness(0, 0, 0, 10)
        };
        kind.Items.Add("文本口令 / passphrase");
        kind.Items.Add("Raw Hex 密钥");
        panel.Children.Add(kind);

        var keyBox = new PasswordBox
        {
            Height = 30,
            Margin = new Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(keyBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Raw Hex 模式只接受十六进制字符。留空确认可清除本次会话密钥。",
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        DatabaseKeyInput? result = null;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var ok = new Button
        {
            Content = "确认",
            Width = 90,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 90,
            Height = 30,
            IsCancel = true
        };
        ok.Click += (_, _) =>
        {
            result = new DatabaseKeyInput(keyBox.Password.Trim(), kind.SelectedIndex == 1);
            window.DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        return window.ShowDialog() == true ? result : null;
    }
}
