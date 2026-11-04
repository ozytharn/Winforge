using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WinForge.Application.Setup;

namespace WinForge.App;

public sealed class FirstRunOnboardingWindow : Window
{
    public FirstRunOnboardingWindow(FirstRunExperienceService? service = null)
    {
        service ??= new FirstRunExperienceService();

        Title = "Welcome to WinForge";
        Width = 720;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;

        var root = new StackPanel
        {
            Margin = new Thickness(24),
            Orientation = Orientation.Vertical
        };

        var banner = new TextBlock
        {
            Text = "WinForge local system intelligence",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var intro = new TextBlock
        {
            Text = "WinForge inspects your local Windows environment without modifying it. It reports risks, health, software, storage, and security observations using read-only data collection.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };

        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        foreach (var step in service.GetChecklist())
        {
            var header = new TextBlock
            {
                Text = $"• {step.Title}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var description = new TextBlock
            {
                Text = step.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18, 0, 0, 2)
            };

            var detail = new TextBlock
            {
                Text = step.Detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(18, 0, 0, 10)
            };

            list.Children.Add(header);
            list.Children.Add(description);
            list.Children.Add(detail);
        }

        var dontShow = new CheckBox
        {
            Content = "Do not show this welcome screen again",
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 18)
        };

        var footer = new DockPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };

        var continueButton = new Button
        {
            Content = "Continue",
            Width = 140,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        continueButton.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        footer.Children.Add(continueButton);
        DockPanel.SetDock(continueButton, Dock.Right);

        root.Children.Add(banner);
        root.Children.Add(intro);
        root.Children.Add(list);
        root.Children.Add(dontShow);
        root.Children.Add(footer);

        Content = root;
        DoNotShowAgain = () => dontShow.IsChecked == true;
    }

    public Func<bool> DoNotShowAgain { get; }
}
