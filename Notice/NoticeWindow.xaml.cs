using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// A message to read (from the Caster or the window) with what to do about it; display only.
public partial class NoticeWindow : Window
{
    Func<Task<string>> _check;
    bool _passed;

    NoticeWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    // OK is the popup's cancel button too, so it closes it by itself (as Enter and Esc do).
    public static void Tell(Window owner, string title, string body)
    {
        var w = new NoticeWindow { Owner = owner };
        w.TitleText.Text = title;
        w.BodyText.Text = body;
        w.ShowDialog();
    }

    // The main button checks again: check gives null once it's fixed (true, and the popup closes), else the line to show; CLOSE, Esc or the X give up.
    public static bool Ask(Window owner, string title, string body, string action, Func<Task<string>> check)
    {
        var w = new NoticeWindow { Owner = owner, _check = check };
        w.TitleText.Text = title;
        w.BodyText.Text = body;
        w.MainLabel.Text = action;
        w.MainBtn.IsCancel = false;
        w.CloseBtn.IsCancel = true;
        w.CloseBtn.Visibility = Visibility.Visible;
        w.ShowDialog();
        return w._passed;
    }

    async void Main_Click(object sender, RoutedEventArgs e)
    {
        if (_check == null) return; // a plain OK closes by itself
        MainBtn.IsEnabled = false;
        string still = await _check();
        if (!IsVisible) return; // closed while checking
        MainBtn.IsEnabled = true;
        if (still == null)
        {
            _passed = true;
            Close();
            return;
        }
        ResultText.Text = still;
        ResultText.Visibility = Visibility.Visible;
    }
}
