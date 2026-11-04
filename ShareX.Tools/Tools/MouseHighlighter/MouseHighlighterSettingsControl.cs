#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ShareX.Tools.Localization;
using DrawingColor = System.Drawing.Color;

namespace ShareX.Tools;

public sealed class MouseHighlighterSettingsControl : UserControl
{
    public MouseHighlighterSettingsControl(MouseHighlighterOptions options, Action? settingsChanged = null)
    {
        options.Validate();
        void Changed()
        {
            options.Validate();
            MouseHighlighterManager.RefreshOptions(options);
            settingsChanged?.Invoke();
        }

        StackPanel panel = new() { Spacing = 12 };
        panel.Children.Add(ColorRow(Strings.MouseHighlighter_AlwaysColor, options.AlwaysColor,
            color => { options.AlwaysColor = color; Changed(); }));
        panel.Children.Add(NumberRow(Strings.MouseHighlighter_Radius, options.Radius, 5, 500, 1,
            value => { options.Radius = (int)value; Changed(); }));
        panel.Children.Add(Check(Strings.MouseHighlighter_AutoActivate, options.AutoActivate,
            value => { options.AutoActivate = value; Changed(); }));
        Content = panel;
    }

    private static Control NumberRow(string label, decimal value, decimal min, decimal max, decimal increment, Action<decimal> changed)
    {
        NumericUpDown number = new() { Minimum = min, Maximum = max, Increment = increment, Value = value, FormatString = "0" };
        number.ValueChanged += (_, _) => { if (number.Value.HasValue) changed(number.Value.Value); };
        return Row(label, number);
    }

    private static Control ColorRow(string label, DrawingColor color, Action<DrawingColor> changed)
    {
        ColorView picker = new() { Color = Color.FromArgb(color.A, color.R, color.G, color.B), IsAlphaEnabled = true, IsAlphaVisible = true };
        Border swatch = new() { Width = 28, Height = 20, Background = new SolidColorBrush(picker.Color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        Button button = new() { Content = swatch, HorizontalAlignment = HorizontalAlignment.Left, Flyout = new Flyout { Content = picker } };
        picker.ColorChanged += (_, _) =>
        {
            Color selected = picker.Color;
            swatch.Background = new SolidColorBrush(selected);
            changed(DrawingColor.FromArgb(selected.A, selected.R, selected.G, selected.B));
        };
        return Row(label, button);
    }

    private static CheckBox Check(string label, bool value, Action<bool> changed)
    {
        CheckBox check = new() { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Normal }, IsChecked = value };
        check.IsCheckedChanged += (_, _) => changed(check.IsChecked == true);
        return check;
    }

    private static Control Row(string label, Control editor)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,180"), ColumnSpacing = 12 };
        row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Normal });
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);
        return row;
    }
}
