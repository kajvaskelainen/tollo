using System.Windows;
using System.Windows.Threading;
using Kanava.Services;

namespace Kanava.Views;

/// <summary>Muistutusilmoitus näytön oikeassa yläkulmassa (aina päällimmäinen, ei vie fokusta).</summary>
public partial class ReminderToast : Window
{
    private static readonly List<ReminderToast> Open = [];
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromMinutes(2) };

    public event Action? WatchRequested;

    public ReminderToast(Reminder r)
    {
        InitializeComponent();
        var minutes = (int)Math.Round((r.Start - DateTime.Now).TotalMinutes);
        Heading.Text = minutes > 0 ? $"Alkaa {minutes} min kuluttua · {r.Start:HH.mm}" : $"Alkoi {r.Start:HH.mm}";
        ProgramTitle.Text = r.Title;
        ChannelText.Text = r.ChannelName;
        _autoClose.Tick += (_, _) => Close();
        Loaded += (_, _) =>
        {
            // Pinotaan oikeaan yläkulmaan
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 16;
            Top = wa.Top + 16 + Open.Sum(t => t.ActualHeight + 10);
            Open.Add(this);
            _autoClose.Start();
            System.Media.SystemSounds.Asterisk.Play();
        };
    }

    private void OnWatch(object sender, RoutedEventArgs e)
    {
        WatchRequested?.Invoke();
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _autoClose.Stop();
        Open.Remove(this);
        base.OnClosed(e);
    }
}
