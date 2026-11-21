using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyPcdEditor.App.Views;

/// <summary>
/// 只读的多行文本报告框（合并对齐报告用）：等宽字体 + 可选中复制。
/// </summary>
/// <remarks>
/// 与 <see cref="ParameterDialog"/> 同样的做法：一次性对话框直接用代码搭，不引 axaml。
/// 报告是对齐质量的唯一凭据（初值来源、修正量、内点率），必须能复制出去贴进巡检记录，
/// 所以用只读 TextBox 而不是 TextBlock。
/// </remarks>
public sealed class ReportDialog : Window
{
    public ReportDialog(string title, string report, string confirmText, string? cancelText = "放弃")
    {
        Title = title;
        Width = 560;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#15181D"));

        var panel = new DockPanel { Margin = new Thickness(16) };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var confirm = new Button { Content = confirmText, IsDefault = true };
        confirm.Click += (_, _) => Close(true);
        buttons.Children.Add(confirm);

        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }

        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        panel.Children.Add(new TextBox
        {
            Text = report,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
            Padding = new Thickness(8),
        });

        Content = panel;
    }
}
