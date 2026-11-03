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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ShareX.AvaloniaUI.Input;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Core.Annotations;
using ShareX.ImageEditor.Presentation.Controls;
using ShareX.ImageEditor.Presentation.ViewModels;
using ShareX.ImageEditor.Presentation.Views;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AvaloniaCanvas = Avalonia.Controls.Canvas;
using DrawingColor = System.Drawing.Color;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ShareX.ScreenCaptureLib.Presentation.RegionCapture;

public partial class RegionCaptureWindow : Window
{
    private readonly RegionCaptureSession? _session;
    private readonly SKBitmap? _screenshot;
    private readonly DrawingRectangle _imageRectangle;
    private readonly DrawingRectangle _x11Rectangle;
    private readonly bool _takesInitialFocus;
    private AvaloniaRegionCaptureRequest? _request;
    private EditorView _editorWorkspace = null!;
    private MainViewModel? _viewModel;
    private Grid _regionInputSurface = null!;
    private RegionSelectionOverlay _regionOverlay = null!;
    private AvaloniaCanvas _regionResizeNodeCanvas = null!;
    private LayoutTransformControl _regionTransform = null!;
    private Grid _captureToolbar = null!;
    private AnnotationToolbar _annotationToolbar = null!;
    private Button _regionToolButton = null!;
    private StackPanel _magnifierPanel = null!;
    private Grid _magnifierView = null!;
    private Image _magnifierImage = null!;
    private MagnifierPixelGrid _magnifierPixelGrid = null!;
    private Border _pointerInfoPanel = null!;
    private TextBlock _pointerInfoText = null!;
    private Border _selectionInfoPanel = null!;
    private TextBlock _selectionInfoText = null!;
    private WriteableBitmap? _magnifierBitmap;
    private readonly Dictionary<SelectionResizeNodeKind, Border> _regionResizeNodes = [];
    private RegionInteraction _interaction;
    private SelectionResizeNodeKind? _resizeNode;
    private Point _pressPoint;
    private Point _lastCreationPoint;
    private Rect _interactionStartRectangle;
    private readonly TranslateTransform _magnifierTransform = new();
    private double _positionedCaptureToolbarWidth = double.NaN;
    private bool _regionToolActive = true;
    private bool _keyboardInputEnabled;
    private bool _isMovingSelectionDuringCreation;
    private bool _wasControlHeldDuringCreation;
    private bool _suppressNextRightButtonReleaseAction;
    private bool _annotationMiddleButtonPressed;
    private bool _annotationRightButtonPressed;
    private bool _workspaceOwnsScreenshot;
    private bool _closing;

    public RegionCaptureWindow()
    {
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        AvaloniaXamlLoader.Load(this);
        Topmost = true;
        ResolveControls();
        AddHandler(PointerPressedEvent, OnCaptureHostPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnCaptureHostPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnCaptureHostPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <param name="session">Selection state shared with the windows on the other monitors.</param>
    /// <param name="screenshot">This monitor's crop of the frozen screenshot. The window takes ownership.</param>
    /// <param name="imageRectangle">Where the crop is in the frozen screenshot.</param>
    /// <param name="x11Rectangle">Where the monitor is on the X11 desktop.</param>
    /// <param name="takesInitialFocus">True for the window on the monitor with the cursor.</param>
    internal RegionCaptureWindow(RegionCaptureSession session, SKBitmap screenshot, DrawingRectangle imageRectangle,
        DrawingRectangle x11Rectangle, bool takesInitialFocus) : this()
    {
        _session = session;
        _request = session.Request;
        _screenshot = screenshot;
        _imageRectangle = imageRectangle;
        _x11Rectangle = x11Rectangle;
        _takesInitialFocus = takesInitialFocus;
        InitializeCaptureWorkspace();
        Opened += OnOpened;
        SizeChanged += (_, _) =>
        {
            _positionedCaptureToolbarWidth = double.NaN;
            PositionCaptureToolbar();
        };
    }

    internal DrawingRectangle ImageRectangle => _imageRectangle;

    internal bool IsDirty => _viewModel?.IsDirty == true;

    internal void ShowOnMonitor()
    {
        // Set while the window still has its small initial size, so it is mapped on the right monitor.
        Position = new PixelPoint(_x11Rectangle.X, _x11Rectangle.Y);
        ApplyPixelSize();
        Show();
    }

    internal void CloseForSession()
    {
        _closing = true;
        Close();
    }

    internal SKColor GetPixel(int imageX, int imageY)
    {
        return _editorWorkspace.GetWorkspacePixel(imageX - _imageRectangle.X, imageY - _imageRectangle.Y);
    }

    /// <summary>Renders the part of an image rectangle on this monitor, annotations included.</summary>
    internal SKBitmap? GetSnapshot(DrawingRectangle imageRectangle)
    {
        return _editorWorkspace.GetSnapshot(new Rect(
            imageRectangle.X - _imageRectangle.X,
            imageRectangle.Y - _imageRectangle.Y,
            imageRectangle.Width,
            imageRectangle.Height));
    }

    protected override void OnClosed(EventArgs e)
    {
        Exception? closeException = null;

        try
        {
            _magnifierBitmap?.Dispose();
            _magnifierBitmap = null;
            _captureToolbar.LayoutUpdated -= OnCaptureToolbarLayoutUpdated;
            _editorWorkspace.DetachHostAnnotationToolbar(_annotationToolbar);

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.ToolSelectionRequested -= OnAnnotationToolSelected;
            }

            if (_workspaceOwnsScreenshot)
            {
                _editorWorkspace.DisposeWorkspace();
            }
            else
            {
                _screenshot?.Dispose();
            }

            _viewModel?.Dispose();
        }
        catch (Exception ex)
        {
            closeException ??= ex;
        }
        finally
        {
            _session?.OnWindowClosed(this, closeException);
            base.OnClosed(e);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_keyboardInputEnabled && !_closing && _request != null &&
            e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _viewModel?.IsModalOpen != true)
        {
            ActivateRegionTool();
            e.Handled = true;
            return;
        }

        if (_keyboardInputEnabled && !_closing && e.Key == Key.H && e.KeyModifiers == KeyModifiers.Control)
        {
            _captureToolbar.Classes.Set("hidden", !_captureToolbar.Classes.Contains("hidden"));
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);

        if (!_keyboardInputEnabled || _closing || _request == null)
        {
            return;
        }

        if (_regionToolActive && _interaction == RegionInteraction.Creating &&
            (e.Key is Key.LeftShift or Key.RightShift))
        {
            UpdateSelectionDuringCreation(_lastCreationPoint, e.KeyModifiers | KeyModifiers.Shift);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && HasValidSelection() && !e.Handled &&
            _viewModel?.IsModalOpen != true && e.Source is not TextBox &&
            FocusManager?.GetFocusedElement() is not TextBox)
        {
            e.Handled = true;
            Complete(_session!.Selection);
            return;
        }

        if (_regionToolActive && e.Key == Key.Space)
        {
            e.Handled = true;
            Complete(new Rect(GetImageSize()), includeWindowInfo: false);
            return;
        }

        if (_regionToolActive && e.Key == Key.OemTilde)
        {
            e.Handled = true;
            CompleteActiveMonitor();
            return;
        }

        if (_regionToolActive && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            int distance = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? RegionCaptureOptions.MoveSpeedMaximum
                : RegionCaptureOptions.MoveSpeedMinimum;
            int dx = e.Key == Key.Left ? -distance : e.Key == Key.Right ? distance : 0;
            int dy = e.Key == Key.Up ? -distance : e.Key == Key.Down ? distance : 0;
            NudgePointer(dx, dy, e.KeyModifiers);
            e.Handled = true;
            return;
        }

        if (_regionToolActive && e.Key == Key.Delete && HasValidSelection())
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if ((_interaction is RegionInteraction.Creating or RegionInteraction.PendingHover) &&
            (e.Key is Key.LeftCtrl or Key.RightCtrl))
        {
            _isMovingSelectionDuringCreation = false;
            _wasControlHeldDuringCreation = false;
        }

        if (_keyboardInputEnabled && !_closing && _request != null && _regionToolActive &&
            _interaction == RegionInteraction.Creating && (e.Key is Key.LeftShift or Key.RightShift))
        {
            UpdateSelectionDuringCreation(_lastCreationPoint, e.KeyModifiers & ~KeyModifiers.Shift);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || _closing)
        {
            return;
        }

        e.Handled = true;

        if (!_regionToolActive && _editorWorkspace.CancelActiveInteractionOrSelection())
        {
            return;
        }

        CancelCapture();
    }

    private void ResolveControls()
    {
        _editorWorkspace = this.FindControl<EditorView>("EditorWorkspace")!;
        _regionInputSurface = this.FindControl<Grid>("RegionInputSurface")!;
        _regionOverlay = this.FindControl<RegionSelectionOverlay>("RegionOverlay")!;
        _regionResizeNodeCanvas = this.FindControl<AvaloniaCanvas>("RegionResizeNodeCanvas")!;
        _regionTransform = this.FindControl<LayoutTransformControl>("RegionTransform")!;
        _captureToolbar = this.FindControl<Grid>("CaptureToolbar")!;
        _captureToolbar.LayoutUpdated += OnCaptureToolbarLayoutUpdated;
        _annotationToolbar = this.FindControl<AnnotationToolbar>("CaptureAnnotationToolbar")!;
        _regionToolButton = this.FindControl<Button>("RegionToolButton")!;
        _magnifierPanel = this.FindControl<StackPanel>("MagnifierPanel")!;
        _magnifierPanel.RenderTransform = _magnifierTransform;
        _magnifierView = this.FindControl<Grid>("MagnifierView")!;
        _magnifierImage = this.FindControl<Image>("MagnifierImage")!;
        _magnifierPixelGrid = this.FindControl<MagnifierPixelGrid>("MagnifierPixelGrid")!;
        _pointerInfoPanel = this.FindControl<Border>("PointerInfoPanel")!;
        _pointerInfoText = this.FindControl<TextBlock>("PointerInfoText")!;
        _selectionInfoPanel = this.FindControl<Border>("SelectionInfoPanel")!;
        _selectionInfoText = this.FindControl<TextBlock>("SelectionInfoText")!;
    }

    private void InitializeCaptureWorkspace()
    {
        if (_request == null)
        {
            return;
        }

        _viewModel = new MainViewModel(_request.ImageEditorOptions)
        {
            ShowFileMenu = false,
            ShowTaskButtons = false,
            ShowBottomToolbar = false,
            ShowToolbars = false,
            ShowStartScreen = false,
            UseContinueWorkflow = false
        };
        _viewModel.SetHostToolbarItems(_request.EnableAnnotations
            ? RegionCaptureToolbar.CreateItems(_request.RegionCaptureOptions.ToolbarItems)
            : []);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ToolSelectionRequested += OnAnnotationToolSelected;

        _editorWorkspace.DataContext = _viewModel;
        _annotationToolbar.DataContext = _viewModel.ToolbarAdapter;
        _editorWorkspace.AttachHostAnnotationToolbar(_annotationToolbar);
        _annotationToolbar.IsVisible = _request.EnableAnnotations;
        _annotationToolbar.ShowToolOptions = false;

        _regionInputSurface.Width = Math.Max(1, _imageRectangle.Width);
        _regionInputSurface.Height = Math.Max(1, _imageRectangle.Height);
        _regionOverlay.DimAlpha = GetDimAlpha(_request.RegionCaptureOptions);
        _regionOverlay.DimAllExceptSelection = _request.RegionCaptureOptions.DimAllExceptSelection;
        _regionOverlay.ShowCenterCrosshair = _request.RegionCaptureOptions.ShowCenterCrosshair;
        _regionOverlay.ShowCursorCrosshair = _request.RegionCaptureOptions.ShowScreenCrosshair;
        _regionInputSurface.Cursor = CursorAssetLoader.GetCrosshairCursor(GetInitialScaling());
        InitializeRegionResizeNodes();

        _magnifierView.IsVisible = _request.RegionCaptureOptions.ShowMagnifier;
        ApplyMagnifierShape();
        _pointerInfoPanel.IsVisible = _request.RegionCaptureOptions.ShowInfo;
        _magnifierPanel.IsVisible = _magnifierView.IsVisible || _pointerInfoPanel.IsVisible;
        _regionToolButton.Classes.Set("active", true);
        _viewModel.SetHostToolbarToolsActive(false);
        Title = Localization.Strings.BaseRegionForm_InitializeComponent_Region_capture;
    }

    private void ApplyMagnifierShape()
    {
        if (_request == null)
        {
            return;
        }

        RegionCaptureOptions options = _request.RegionCaptureOptions;
        int magnifierSize = Math.Clamp(options.MagnifierSize,
            RegionCaptureOptions.MagnifierSizeMinimum,
            RegionCaptureOptions.MagnifierSizeMaximum);
        options.MagnifierSize = magnifierSize;
        double outerSize = magnifierSize + 2;

        Control circleOuter = this.FindControl<Control>("MagnifierCircleOuter")!;
        Control circleInner = this.FindControl<Control>("MagnifierCircleInner")!;
        Control squareOuter = this.FindControl<Control>("MagnifierSquareOuter")!;
        Control squareInner = this.FindControl<Control>("MagnifierSquareInner")!;
        bool useSquare = options.UseSquareMagnifier;
        circleOuter.IsVisible = !useSquare;
        circleInner.IsVisible = !useSquare;
        squareOuter.IsVisible = useSquare;
        squareInner.IsVisible = useSquare;

        _magnifierView.Width = outerSize;
        _magnifierView.Height = outerSize;
        circleOuter.Width = outerSize;
        circleOuter.Height = outerSize;
        squareOuter.Width = outerSize;
        squareOuter.Height = outerSize;
        circleInner.Width = magnifierSize;
        circleInner.Height = magnifierSize;
        squareInner.Width = magnifierSize;
        squareInner.Height = magnifierSize;

        Grid magnifierContent = this.FindControl<Grid>("MagnifierContent")!;
        magnifierContent.Width = magnifierSize;
        magnifierContent.Height = magnifierSize;
        _magnifierImage.Width = magnifierSize;
        _magnifierImage.Height = magnifierSize;
        magnifierContent.Clip = useSquare
            ? null
            : new EllipseGeometry(new Rect(0, 0, magnifierSize, magnifierSize));
    }

    private void InitializeRegionResizeNodes()
    {
        double scaling = GetInitialScaling();
        foreach (SelectionResizeNodeKind node in SelectionResizeNode.RectangleNodes)
        {
            Border control = SelectionResizeNode.Create(
                0,
                0,
                node,
                CursorAssetLoader.GetOpenHandCursor(scaling));
            _regionResizeNodeCanvas.Children.Add(control);
            _regionResizeNodes.Add(node, control);
        }
    }

    /// <summary>Shows the selection state shared by all monitors, in this window's coordinates.</summary>
    internal void ApplySharedState()
    {
        if (_session == null)
        {
            return;
        }

        _regionOverlay.SelectionRectangle = ToLocal(_session.Selection);
        _regionOverlay.HoverRectangle = _regionToolActive ? ToLocal(_session.Hover) : default;
        _regionOverlay.IsSelectionPending = _session.IsSelectionPending;
        _regionResizeNodeCanvas.IsVisible = _regionToolActive && _session.ResizeNodesVisible;
        if (_regionResizeNodeCanvas.IsVisible)
        {
            UpdateRegionResizeNodePositions();
        }
        UpdateSelectionInfo();
    }

    private void SetRegionResizeNodesVisible(bool visible)
    {
        _session?.SetResizeNodesVisible(visible);
    }

    private void UpdateRegionResizeNodePositions()
    {
        Rect selection = _regionOverlay.SelectionRectangle;
        if (!RegionSelectionOverlay.IsValid(selection))
        {
            return;
        }

        foreach ((SelectionResizeNodeKind node, Border control) in _regionResizeNodes)
        {
            SelectionResizeNode.SetPosition(control, SelectionResizeNode.GetPosition(selection, node));
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            if (_session == null || _screenshot == null || _viewModel == null)
            {
                CancelCapture();
                return;
            }

            ApplyPixelSize();
            _editorWorkspace.ConfigureForFullscreenWorkspace();
            _editorWorkspace.LoadWorkspaceImage(_screenshot);
            _workspaceOwnsScreenshot = true;
            UpdatePixelTransforms();

            if (_takesInitialFocus)
            {
                Activate();
                Focus();
                _regionInputSurface.Focus();
            }

            PositionCaptureToolbar();
            ApplySharedState();
            UpdateHud(_session.PointerPoint);
            UpdateHover(_session.PointerPoint);

            int inputDelay = Math.Max(0, RegionCaptureOptions.InputDelay);
            if (inputDelay > 0)
            {
                await Task.Delay(inputDelay);
            }

            if (!_closing)
            {
                _keyboardInputEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _session?.Fail(ex);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTool))
        {
            ActivateAnnotationTool();
        }
    }

    private void OnAnnotationToolSelected(object? sender, EditorTool tool)
    {
        ActivateAnnotationTool();
    }

    private void OnRegionToolClick(object? sender, RoutedEventArgs e)
    {
        ActivateRegionTool();
        e.Handled = true;
    }

    private void ActivateRegionTool()
    {
        _regionToolActive = true;
        _regionInputSurface.IsVisible = true;
        _regionInputSurface.IsHitTestVisible = true;
        _regionOverlay.IsVisible = true;
        _regionOverlay.ShowCursorCrosshair = _request?.RegionCaptureOptions.ShowScreenCrosshair == true;
        SetRegionResizeNodesVisible(_request?.RegionCaptureOptions.QuickCapture == false && HasValidSelection());
        ApplySharedState();
        _regionToolButton.Classes.Set("active", true);
        _viewModel?.SetHostToolbarToolsActive(false);
        _annotationToolbar.ShowToolOptions = false;
        _editorWorkspace.CancelActiveInteractionOrSelection();
        _regionInputSurface.Cursor = CursorAssetLoader.GetCrosshairCursor(Math.Max(1, RenderScaling));
        bool showMagnifier = _request?.RegionCaptureOptions.ShowMagnifier == true;
        bool showInfo = _request?.RegionCaptureOptions.ShowInfo == true;
        _magnifierView.IsVisible = showMagnifier;
        _pointerInfoPanel.IsVisible = showInfo;
        _magnifierPanel.IsVisible = showMagnifier || showInfo;
        UpdateHover(PointerPoint);
        UpdateHud(PointerPoint);
        _regionInputSurface.Focus();
    }

    private void ActivateAnnotationTool()
    {
        if (_request?.EnableAnnotations != true)
        {
            return;
        }

        _regionToolActive = false;
        SetInteraction(RegionInteraction.None);
        bool showSelection = !_request.RegionCaptureOptions.QuickCapture && HasValidSelection();
        _regionInputSurface.IsVisible = showSelection;
        _regionInputSurface.IsHitTestVisible = false;
        _regionOverlay.IsVisible = showSelection;
        _regionOverlay.ShowCursorCrosshair = false;
        ApplySharedState();
        _regionToolButton.Classes.Set("active", false);
        _viewModel?.SetHostToolbarToolsActive(true);
        _annotationToolbar.ShowToolOptions = true;
        _magnifierPanel.IsVisible = false;
        _selectionInfoPanel.IsVisible = false;
        _editorWorkspace.Focus();
    }

    private void OnRegionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_closing || _request == null || !_regionToolActive)
        {
            return;
        }

        Point point = GetGlobalPointerPoint(e);
        _session!.PointerPoint = point;
        PointerPointProperties properties = e.GetCurrentPoint(_regionInputSurface).Properties;

        if (TryCancelRegionCreation(e, point))
        {
            return;
        }

        if (properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            e.Handled = true;
            return;
        }

        if (properties.IsMiddleButtonPressed)
        {
            RunCaptureAction(_request.RegionCaptureOptions.RegionCaptureActionMiddleClick);
            e.Handled = true;
            return;
        }

        if (properties.IsXButton1Pressed)
        {
            RunCaptureAction(_request.RegionCaptureOptions.RegionCaptureActionX1Click);
            e.Handled = true;
            return;
        }

        if (properties.IsXButton2Pressed)
        {
            RunCaptureAction(_request.RegionCaptureOptions.RegionCaptureActionX2Click);
            e.Handled = true;
            return;
        }

        if (properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2 && HasValidSelection() && _session.Selection.Contains(point))
        {
            Complete(_session.Selection);
            e.Handled = true;
            return;
        }

        _pressPoint = point;
        _interactionStartRectangle = _session.Selection;
        _resizeNode = SelectionResizeNode.TryGetKind((e.Source as Control)?.Tag, out SelectionResizeNodeKind resizeNode)
            ? resizeNode
            : null;

        if (_resizeNode.HasValue)
        {
            SetInteraction(RegionInteraction.Resizing);
        }
        else if (HasValidSelection() && _session.Selection.Contains(point))
        {
            SetInteraction(RegionInteraction.Moving);
        }
        else if (RegionSelectionOverlay.IsValid(_session.Hover))
        {
            SetSelection(_session.Hover, _session.HoverCandidate);
            _interactionStartRectangle = _session.Selection;
            SetInteraction(RegionInteraction.PendingHover);
        }
        else
        {
            SetSelection(default, null);
            SetInteraction(RegionInteraction.Creating);
        }

        if (_interaction is RegionInteraction.Creating or RegionInteraction.PendingHover)
        {
            _lastCreationPoint = point;
            _isMovingSelectionDuringCreation = false;
            _wasControlHeldDuringCreation = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        }

        SetRegionResizeNodesVisible(false);
        if (_interaction is RegionInteraction.Moving or RegionInteraction.Resizing)
        {
            _regionInputSurface.Cursor = CursorAssetLoader.GetClosedHandCursor(Math.Max(1, RenderScaling));
        }
        e.Pointer.Capture(_regionInputSurface);
        e.Handled = true;
    }

    private void OnRegionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_closing || _request == null || !_regionToolActive)
        {
            return;
        }

        Point point = GetGlobalPointerPoint(e);
        _session!.PointerPoint = point;

        if (TryCancelRegionCreation(e, point))
        {
            return;
        }

        if (TryHandleRightButtonRelease(e))
        {
            return;
        }

        _session.UpdateHud();

        switch (_interaction)
        {
            case RegionInteraction.None:
                UpdateHover(point);
                break;
            case RegionInteraction.PendingHover:
                if (Distance(_pressPoint, point) >= 4)
                {
                    SetInteraction(RegionInteraction.Creating);
                    SetSelection(default, null);
                    UpdateSelectionDuringCreation(point, e.KeyModifiers);
                }
                break;
            case RegionInteraction.Creating:
                UpdateSelectionDuringCreation(point, e.KeyModifiers);
                break;
            case RegionInteraction.Moving:
                MoveSelection(point.X - _pressPoint.X, point.Y - _pressPoint.Y, _interactionStartRectangle);
                break;
            case RegionInteraction.Resizing:
                ResizeSelection(point);
                break;
        }

        e.Handled = true;
    }

    private void OnRegionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_closing || _request == null || !_regionToolActive)
        {
            return;
        }

        Point point = GetGlobalPointerPoint(e);
        _session!.PointerPoint = point;

        if (TryCancelRegionCreation(e, point))
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(_regionInputSurface).Properties;
        PointerUpdateKind updateKind = properties.PointerUpdateKind;
        if (TryHandleRightButtonRelease(e))
        {
            return;
        }

        if (_interaction == RegionInteraction.None)
        {
            if (updateKind == PointerUpdateKind.LeftButtonReleased && !properties.IsRightButtonPressed)
            {
                _suppressNextRightButtonReleaseAction = false;
            }

            return;
        }

        if (updateKind != PointerUpdateKind.LeftButtonReleased)
        {
            return;
        }

        e.Pointer.Capture(null);
        SetInteraction(RegionInteraction.None);
        _resizeNode = null;
        _regionInputSurface.Cursor = CursorAssetLoader.GetCrosshairCursor(Math.Max(1, RenderScaling));
        ResetCreationModifiers();

        Rect selection = _session.Selection;
        if (selection.Width < RegionCaptureOptions.MinimumSize || selection.Height < RegionCaptureOptions.MinimumSize)
        {
            if (RegionSelectionOverlay.IsValid(_session.Hover))
            {
                SetSelection(_session.Hover, _session.HoverCandidate);
            }
            else
            {
                ClearSelection();
            }
        }

        if (HasValidSelection() && _request.RegionCaptureOptions.QuickCapture)
        {
            Complete(_session.Selection);
        }
        else
        {
            SetRegionResizeNodesVisible(HasValidSelection());
            _session.UpdateHud();
        }

        e.Handled = true;
    }

    private bool TryCancelRegionCreation(PointerEventArgs e, Point point)
    {
        PointerUpdateKind updateKind = e.GetCurrentPoint(_regionInputSurface).Properties.PointerUpdateKind;
        if (updateKind is not PointerUpdateKind.RightButtonPressed and not PointerUpdateKind.RightButtonReleased ||
            _interaction is not (RegionInteraction.Creating or RegionInteraction.PendingHover))
        {
            return false;
        }

        _suppressNextRightButtonReleaseAction = updateKind == PointerUpdateKind.RightButtonPressed;
        e.Pointer.Capture(null);
        ClearSelection();
        _session?.UpdateHud();
        e.Handled = true;
        return true;
    }

    private bool TryHandleRightButtonRelease(PointerEventArgs e)
    {
        if (e.GetCurrentPoint(_regionInputSurface).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
        {
            return false;
        }

        if (_suppressNextRightButtonReleaseAction)
        {
            _suppressNextRightButtonReleaseAction = false;
        }
        else if (_request != null)
        {
            RunCaptureAction(_request.RegionCaptureOptions.RegionCaptureActionRightClick);
        }

        e.Handled = true;
        return true;
    }

    private void OnRegionPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_request == null || !_regionToolActive)
        {
            return;
        }

        int delta = e.Delta.Y > 0 ? -2 : 2;
        int count = NormalizeMagnifierPixelCount(_request.RegionCaptureOptions.MagnifierPixelCount + delta);

        _request.RegionCaptureOptions.MagnifierPixelCount = count;
        _request.RegionCaptureOptions.ShowMagnifier = true;
        RecreateMagnifierBitmap();
        _session?.UpdateHud();
        e.Handled = true;
    }

    private void OnCaptureToolbarLayoutUpdated(object? sender, EventArgs e)
    {
        PositionCaptureToolbar();
    }

    private void PositionCaptureToolbar()
    {
        double toolbarWidth = _captureToolbar.Bounds.Width > 0
            ? _captureToolbar.Bounds.Width
            : _captureToolbar.DesiredSize.Width;
        if (!double.IsFinite(toolbarWidth) || toolbarWidth <= 0 ||
            Math.Abs(toolbarWidth - _positionedCaptureToolbarWidth) < 0.01)
        {
            return;
        }

        _positionedCaptureToolbarWidth = toolbarWidth;
        AvaloniaCanvas.SetLeft(_captureToolbar, (Bounds.Width - toolbarWidth) / 2d);
        AvaloniaCanvas.SetTop(_captureToolbar, 0);
    }

    private void OnCaptureHostPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_regionToolActive || _closing || _request == null || !IsEditorWorkspaceSource(e.Source))
        {
            return;
        }

        PointerUpdateKind updateKind = e.GetCurrentPoint(_editorWorkspace).Properties.PointerUpdateKind;
        if (updateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            if (_viewModel?.IsModalOpen == true || IsEditorNotificationSource(e.Source))
            {
                _annotationMiddleButtonPressed = false;
                return;
            }

            _annotationMiddleButtonPressed = true;
            e.Handled = true;
            RunAnnotationCaptureAction(
                _request.RegionCaptureOptions.RegionCaptureActionMiddleClick,
                e.GetPosition(_editorWorkspace));
            return;
        }

        if (updateKind == PointerUpdateKind.RightButtonPressed)
        {
            if (_viewModel?.IsModalOpen == true || IsEditorNotificationSource(e.Source))
            {
                _annotationRightButtonPressed = false;
                return;
            }

            _annotationRightButtonPressed = true;
            e.Handled = true;
        }
    }

    private void OnCaptureHostPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_annotationMiddleButtonPressed)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _annotationMiddleButtonPressed = false;
            return;
        }

        e.Handled = true;
    }

    private void OnCaptureHostPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_annotationMiddleButtonPressed &&
            e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
        {
            _annotationMiddleButtonPressed = false;
            e.Handled = true;
            return;
        }

        if (!_annotationRightButtonPressed || _regionToolActive || _closing || _request == null)
        {
            return;
        }

        if (e.GetCurrentPoint(_editorWorkspace).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
        {
            return;
        }

        _annotationRightButtonPressed = false;
        RunAnnotationCaptureAction(
            _request.RegionCaptureOptions.RegionCaptureActionRightClick,
            e.GetPosition(_editorWorkspace));
        e.Handled = true;
    }

    private bool IsEditorWorkspaceSource(object? source)
    {
        Visual? visual = source as Visual;
        while (visual != null)
        {
            if (ReferenceEquals(visual, _editorWorkspace))
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }

    private static bool IsEditorNotificationSource(object? source)
    {
        Visual? visual = source as Visual;
        while (visual != null)
        {
            if (visual is Control { Name: "EditorNotificationHost" })
            {
                return true;
            }

            visual = visual.GetVisualParent();
        }

        return false;
    }

    private void RunAnnotationCaptureAction(RegionCaptureAction action, Point workspacePoint)
    {
        if (_request == null)
        {
            return;
        }

        if (action == RegionCaptureAction.RemoveShapeCancelCapture)
        {
            if (!_editorWorkspace.DeleteWorkspaceAnnotationAt(workspacePoint))
            {
                CancelCapture();
            }
        }
        else if (action == RegionCaptureAction.RemoveShape)
        {
            _editorWorkspace.DeleteWorkspaceAnnotationAt(workspacePoint);
        }
        else
        {
            RunCaptureAction(action);
        }
    }

    private void UpdateHover(Point imagePoint)
    {
        if (_session == null)
        {
            return;
        }

        if (HasValidSelection() || _interaction != RegionInteraction.None)
        {
            _session.SetHover(default, null);
            return;
        }

        double x = Math.Round(imagePoint.X);
        double y = Math.Round(imagePoint.Y);
        SimpleWindowInfo? candidate = _session.Candidates.FirstOrDefault(window => ContainsPoint(window.Rectangle, x, y));

        if (candidate == null)
        {
            _session.SetHover(default, null);
            return;
        }

        DrawingRectangle rectangle = candidate.Rectangle;
        Rect hover = RegionSelectionOverlay.Intersect(
            new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
            new Rect(GetImageSize()));
        _session.SetHover(hover, RegionSelectionOverlay.IsValid(hover) ? candidate : null);
    }

    private static bool ContainsPoint(DrawingRectangle rectangle, double x, double y)
    {
        return rectangle.Width > 0 && rectangle.Height > 0 &&
            x >= rectangle.X && y >= rectangle.Y &&
            x < (double)rectangle.X + rectangle.Width &&
            y < (double)rectangle.Y + rectangle.Height;
    }

    private void SetSelection(Rect rectangle, SimpleWindowInfo? candidate)
    {
        _session?.SetSelection(RegionSelectionOverlay.Intersect(rectangle, new Rect(GetImageSize())), candidate);
    }

    private void SetInteraction(RegionInteraction interaction)
    {
        _interaction = interaction;
        _session?.SetSelectionPending(interaction == RegionInteraction.PendingHover);
    }

    private void ClearSelection()
    {
        SetInteraction(RegionInteraction.None);
        ResetCreationModifiers();
        _session?.SetSelection(default, null);
        SetRegionResizeNodesVisible(false);
        UpdateHover(PointerPoint);
    }

    private void MoveSelection(double dx, double dy, Rect source)
    {
        Size bounds = GetImageSize();
        double x = Math.Clamp(source.X + dx, 0, Math.Max(0, bounds.Width - source.Width));
        double y = Math.Clamp(source.Y + dy, 0, Math.Max(0, bounds.Height - source.Height));
        SetSelection(new Rect(x, y, source.Width, source.Height), null);
    }

    private void UpdateSelectionDuringCreation(Point point, KeyModifiers modifiers)
    {
        bool controlHeld = modifiers.HasFlag(KeyModifiers.Control);
        if (!controlHeld)
        {
            _isMovingSelectionDuringCreation = false;
        }
        else if (RegionSelectionOverlay.IsValid(_session!.Selection) &&
            (_isMovingSelectionDuringCreation || !_wasControlHeldDuringCreation))
        {
            MoveSelectionDuringCreation(point);
            _isMovingSelectionDuringCreation = true;
            _lastCreationPoint = point;
            _wasControlHeldDuringCreation = true;
            return;
        }

        bool constrainToSquare = modifiers.HasFlag(KeyModifiers.Shift);
        SetSelection(CreateSelectionRectangle(_pressPoint, point, GetImageSize(), constrainToSquare), null);
        _lastCreationPoint = point;
        _wasControlHeldDuringCreation = controlHeld;
    }

    private void MoveSelectionDuringCreation(Point point)
    {
        Rect before = _session!.Selection;
        MoveSelection(point.X - _lastCreationPoint.X, point.Y - _lastCreationPoint.Y, before);
        Rect after = _session.Selection;
        _pressPoint = ClampPoint(new Point(
            _pressPoint.X + after.X - before.X,
            _pressPoint.Y + after.Y - before.Y));
    }

    private static Rect CreateSelectionRectangle(Point first, Point second, Size bounds, bool constrainToSquare)
    {
        if (!constrainToSquare)
        {
            return RegionSelectionOverlay.CreatePixelRectangle(first, second, bounds);
        }

        double deltaX = second.X - first.X;
        double deltaY = second.Y - first.Y;
        double side = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
        double availableWidth = deltaX < 0 ? first.X : bounds.Width - 1 - first.X;
        double availableHeight = deltaY < 0 ? first.Y : bounds.Height - 1 - first.Y;
        side = Math.Max(0, Math.Min(side, Math.Min(availableWidth, availableHeight)));

        Point constrained = new(
            first.X + (deltaX < 0 ? -side : side),
            first.Y + (deltaY < 0 ? -side : side));
        return RegionSelectionOverlay.CreatePixelRectangle(first, constrained, bounds);
    }

    private void ResetCreationModifiers()
    {
        _isMovingSelectionDuringCreation = false;
        _wasControlHeldDuringCreation = false;
    }

    private void ResizeSelection(Point point)
    {
        if (!_resizeNode.HasValue)
        {
            return;
        }

        Vector delta = point - _pressPoint;
        Rect resized = SelectionResizeNode.Resize(_session!.Selection, _resizeNode.Value, delta);
        SetSelection(resized, null);
        _pressPoint = point;
    }

    internal void UpdateHud(Point imagePoint)
    {
        if (_request == null)
        {
            return;
        }

        Point local = new Point(imagePoint.X - _imageRectangle.X, imagePoint.Y - _imageRectangle.Y);
        _regionOverlay.CursorPosition = local;

        bool showMagnifier = _request.RegionCaptureOptions.ShowMagnifier;
        bool showInfo = _request.RegionCaptureOptions.ShowInfo;
        bool pointerIsHere = imagePoint.X >= _imageRectangle.Left && imagePoint.X < _imageRectangle.Right &&
            imagePoint.Y >= _imageRectangle.Top && imagePoint.Y < _imageRectangle.Bottom;

        if (pointerIsHere && _regionToolActive && (showMagnifier || showInfo))
        {
            if (showMagnifier)
            {
                UpdateMagnifier(imagePoint);
            }

            _magnifierView.IsVisible = showMagnifier;
            _pointerInfoPanel.IsVisible = showInfo;
            _magnifierPanel.IsVisible = true;
            _pointerInfoText.Text = GetPointerInfoText(imagePoint);

            double scale = GetImagePixelsPerDip();
            Point pointer = new Point(local.X / scale, local.Y / scale);
            PositionPanelNearPointer(_magnifierPanel, pointer, 18, new Rect(Bounds.Size));
            _magnifierPanel.InvalidateVisual();
        }
        else
        {
            _magnifierPanel.IsVisible = false;
        }
    }

    private string GetPointerInfoText(Point imagePoint)
    {
        if (_request == null)
        {
            return string.Empty;
        }

        Size imageSize = GetImageSize();
        int imageX = Math.Clamp((int)Math.Round(imagePoint.X), 0, (int)imageSize.Width - 1);
        int imageY = Math.Clamp((int)Math.Round(imagePoint.Y), 0, (int)imageSize.Height - 1);
        DrawingPoint screenPosition = _session!.Request.Coordinates.ImageToX11(new DrawingRectangle(imageX, imageY, 1, 1)).Location;

        RegionCaptureOptions options = _request.RegionCaptureOptions;
        if (options.UseCustomInfoText)
        {
            SKColor pixel = _session.GetPixel(imageX, imageY);
            DrawingColor color = DrawingColor.FromArgb(pixel.Alpha, pixel.Red, pixel.Green, pixel.Blue);

            if (!string.IsNullOrEmpty(options.CustomInfoText))
            {
                return CodeMenuEntryPixelInfo.Parse(options.CustomInfoText, color, screenPosition);
            }

            return $"RGB: {color.R}, {color.G}, {color.B}{Environment.NewLine}" +
                $"Hex: {ColorHelpers.ColorToHex(color)}{Environment.NewLine}" +
                $"X: {screenPosition.X} Y: {screenPosition.Y}";
        }

        return $"X: {screenPosition.X} Y: {screenPosition.Y}";
    }

    private unsafe void UpdateMagnifier(Point imagePoint)
    {
        if (_request == null)
        {
            return;
        }

        EnsureMagnifierBitmap();
        if (_magnifierBitmap == null)
        {
            return;
        }

        int count = _magnifierBitmap.PixelSize.Width;
        int radius = count / 2;
        int centerX = (int)Math.Round(imagePoint.X);
        int centerY = (int)Math.Round(imagePoint.Y);
        Size imageSize = GetImageSize();
        int lastX = (int)imageSize.Width - 1;
        int lastY = (int)imageSize.Height - 1;

        using ILockedFramebuffer framebuffer = _magnifierBitmap.Lock();
        byte* destination = (byte*)framebuffer.Address;

        for (int y = 0; y < count; y++)
        {
            byte* row = destination + y * framebuffer.RowBytes;
            int sourceY = Math.Clamp(centerY + y - radius, 0, lastY);

            for (int x = 0; x < count; x++)
            {
                int sourceX = Math.Clamp(centerX + x - radius, 0, lastX);
                SKColor color = _session!.GetPixel(sourceX, sourceY);
                int offset = x * 4;
                row[offset] = color.Blue;
                row[offset + 1] = color.Green;
                row[offset + 2] = color.Red;
                row[offset + 3] = color.Alpha;
            }
        }
    }

    private void EnsureMagnifierBitmap()
    {
        if (_request == null)
        {
            return;
        }

        int count = NormalizeMagnifierPixelCount(_request.RegionCaptureOptions.MagnifierPixelCount);

        if (_magnifierBitmap?.PixelSize == new PixelSize(count, count))
        {
            return;
        }

        RecreateMagnifierBitmap(count);
    }

    private void RecreateMagnifierBitmap(int? requestedCount = null)
    {
        if (_request == null)
        {
            return;
        }

        int count = NormalizeMagnifierPixelCount(requestedCount ?? _request.RegionCaptureOptions.MagnifierPixelCount);
        _request.RegionCaptureOptions.MagnifierPixelCount = count;

        if (_magnifierBitmap?.PixelSize == new PixelSize(count, count))
        {
            _magnifierPixelGrid.PixelCount = count;
            return;
        }

        _magnifierBitmap?.Dispose();
        _magnifierBitmap = new WriteableBitmap(
            new PixelSize(count, count),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        _magnifierImage.Source = _magnifierBitmap;
        _magnifierPixelGrid.PixelCount = count;
    }

    private int NormalizeMagnifierPixelCount(int requestedCount)
    {
        double scale = double.IsFinite(RenderScaling) && RenderScaling > 0 ? RenderScaling : 1;
        int magnifierSize = Math.Clamp(_request?.RegionCaptureOptions.MagnifierSize ?? RegionCaptureOptions.MagnifierSizeMinimum,
            RegionCaptureOptions.MagnifierSizeMinimum,
            RegionCaptureOptions.MagnifierSizeMaximum);
        int sizeLimitedMaximum = (int)Math.Floor(
            magnifierSize * scale / RegionCaptureOptions.MagnifierPixelSizeMinimum);
        int maximum = Math.Clamp(
            sizeLimitedMaximum,
            RegionCaptureOptions.MagnifierPixelCountMinimum,
            RegionCaptureOptions.MagnifierPixelCountMaximum);

        if ((maximum & 1) == 0)
        {
            maximum--;
        }

        int count = Math.Clamp(requestedCount, RegionCaptureOptions.MagnifierPixelCountMinimum, maximum);
        if ((count & 1) == 0)
        {
            count = count < maximum ? count + 1 : count - 1;
        }

        return count;
    }

    private void UpdateSelectionInfo()
    {
        if (_session == null || _request?.RegionCaptureOptions.ShowInfo != true)
        {
            _selectionInfoPanel.IsVisible = false;
            return;
        }

        Rect selection = _session.Selection;
        if (!RegionSelectionOverlay.IsValid(selection) || _session.GetSelectionInfoWindow() != this)
        {
            _selectionInfoPanel.IsVisible = false;
            return;
        }

        int width = Math.Max(1, (int)Math.Round(selection.Width));
        int height = Math.Max(1, (int)Math.Round(selection.Height));
        _selectionInfoText.Text = $"{width} × {height}";
        _selectionInfoPanel.IsVisible = true;

        double scale = GetImagePixelsPerDip();
        Rect local = ToLocal(selection);
        double panelWidth = _selectionInfoPanel.Bounds.Width > 0 ? _selectionInfoPanel.Bounds.Width : 100;
        double panelHeight = _selectionInfoPanel.Bounds.Height > 0 ? _selectionInfoPanel.Bounds.Height : 36;
        Rect area = new Rect(Bounds.Size);

        // Prefer below the selection, otherwise above it.
        double y = local.Bottom / scale + 8;
        if (y + panelHeight > area.Bottom)
        {
            y = local.Top / scale - panelHeight;
        }

        double x = Math.Clamp(local.Left / scale, area.Left, Math.Max(area.Left, area.Right - panelWidth));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - panelHeight));

        AvaloniaCanvas.SetLeft(_selectionInfoPanel, x);
        AvaloniaCanvas.SetTop(_selectionInfoPanel, y);
    }

    private void PositionPanelNearPointer(Control panel, Point pointer, double offset, Rect area)
    {
        bool isMagnifierPanel = ReferenceEquals(panel, _magnifierPanel);
        double fallbackWidth = isMagnifierPanel ? _magnifierView.Width : 170;
        double fallbackHeight = isMagnifierPanel ? _magnifierView.Height + 53 : 205;
        double width = panel.Bounds.Width > 0 ? panel.Bounds.Width : fallbackWidth;
        double height = panel.Bounds.Height > 0 ? panel.Bounds.Height : fallbackHeight;
        double x = pointer.X + offset;
        double y = pointer.Y + offset;

        if (x + width > area.Right)
        {
            x = pointer.X - width - offset;
        }

        if (y + height > area.Bottom)
        {
            y = pointer.Y - height - offset;
        }

        double targetX = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
        double targetY = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));

        if (isMagnifierPanel)
        {
            double scale = Math.Max(1, RenderScaling);
            targetX = Math.Round(targetX * scale) / scale;
            targetY = Math.Round(targetY * scale) / scale;
            _magnifierTransform.X = targetX;
            _magnifierTransform.Y = targetY;
            _magnifierPixelGrid.InvalidateVisual();
        }
        else
        {
            AvaloniaCanvas.SetLeft(panel, targetX);
            AvaloniaCanvas.SetTop(panel, targetY);
        }
    }

    private void RunCaptureAction(RegionCaptureAction action)
    {
        if (_request == null)
        {
            return;
        }

        switch (action)
        {
            case RegionCaptureAction.None:
                break;
            case RegionCaptureAction.CancelCapture:
                CancelCapture();
                break;
            case RegionCaptureAction.RemoveShapeCancelCapture:
                if (HasValidSelection() && _session!.Selection.Contains(PointerPoint))
                {
                    ClearSelection();
                }
                else
                {
                    CancelCapture();
                }
                break;
            case RegionCaptureAction.RemoveShape:
                if (HasValidSelection() && _session!.Selection.Contains(PointerPoint))
                {
                    ClearSelection();
                }
                break;
            case RegionCaptureAction.SwapToolType:
                if (_regionToolActive)
                {
                    ActivateAnnotationTool();
                }
                else
                {
                    ActivateRegionTool();
                }
                break;
            case RegionCaptureAction.CaptureFullscreen:
                Complete(new Rect(GetImageSize()), includeWindowInfo: false);
                break;
            case RegionCaptureAction.CaptureActiveMonitor:
                CompleteActiveMonitor();
                break;
            case RegionCaptureAction.CaptureLastRegion:
                CompleteLastRegion();
                break;
        }
    }

    private void CompleteLastRegion()
    {
        if (_session == null || RegionCaptureIntegration.LastRegionRectangle.IsEmpty)
        {
            return;
        }

        DrawingRectangle rectangle = _session.Request.Coordinates.X11ToImage(RegionCaptureIntegration.LastRegionRectangle);
        Rect selection = RegionSelectionOverlay.Intersect(
            new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
            new Rect(GetImageSize()));
        Complete(selection, includeWindowInfo: false);
    }

    private void CompleteActiveMonitor()
    {
        DrawingRectangle? monitor = _session?.GetMonitorImageRectangle(PointerPoint);
        if (monitor is DrawingRectangle rectangle)
        {
            Complete(new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), includeWindowInfo: false);
        }
    }

    private void Complete(Rect selection, bool includeWindowInfo = true)
    {
        if (!_closing)
        {
            _session?.Complete(selection, includeWindowInfo);
        }
    }

    private void CancelCapture()
    {
        if (_closing)
        {
            return;
        }

        if (_session != null)
        {
            _session.Cancel();
        }
        else
        {
            _closing = true;
            Close();
        }
    }

    private bool HasValidSelection()
    {
        if (_session == null)
        {
            return false;
        }

        Rect selection = _session.Selection;
        return selection.Width >= RegionCaptureOptions.MinimumSize &&
            selection.Height >= RegionCaptureOptions.MinimumSize;
    }

    private Point ClampPoint(Point point)
    {
        Size size = GetImageSize();
        return new Point(
            Math.Clamp(point.X, 0, size.Width),
            Math.Clamp(point.Y, 0, size.Height));
    }

    private Size GetImageSize()
    {
        return _session?.ImageSize ?? default;
    }

    private Point PointerPoint => _session?.PointerPoint ?? default;

    /// <summary>Pointer position in the frozen screenshot. While dragging, X11 keeps reporting to the
    /// window that was pressed, with coordinates outside it, so a drag can cross monitors.</summary>
    private Point GetGlobalPointerPoint(PointerEventArgs e)
    {
        Point local = e.GetPosition(_regionInputSurface);
        return ClampPoint(new Point(local.X + _imageRectangle.X, local.Y + _imageRectangle.Y));
    }

    private Rect ToLocal(Rect imageRectangle)
    {
        return RegionSelectionOverlay.IsValid(imageRectangle)
            ? imageRectangle.Translate(new Vector(-_imageRectangle.X, -_imageRectangle.Y))
            : default;
    }

    /// <summary>Moves a virtual pointer. A Wayland client can't warp the real one.</summary>
    private void NudgePointer(int dx, int dy, KeyModifiers modifiers)
    {
        if (_session == null)
        {
            return;
        }

        Point point = ClampPoint(new Point(PointerPoint.X + dx, PointerPoint.Y + dy));
        _session.PointerPoint = point;
        _session.UpdateHud();

        switch (_interaction)
        {
            case RegionInteraction.None:
                UpdateHover(point);
                break;
            case RegionInteraction.Creating:
                UpdateSelectionDuringCreation(point, modifiers & ~KeyModifiers.Shift);
                break;
        }
    }

    private void ApplyPixelSize()
    {
        double scaling = GetRenderScaling();
        Width = _x11Rectangle.Width / scaling;
        Height = _x11Rectangle.Height / scaling;
    }

    // The frozen screenshot and the window are the same size in pixels unless XwaylandClientsScale is off.
    private double GetImagePixelsPerX11Pixel()
    {
        return _x11Rectangle.Width > 0 ? (double)_imageRectangle.Width / _x11Rectangle.Width : 1;
    }

    private double GetImagePixelsPerDip()
    {
        return GetImagePixelsPerX11Pixel() * GetRenderScaling();
    }

    private double GetRenderScaling()
    {
        return double.IsFinite(RenderScaling) && RenderScaling > 0 ? RenderScaling : 1;
    }

    private void UpdatePixelTransforms()
    {
        double scaling = GetRenderScaling();
        double imagePixelsPerDip = GetImagePixelsPerDip();
        _regionTransform.LayoutTransform = new ScaleTransform(1 / imagePixelsPerDip, 1 / imagePixelsPerDip);
        if (_viewModel != null)
        {
            _viewModel.DpiScale = scaling;
            _viewModel.Zoom = 1 / GetImagePixelsPerX11Pixel();
        }
        _regionInputSurface.Cursor = CursorAssetLoader.GetCrosshairCursor(scaling);
        foreach (Border node in _regionResizeNodes.Values)
        {
            node.Cursor = CursorAssetLoader.GetOpenHandCursor(scaling);
        }
    }

    private double GetInitialScaling()
    {
        PixelPoint topLeft = new PixelPoint(_x11Rectangle.X, _x11Rectangle.Y);
        return Screens.ScreenFromPoint(topLeft)?.Scaling ?? Screens.Primary?.Scaling ?? 1;
    }

    private static byte GetDimAlpha(RegionCaptureOptions options)
    {
        if (options.BackgroundDimStrength <= 0)
        {
            return 0;
        }

        return (byte)Math.Clamp((int)Math.Round(options.BackgroundDimStrength / 100d * 255), 0, 255);
    }

    private static double Distance(Point first, Point second)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private enum RegionInteraction
    {
        None,
        PendingHover,
        Creating,
        Moving,
        Resizing
    }
}
