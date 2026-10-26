using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VocalTrainer.Infrastructure;
using VocalTrainer.Services;
using VocalTrainer.ViewModels;

namespace VocalTrainer;

/// <summary>WPF composition root and keyboard adapter. Actions remain in the ViewModels.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new AudioService(), new DesktopDialogService());
        DataContext = _viewModel;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None or Key.LWin or Key.RWin ||
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            return;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) return;
        ModifierKeys mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        bool alt = mods.HasFlag(ModifierKeys.Alt);

        if (_viewModel.Hotkeys.IsCapturing)
        {
            e.Handled = true;
            if (key == Key.Escape) { _viewModel.Hotkeys.CancelCapture(); return; }
            if (key == Key.Back) { _viewModel.Hotkeys.Assign(new ShortcutChord("")); return; }
            _viewModel.Hotkeys.Assign(new ShortcutChord(key.ToString(), ctrl, shift, alt));
            return;
        }

        // Don't hijack letters, Space or Enter during normal text entry or combo-box selection.
        if (!ctrl && !alt && key is not (Key.F1 or Key.Escape) &&
            IsTextEditing(Keyboard.FocusedElement as DependencyObject)) return;

        string scope = _viewModel.SelectedTabIndex switch
        {
            0 => "trainer", 1 => "exercises", 2 => "ear", 3 => "range", 4 => "editor", 5 => "midi", _ => "settings"
        };
        var pressed = new ShortcutChord(key.ToString(), ctrl, shift, alt);
        var action = ShortcutCatalog.Resolve(_viewModel.Hotkeys.Bindings, pressed, scope);
        if (action is null) return;
        e.Handled = true;
        _viewModel.ActivateShortcut(action);
    }

    private static bool IsTextEditing(DependencyObject? focused)
    {
        for (var node = focused; node is not null;)
        {
            if (node is TextBoxBase or PasswordBox) return true;
            if (node is ComboBox) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void MelodyPause_Click(object sender, RoutedEventArgs e) => _viewModel.Melody.TogglePlaybackPause();
    private void MelodyRepeat_Click(object sender, RoutedEventArgs e) => _viewModel.Melody.Repeat();
    private void Window_Closing(object? sender, CancelEventArgs e) => _viewModel.Dispose();
}
