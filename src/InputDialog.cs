using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GeminiLauncher
{
    /// <summary>Small Fluent text prompt.</summary>
    public static class InputDialog
    {
        public static string Ask(Window owner, string title, string label, string initial)
        {
            var w = new Window
            {
                Title = title, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false, Topmost = true,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13.5
            };
            w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
            w.SetResourceReference(Window.ForegroundProperty, "Fg1");
            w.SourceInitialized += (s, e) => Theme.ApplyBackdrop(w, true, 2);

            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
            var tb = new TextBox { Text = initial, Height = 32, Style = (Style)Application.Current.FindResource("FluentTextBox") };
            sp.Children.Add(tb);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true, Style = (Style)Application.Current.FindResource("FluentButton") };
            var ok = new Button { Content = "Pin", Width = 90, IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton") };
            ok.Click += (s, e) => { w.DialogResult = true; };
            buttons.Children.Add(cancel); buttons.Children.Add(ok);
            sp.Children.Add(buttons);
            w.Content = sp;
            w.Loaded += (s, e) => { tb.Focus(); tb.CaretIndex = tb.Text.Length; Keyboard.Focus(tb); };
            return w.ShowDialog() == true ? tb.Text : null;
        }
    }
}
