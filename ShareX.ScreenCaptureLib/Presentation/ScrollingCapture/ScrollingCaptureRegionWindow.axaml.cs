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

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ShareX.HelpersLib;
using System;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ShareX.ScreenCaptureLib;

public partial class ScrollingCaptureRegionWindow : Window
{
    private const int BorderPixels = 1;

    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private readonly PixelPoint _initialPosition;
    private double _windowScaling = 1;

    public ScrollingCaptureRegionWindow()
        : this(new DrawingRectangle(0, 0, 640, 420))
    {
    }

    public ScrollingCaptureRegionWindow(DrawingRectangle regionRectangle)
    {
        _frameWidth = regionRectangle.Width + BorderPixels * 2;
        _frameHeight = regionRectangle.Height + BorderPixels * 2;
        _initialPosition = new PixelPoint(
            regionRectangle.X - BorderPixels,
            regionRectangle.Y - BorderPixels);

        InitializeComponent();
        Position = _initialPosition;
        ConfigureGeometry(1);

        Opened += OnOpened;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ConfigureGeometry(GetScreenScaling());
        Helpers.EnsureWindowPosition(this, _initialPosition.X, _initialPosition.Y);
    }

    private void ConfigureGeometry(double scaling)
    {
        _windowScaling = Math.Max(0.5, scaling);
        Width = _frameWidth / _windowScaling;
        Height = _frameHeight / _windowScaling;
    }

    private double GetScreenScaling()
    {
        return Screens.ScreenFromPoint(_initialPosition)?.Scaling ?? Screens.Primary?.Scaling ?? 1;
    }
}
