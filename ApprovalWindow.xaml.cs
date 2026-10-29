using System.Windows;

namespace Thokolosi;

/// <summary>Non-blocking Yes/No prompt that can also be closed from code (when the phone answers first).</summary>
public partial class ApprovalWindow : Window
{
    bool _answered;
    public event Action<bool>? Answered;

    public ApprovalWindow(string title, string details, bool alsoOnPhone)
    {
        InitializeComponent();
        TitleText.Text = title;
        DetailsText.Text = details.Length > 4000 ? details[..4000] + "..." : details;
        if (alsoOnPhone) PhoneNote.Visibility = Visibility.Visible;
    }

    void Allow_Click(object sender, RoutedEventArgs e) => Answer(true);
    void Deny_Click(object sender, RoutedEventArgs e) => Answer(false);

    void Answer(bool allow)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(allow);
        Close();
    }

    /// <summary>Close because the answer came from elsewhere.</summary>
    public void Dismiss()
    {
        _answered = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_answered) { _answered = true; Answered?.Invoke(false); } // closing with X = deny
        base.OnClosed(e);
    }
}
