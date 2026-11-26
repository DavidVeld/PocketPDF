using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PurpleStreamPDF.Core;

namespace PurpleStreamPDF;

/// <summary>Tool templates: a tool and its style under a name, one button each on the tool bar. Markups drawn with one carry its name as their subject.</summary>
public partial class MainWindow
{
    /// <summary>The style pickers a template remembers, by name.</summary>
    static readonly string[] TemplatePickers =
        ["ColorPicker", "LineEndPicker", "LineWidthPicker", "FontSizePicker", "ShapePicker", "LineStylePicker", "FillPicker", "HatchPicker"];

    void ShowTemplateButtons()
    {
        TemplateBar.Children.Clear();
        foreach (var template in _settings.Templates)
        {
            var remove = new MenuItem { Header = $"Remove “{template.Name}”" };
            remove.Click += (_, _) =>
            {
                _settings.Templates.Remove(template);
                _settings.Save();
                ShowTemplateButtons();
            };
            var button = new Button
            {
                Tag = template, Margin = new Thickness(2, 0, 2, 0), ContextMenu = new ContextMenu { Items = { remove } },
                Content = template.Styles.TryGetValue("ColorPicker", out string? color) ? ColorSwatch(color, template.Name) : template.Name,
                ToolTip = $"{template.Name}: the {template.Tool.Replace("Tool", "").ToLowerInvariant()} tool in its saved style. "
                    + $"Its markups are marked “{template.Name}” in the file. Right-click to remove",
            };
            button.Click += OnTemplate;
            if (FindName(template.Tool) is RadioButton tool) button.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = tool }); // off where the tool is
            TemplateBar.Children.Add(button);
        }
    }

    void OnTemplate(object sender, RoutedEventArgs e)
    {
        var template = (ToolTemplate)((FrameworkElement)sender).Tag;
        if (FindName(template.Tool) is not RadioButton tool) return; // a tool since renamed
        if (tool.IsChecked == true) OnToolChecked(tool, e); // picked again: starts over
        else tool.IsChecked = true;
        // After the tool: picking one sets its own starting colour or fill, which the template overrides.
        foreach (var (name, tag) in template.Styles)
        {
            if (FindName(name) is not ComboBox picker) continue;
            if (picker == ColorPicker) picker.SelectedItem = ColorItem(ColorPicker, CustomColorItem, tag);
            else if (picker == FillPicker && tag != "None") picker.SelectedItem = ColorItem(FillPicker, CustomFillItem, tag);
            else SelectByTag(picker, item => item == tag);
        }
        MarkupLayer.CurrentSubject = template.Name;
    }

    void OnSaveTemplate(object sender, RoutedEventArgs e)
    {
        if (MarkupLayer.Tool is MarkupTool.Select or MarkupTool.SelectText)
        {
            MessageBox.Show(this, "Pick a drawing tool and its style first, then save it as a template.", "Save template");
            return;
        }
        if (AskForName("Save template", "Name, also what its markups mean, for example “Delete”:", MarkupLayer.CurrentSubject ?? "") is not { } name) return;
        var template = new ToolTemplate { Name = name, Tool = $"{MarkupLayer.Tool}Tool" };
        foreach (string pickerName in TemplatePickers)
            if (((ComboBox)FindName(pickerName)).SelectedItem is FrameworkElement { Tag: string tag }) template.Styles[pickerName] = tag;
        int existing = _settings.Templates.FindIndex(other => string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _settings.Templates[existing] = template; // same name: replaces it
        else _settings.Templates.Add(template);
        _settings.Save();
        ShowTemplateButtons();
        MarkupLayer.CurrentSubject = name;
    }

    /// <summary>Asks for a short name. Null if cancelled or left empty.</summary>
    string? AskForName(string title, string prompt, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 260 };
        var ok = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), Style = TryFindResource("AccentButtonStyle") as Style };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        var dialog = new Window
        {
            Title = title, Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true && box.Text.Trim() is { Length: > 0 } name ? name : null;
    }
}
