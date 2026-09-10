using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using GamepadToolkit.Core.Setup;

namespace GamepadToolkit.App;

/// <summary>
/// First-run prerequisites dialog. The portable build has no installer to run before it,
/// so the app itself has to notice a missing driver and offer to fetch it.
/// </summary>
public partial class DriverSetupWindow : Window
{
    /// <summary>winget's APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE — the package is already there.</summary>
    private const int WingetNoApplicableUpgrade = -1978335189;

    private bool _installing;

    public DriverSetupWindow()
    {
        InitializeComponent();
        Render();
    }

    /// <summary>True once nothing is missing, so the caller knows whether to re-read status.</summary>
    public bool AllPresent { get; private set; }

    /// <summary>Set when a driver that only attaches at boot was installed during this session.</summary>
    public bool RebootRecommended { get; private set; }

    /// <summary>
    /// Shows the dialog only when something is actually missing. Returns true if the state
    /// changed and the caller should refresh its own driver status.
    /// </summary>
    public static bool PromptIfNeeded(Window? owner = null)
    {
        if (DriverBootstrap.Missing().Count == 0)
            return false;

        var window = new DriverSetupWindow();
        if (owner is not null && owner.IsVisible)
            window.Owner = owner;

        window.ShowDialog();
        return true;
    }

    private void Render()
    {
        DriverList.Children.Clear();

        var missing = DriverBootstrap.Missing();
        AllPresent = missing.Count == 0;

        foreach (var driver in DriverBootstrap.All)
        {
            var present = !missing.Contains(driver);
            DriverList.Children.Add(BuildCard(driver, present));
        }

        InstallButton.IsEnabled = !_installing && missing.Count > 0;
        RecheckButton.IsEnabled = !_installing;
        SkipButton.Content = AllPresent ? "Close" : "Continue without them";

        if (_installing)
            return;

        if (AllPresent)
        {
            StatusText.Text = RebootRecommended
                ? "All drivers are installed. Restart Windows before remapping — HidHide only starts filtering devices whose stacks are built after it loads."
                : "All drivers are installed.";
            StatusText.Foreground = (Brush)FindResource(RebootRecommended ? "Warn" : "Good");
        }
        else if (DriverBootstrap.FindWinget() is null)
        {
            StatusText.Text =
                "winget was not found on this machine, so the drivers cannot be installed automatically. " +
                "Use the download links above, then press Re-check.";
            StatusText.Foreground = (Brush)FindResource("Warn");
            InstallButton.IsEnabled = false;
        }
        else
        {
            StatusText.Text = "Installing runs winget and each driver's own installer, so Windows will ask for administrator permission.";
            StatusText.Foreground = (Brush)FindResource("Muted");
        }
    }

    private Border BuildCard(DriverRequirement driver, bool present)
    {
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        heading.Inlines.Add(new Run(driver.DisplayName));
        heading.Inlines.Add(new Run(present ? "   installed" : "   missing")
        {
            Foreground = (Brush)FindResource(present ? "Good" : "Warn"),
            FontWeight = FontWeights.Normal
        });

        var panel = new StackPanel();
        panel.Children.Add(heading);
        panel.Children.Add(new TextBlock
        {
            Text = driver.Purpose,
            Style = (Style)FindResource("Label"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });

        if (!present && driver.NeedsRebootAfterInstall)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Needs a Windows restart after installing before it can hide anything.",
                Style = (Style)FindResource("Label"),
                Foreground = (Brush)FindResource("Warn"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        var link = new Hyperlink(new Run(driver.DownloadUrl))
        {
            NavigateUri = new Uri(driver.DownloadUrl),
            Foreground = (Brush)FindResource("Accent")
        };
        link.RequestNavigate += OnNavigate;

        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Label"),
            TextWrapping = TextWrapping.Wrap
        }.With(t => t.Inlines.Add(link)));

        return new Border { Style = (Style)FindResource("Card"), Child = panel };
    }

    private static void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var missing = DriverBootstrap.Missing();
        if (missing.Count == 0 || _installing)
            return;

        _installing = true;
        InstallButton.IsEnabled = false;
        RecheckButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        LogCard.Visibility = Visibility.Visible;
        LogBox.Clear();

        var progress = new Progress<string>(Append);
        var failed = 0;

        foreach (var driver in missing)
        {
            StatusText.Text = $"Installing {driver.DisplayName}…";
            StatusText.Foreground = (Brush)FindResource("Muted");
            Append($"--- {driver.DisplayName} ({driver.WingetId}) ---");

            var result = await DriverBootstrap.InstallAsync(driver, progress);

            if (DriverBootstrap.IsInstalled(driver.Kind))
            {
                Append($"{driver.DisplayName} installed.");
                if (driver.NeedsRebootAfterInstall)
                    RebootRecommended = true;
            }
            else if (result.ExitCode == WingetNoApplicableUpgrade)
            {
                // The package is on disk but its device is not answering, which is what a
                // pending reboot looks like.
                Append($"{driver.DisplayName} is already installed but is not responding yet — restart Windows and check again.");
                RebootRecommended = true;
                failed++;
            }
            else
            {
                Append($"{driver.DisplayName} was not installed (winget exit code {result.ExitCode}). " +
                       "Declining the installer's administrator prompt is the usual cause, and Install can simply be pressed again.");
                failed++;
            }

            Append(string.Empty);
        }

        _installing = false;
        SkipButton.IsEnabled = true;
        Render();

        // Render() writes the neutral pre-install hint, which reads as if nothing happened.
        if (failed > 0)
        {
            StatusText.Text = failed == 1
                ? "One driver could not be installed. The installer output above says why."
                : $"{failed} drivers could not be installed. The installer output above says why.";
            StatusText.Foreground = (Brush)FindResource("Warn");
        }
    }

    private void Append(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void OnRecheckClick(object sender, RoutedEventArgs e) => Render();

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();
}

internal static class FluentExtensions
{
    /// <summary>Lets a control be configured inline while still being used as an expression.</summary>
    public static T With<T>(this T item, Action<T> configure)
    {
        configure(item);
        return item;
    }
}
