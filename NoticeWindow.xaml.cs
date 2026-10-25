using System.Windows;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// A message the Caster wants read, with what to do about it. Display only.
public partial class NoticeWindow : Window
{
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
}
