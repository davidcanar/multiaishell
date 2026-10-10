using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiAI.Helpers;

namespace MultiAI.Controls;

/// <summary>
/// Modal dialogs for adding entries to the folder and agent dropdowns.
/// </summary>
public static class AddEntryDialogs
{
    private static readonly Brush FieldBackground = new SolidColorBrush(Color.FromRgb(0x3d, 0x3d, 0x3d));
    private static readonly Brush FieldForeground = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0));
    private static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush LabelForeground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa));

    private static TextBox MakeTextBox(string automationId)
    {
        var box = new TextBox
        {
            Background = FieldBackground,
            Foreground = FieldForeground,
            BorderBrush = FieldBorder,
            Padding = new Thickness(6, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    private static TextBlock MakeLabel(string text) => new TextBlock
    {
        Text = text,
        Foreground = LabelForeground,
        Margin = new Thickness(0, 10, 0, 4),
        FontSize = 12
    };

    private static Button MakeButton(string content, string automationId, Brush background)
    {
        var button = new Button
        {
            Content = content,
            Background = background,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 6, 10, 6),
            MinWidth = 84,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static Window MakeWindow(Window owner, string title) => new Window
    {
        Title = title,
        Owner = owner,
        Width = 520,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e))
    };

    /// <summary>
    /// Asks for a label + folder pair. Returns null when cancelled.
    /// </summary>
    public static SavedPathEntry? PromptForFolder(Window owner, string suggestedLabel)
    {
        var dlg = MakeWindow(owner, "Add saved folder");
        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(MakeLabel("Label"));
        var labelBox = MakeTextBox("LabelTextBox");
        labelBox.Text = suggestedLabel;
        panel.Children.Add(labelBox);

        panel.Children.Add(MakeLabel("Folder"));
        var folderBox = MakeTextBox("FolderTextBox");
        var browseButton = MakeButton("Browse…", "BrowseButton", FieldBackground);
        var folderRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        folderRow.Children.Add(browseButton);
        DockPanel.SetDock(browseButton, Dock.Right);
        folderRow.Children.Add(folderBox);
        panel.Children.Add(folderRow);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var okButton = MakeButton("OK", "OkButton", new SolidColorBrush(Color.FromRgb(0x2d, 0x5d, 0x8d)));
        var cancelButton = MakeButton("Cancel", "CancelButton", FieldBackground);
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        panel.Children.Add(buttonRow);

        dlg.Content = panel;

        bool confirmed = false;
        browseButton.Click += (_, _) =>
        {
            using var browser = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the folder to save",
                ShowNewFolderButton = true
            };
            if (browser.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                folderBox.Text = browser.SelectedPath;
        };
        okButton.Click += (_, _) =>
        {
            string folder = folderBox.Text.Trim();
            if (folder.Length == 0)
            {
                MessageBox.Show(dlg, "Please enter or browse a folder.", "Add saved folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!Directory.Exists(folder))
            {
                var create = MessageBox.Show(dlg,
                    $"The folder does not exist:\n{folder}\n\nCreate it now?",
                    "Add saved folder", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (create != MessageBoxResult.Yes) return;
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex)
                {
                    MessageBox.Show(dlg, $"Could not create the folder:\n{ex.Message}",
                        "Add saved folder", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            confirmed = true;
            dlg.Close();
        };
        cancelButton.Click += (_, _) => dlg.Close();

        dlg.ShowDialog();
        if (!confirmed) return null;

        string label = labelBox.Text.Trim();
        string folder2 = folderBox.Text.Trim();
        if (label.Length == 0)
        {
            label = Path.GetFileName(folder2.TrimEnd('\\', '/'));
            if (label.Length == 0) label = folder2;
        }
        return new SavedPathEntry { Name = label, Path = folder2 };
    }

    /// <summary>
    /// Asks for a new agent command name. Returns null when cancelled.
    /// </summary>
    public static string? PromptForAgent(Window owner)
    {
        var dlg = MakeWindow(owner, "Add agent");
        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(MakeLabel("Agent name (a command found on PATH)"));
        var nameBox = MakeTextBox("AgentTextBox");
        panel.Children.Add(nameBox);

        panel.Children.Add(new TextBlock
        {
            Text = "Letters, digits and . _ - : / \\ only — no spaces or shell characters.",
            Foreground = LabelForeground,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var okButton = MakeButton("OK", "OkButton", new SolidColorBrush(Color.FromRgb(0x2d, 0x5d, 0x8d)));
        var cancelButton = MakeButton("Cancel", "CancelButton", FieldBackground);
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        panel.Children.Add(buttonRow);

        dlg.Content = panel;

        bool confirmed = false;
        okButton.Click += (_, _) =>
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(dlg, "Please enter an agent name.", "Add agent",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!AgentManager.IsValidAgentName(name))
            {
                MessageBox.Show(dlg,
                    "Only letters, digits and . _ - : / \\ are allowed —\nspaces and shell characters are rejected for safety.",
                    "Add agent", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            confirmed = true;
            dlg.Close();
        };
        cancelButton.Click += (_, _) => dlg.Close();

        dlg.ShowDialog();
        return confirmed ? nameBox.Text.Trim() : null;
    }
}
