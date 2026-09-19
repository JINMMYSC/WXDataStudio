using System.Windows;
using System.Windows.Controls;
using WXDataStudio.App.Models;

namespace WXDataStudio.App;

public sealed record MoneyMessageInput(MessageKind Kind, string Amount, string Note, string Status);

/// <summary>
/// Small form for transfer / red-packet / payment records: the user types an
/// amount and a note instead of editing protocol markup.
/// </summary>
public static class MoneyMessageDialog
{
    public static MoneyMessageInput? Show(
        Window owner, MessageKind kind, string amount, string note, string status)
    {
        var window = new Window
        {
            Title = "转账 / 红包",
            Owner = owner,
            Width = 430,
            Height = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };

        var panel = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = "这里改的是聊天里显示的文字。转账/红包点进去的“详情页”由微信服务器提供，" +
                   "本地改不了，可能提示失败或仍显示原来的金额。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DarkGoldenrod,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var kindBox = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        kindBox.Items.Add("转账");
        kindBox.Items.Add("红包");
        kindBox.Items.Add("收付款");
        kindBox.SelectedIndex = kind switch
        {
            MessageKind.RedPacket => 1,
            MessageKind.Payment => 2,
            _ => 0
        };
        panel.Children.Add(new TextBlock { Text = "类型" });
        panel.Children.Add(kindBox);

        var amountBox = new TextBox { Text = string.IsNullOrWhiteSpace(amount) ? "¥100.00" : amount, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(new TextBlock { Text = "金额（可以带 ¥ 或文字，例如 ¥88.88）" });
        panel.Children.Add(amountBox);

        var noteBox = new TextBox { Text = note, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(new TextBlock { Text = "说明 / 备注" });
        panel.Children.Add(noteBox);

        var statusBox = new TextBox { Text = status, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = "聊天里显示的状态文字（可留空。例如 已收款 / 已退还 / 待确认收款）"
        });
        panel.Children.Add(statusBox);

        MoneyMessageInput? result = null;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var ok = new Button { Content = "确定", Width = 88, Padding = new Thickness(0, 6, 0, 6), IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) =>
        {
            var selected = kindBox.SelectedIndex switch
            {
                1 => MessageKind.RedPacket,
                2 => MessageKind.Payment,
                _ => MessageKind.Transfer
            };
            result = new MoneyMessageInput(selected, amountBox.Text.Trim(), noteBox.Text.Trim(), statusBox.Text.Trim());
            window.DialogResult = true;
        };
        cancel.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;
        return window.ShowDialog() == true ? result : null;
    }
}
