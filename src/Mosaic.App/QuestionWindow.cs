using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mosaic.App;

internal enum UserChoice { Cancel, Save, Discard, Apply }

// App-owned choices follow the selected language, independently of Windows' native dialog language.
internal sealed class QuestionWindow : Window
{
    internal UserChoice Choice { get; private set; } = UserChoice.Cancel;
    internal Button PrimaryButton { get; }
    internal Button? DiscardButton { get; }

    internal QuestionWindow(Window owner, string message, bool saving)
    {
        Owner = owner; Title = Lang.T("AppName");
        Style = (Style)Application.Current.Resources[typeof(Window)];
        Width = 600; Height = 260; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        var grid = new Grid { Margin = new Thickness(28) };
        grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = message, LineHeight = 26, VerticalAlignment = VerticalAlignment.Center });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        Grid.SetRow(actions, 1); grid.Children.Add(actions); Content = grid;
        Button Add(string label, UserChoice choice, bool primary = false)
        {
            var button = new Button { Content = Lang.T(label), Margin = new Thickness(6, 0, 0, 0), IsDefault = primary };
            if (primary) button.Style = (Style)Application.Current.Resources["Primary"];
            button.Click += (_, _) => { Choice = choice; Close(); };
            actions.Children.Add(button); return button;
        }
        PrimaryButton = Add(saving ? "Save" : "ApplyTrim", saving ? UserChoice.Save : UserChoice.Apply, true);
        if (saving) DiscardButton = Add("Discard", UserChoice.Discard);
        Add("Cancel", UserChoice.Cancel);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Choice = UserChoice.Cancel; Close(); e.Handled = true; } };
        Loaded += (_, _) => App.FitOnScreen(this);
    }

    internal static UserChoice Ask(Window owner, string message, bool saving)
    {
        var dialog = new QuestionWindow(owner, message, saving);
        dialog.ShowDialog(); return dialog.Choice;
    }
}
