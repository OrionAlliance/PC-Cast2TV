using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCCast2TV.Helpers;

namespace PCCast2TV;

// REPORT A PROBLEM's popup, display only: the person picks bug or feedback, types, adds the log or a picture, and ReportSender sends it.
public partial class ReportWindow : Window
{
    const long BiggestPicture = 8_000_000; // Discord takes 10 MB a message, and the log can be 2 MB
    const string Promise = "Goes to the developer's Discord channel, only when you click SEND.";
    bool _bug, _log, _sending, _sent;
    string _picture;

    ReportWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        SendNote.Text = Promise;
        SetKind(true);
    }

    // A copy built without the report address opens GitHub's Issues page instead.
    public static void Open(Window owner)
    {
        if (ReportSender.Ready) new ReportWindow { Owner = owner }.ShowDialog();
        else WebPage.Open(AppUpdater.Page + "/issues", "the Issues page");
    }

    void Bug_Click(object sender, RoutedEventArgs e) => SetKind(true);

    void Feedback_Click(object sender, RoutedEventArgs e) => SetKind(false);

    string SendLabel => _bug ? "SEND BUG REPORT" : "SEND FEEDBACK";

    // A bug needs the log; feedback only offers it. The kind holds still while a report is on its way and after it's sent.
    void SetKind(bool bug)
    {
        if (_sending || _sent) return;
        _bug = bug;
        Paint(BugRing, BugPill, BugTick, bug);
        Paint(FeedbackRing, FeedbackPill, FeedbackTick, !bug);
        WhatTitle.Text = bug ? "WHAT HAPPENED?" : "WHAT'S ON YOUR MIND?";
        WhatHelp.Text = bug ? "What you clicked, what your TV or Roku did, and what you expected." : "An idea, something you like, or something you'd change.";
        LogButtonText.Text = bug ? "ADD DEBUG LOG" : "ADD DEBUG LOG (OPTIONAL)";
        LogButton.Width = bug ? 150 : 210;
        LogStar.Visibility = bug ? Visibility.Visible : Visibility.Collapsed;
        SendText.Text = SendLabel;
        Check();
    }

    // The kind in use is filled in the theme color with a ring and a tick, like Settings' pills.
    void Paint(Border ring, Button pill, UIElement tick, bool on)
    {
        var now = ThemeColors.Current;
        ring.BorderBrush = on ? new SolidColorBrush(now.Soft) : Brushes.Transparent;
        pill.Style = (Style)FindResource(on ? "Pill" : "OutlinePill");
        if (on) pill.Background = new SolidColorBrush(now.Main);
        else pill.ClearValue(BackgroundProperty);
        tick.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    void Typed(object sender, TextChangedEventArgs e)
    {
        EmailHint.Visibility = Email.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Check();
    }

    // SEND lights up once there's an email, some words, and for a bug the log.
    void Check()
    {
        string mail = Email.Text.Trim();
        SendButton.IsEnabled = !_sending && !_sent && mail.Contains('@') && mail.Contains('.') && What.Text.Trim().Length > 0 && (_log || !_bug);
        if (!SendButton.IsEnabled) SendText.SetResourceReference(TextBlock.ForegroundProperty, "Main"); // readable on the dimmed button: not ready yet, SENDING... and SENT
        else SendText.ClearValue(TextBlock.ForegroundProperty);
    }

    void AddLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Log.FilePath))
        {
            SendNote.Text = "There's no debug.log yet. Cast once, then add it.";
            return;
        }
        _log = true;
        LogAdded.Visibility = Visibility.Visible;
        SendNote.Text = Promise; // an earlier note no longer applies
        Check();
    }

    void RemoveLog_Click(object sender, RoutedEventArgs e)
    {
        _log = false;
        LogAdded.Visibility = Visibility.Collapsed;
        Check();
    }

    void AddPicture_Click(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFileDialog { Title = "Add a screenshot", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" };
        if (pick.ShowDialog(this) != true) return;
        if (new FileInfo(pick.FileName).Length > BiggestPicture)
        {
            SendNote.Text = "That picture is over 8 MB. Try a smaller one.";
            return;
        }
        _picture = pick.FileName;
        PictureName.Text = Path.GetFileName(_picture);
        PictureAdded.Visibility = Visibility.Visible;
        SendNote.Text = Promise;
    }

    void RemovePicture_Click(object sender, RoutedEventArgs e) => RemovePicture();

    void RemovePicture()
    {
        _picture = null;
        PictureAdded.Visibility = Visibility.Collapsed;
    }

    // Each way a send can go wrong gets its own words, so nobody checks their internet over a moved picture.
    async void Send_Click(object sender, RoutedEventArgs e)
    {
        _sending = true;
        Check();
        SendText.Text = "SENDING...";
        var outcome = await ReportSender.SendAsync(_bug, Email.Text.Trim(), What.Text.Trim(), _log, _picture);
        _sending = false;
        _sent = outcome == ReportSender.Outcome.Sent;
        if (outcome == ReportSender.Outcome.PictureUnreadable) RemovePicture();
        SendText.Text = _sent ? "SENT, THANK YOU!" : SendLabel;
        SendNote.Text = outcome switch
        {
            ReportSender.Outcome.Sent => "The developer has it now.",
            ReportSender.Outcome.PictureUnreadable => "Couldn't open the screenshot anymore. Add it again, or send without it.",
            ReportSender.Outcome.LogUnreadable => "Couldn't read debug.log just now. Try again in a moment.",
            ReportSender.Outcome.TooSlow => _picture != null ? "Sending took too long. Try a smaller screenshot, or send without one." : "Sending took too long. Try again in a moment.",
            ReportSender.Outcome.Busy => "The report service is busy. Try again in a minute.",
            ReportSender.Outcome.Refused => "The report service didn't take it. Please open an Issue on GitHub instead.",
            _ => "Couldn't reach the internet. Check the connection, then try again.",
        };
        Check();
    }
}
