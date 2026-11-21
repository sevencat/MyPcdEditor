using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyPcdEditor.App.Views;

/// <summary>
/// 就地构造的参数输入框（几个数值字段 + 执行/取消）。
/// </summary>
/// <remarks>
/// 不引 MVVM 也不做独立 axaml：优化操作的参数就两三个数字，这类一次性对话框用代码搭更省事，
/// 也不会把参数状态带进主窗口。
/// </remarks>
public sealed class ParameterDialog : Window
{
    private readonly List<TextBox> _boxes = [];
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.Parse("#FF8080")), IsVisible = false };

    public ParameterDialog(string title, string description, params (string Label, string Value, string Hint)[] fields)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#15181D"));

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });

        foreach ((string label, string value, string hint) in fields)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = label });

            var box = new TextBox { Text = value };
            row.Children.Add(box);
            _boxes.Add(box);

            if (!string.IsNullOrEmpty(hint))
            {
                row.Children.Add(new TextBlock { Text = hint, FontSize = 11, Opacity = 0.55 });
            }

            panel.Children.Add(row);
        }

        panel.Children.Add(_error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var confirm = new Button { Content = "执行" };
        var cancel = new Button { Content = "取消", IsCancel = true };
        confirm.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => Close(null);

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);

        Content = panel;
    }

    /// <summary>用户确认后的各字段文本；取消时为 null。</summary>
    public string[]? Values { get; private set; }

    /// <summary>输入校验失败时在对话框里显示红字，不关闭。</summary>
    public void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    private void Confirm()
    {
        Values = [.. _boxes.Select(box => box.Text ?? string.Empty)];
        Close(Values);
    }
}
