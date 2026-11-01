using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FanController.Models;
using FanController.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using WinForms = System.Windows.Forms;

namespace FanController;

public partial class MainWindow : Window
{
    private readonly HardwareFanService _hardwareService = new();
    private readonly DispatcherTimer _pollTimer = new();
    private readonly ObservableCollection<FanChannelViewModel> _visibleChannels = new();
    private List<FanChannelViewModel> _allChannels = new();
    private FanChannelViewModel? _selectedFan;
    private WinForms.NotifyIcon? _trayIcon;
    private CancellationTokenSource? _calibrationCts;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        FanChannelsListBox.ItemsSource = _visibleChannels;

        InitializeTrayIcon();
        InitializeHardwareAndConfig();

        _pollTimer.Interval = TimeSpan.FromSeconds(1.0);
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        // 若由开机计划任务附带 --minimized 参数启动，则直接静默驻留系统托盘
        if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Loaded += (_, _) => Hide();
        }
    }

    private void InitializeHardwareAndConfig()
    {
        if (_hardwareService.IsAdministrator)
        {
            AdminBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70));
            AdminBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            AdminBadgeText.Text = "管理员";
        }
        else
        {
            AdminBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(120, 53, 15));
            AdminBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
            AdminBadgeText.Text = "点击提权";
        }

        _allChannels = _hardwareService.InitializeAndScanFans();

        AppConfig savedConfig = ConfigStorage.Load();
        RestoreBiosOnExitCheckBox.IsChecked = savedConfig.RestoreBiosOnExit;
        HideZeroRpmCheckBox.IsChecked = savedConfig.HideEmptyHeaders;
        CloseBehaviorCombo.SelectedIndex = (int)savedConfig.CloseAction;
        AutoStartCheckBox.IsChecked = StartupManager.IsEnabled();

        foreach (FanChannelViewModel fan in _allChannels)
        {
            FanProfileConfig? matched = savedConfig.FanProfiles.FirstOrDefault(p => p.Id == fan.Id);
            if (matched != null)
            {
                fan.ApplyConfig(matched);
            }
            fan.CurveChanged += () => Dispatcher.InvokeAsync(DrawFanCurve);
        }

        _hardwareService.UpdateReadings(_allChannels, applyControl: true);
        UpdateTopDashboardUi();
        RefreshFilteredFanList();

        StatusFooterText.Text = $"已识别 {_allChannels.Count} 个风扇通道";
    }

    private void RefreshFilteredFanList()
    {
        string? previousSelectedId = _selectedFan?.Id;
        bool hideEmpty = HideZeroRpmCheckBox.IsChecked == true;

        _visibleChannels.Clear();
        foreach (FanChannelViewModel fan in _allChannels)
        {
            if (hideEmpty && !fan.IsGpuFan && fan.CurrentRpm < 10 && (!fan.IsCalibrated || fan.CalibratedMaxRpm < 50))
            {
                continue;
            }
            _visibleChannels.Add(fan);
        }

        // 若全部被过滤（例如刚启动尚未转起），回退显示全部
        if (_visibleChannels.Count == 0)
        {
            foreach (var fan in _allChannels)
                _visibleChannels.Add(fan);
        }

        FanChannelViewModel? nextSelection = _visibleChannels.FirstOrDefault(f => f.Id == previousSelectedId)
                                             ?? _visibleChannels.FirstOrDefault();

        FanChannelsListBox.SelectedItem = nextSelection;
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_hardwareService.IsCalibrating)
        {
            _hardwareService.UpdateReadings(_allChannels, applyControl: true);
        }
        UpdateTopDashboardUi();
        DrawFanCurve();

        if (_trayIcon != null)
        {
            _trayIcon.Text = $"风扇温控 | CPU {_hardwareService.CpuTemp:F0}°C · GPU {_hardwareService.GpuTemp:F0}°C";
        }
    }

    private void UpdateTopDashboardUi()
    {
        CpuTempText.Text = $"{_hardwareService.CpuTemp:F1}°C";
        CpuLoadText.Text = $"{_hardwareService.CpuLoad:F0}%";
        GpuTempText.Text = $"{_hardwareService.GpuTemp:F1}°C";
        GpuLoadText.Text = $"{_hardwareService.GpuLoad:F0}%";
    }

    private void FanChannelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FanChannelsListBox.SelectedItem is FanChannelViewModel selected)
        {
            _selectedFan = selected;
            DetailPanelBorder.DataContext = selected;
            DetailPanelBorder.IsEnabled = true;
            TempSourceCombo.SelectedIndex = (int)selected.TempSource;
            DrawFanCurve();
        }
        else
        {
            _selectedFan = null;
            DetailPanelBorder.DataContext = null;
            DetailPanelBorder.IsEnabled = false;
            CurveCanvas.Children.Clear();
        }
    }

    private void TempSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedFan != null && TempSourceCombo.SelectedIndex >= 0)
        {
            _selectedFan.TempSource = (TempSourceType)TempSourceCombo.SelectedIndex;
            DrawFanCurve();
        }
    }

    private void ControlMode_Changed(object sender, RoutedEventArgs e)
    {
        DrawFanCurve();
    }

    private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshFilteredFanList();
        }
    }

    private void CloseBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SaveCurrentConfigSilently();
        }
    }

    private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool wantEnable = AutoStartCheckBox.IsChecked == true;
        bool ok = StartupManager.SetEnabled(wantEnable, out string msg);
        if (!ok)
        {
            // 若因未提权等原因设置失败，回滚勾选状态
            AutoStartCheckBox.IsChecked = StartupManager.IsEnabled();
        }
        StatusFooterText.Text = msg;
    }

    private void AdminBadgeBorder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_hardwareService.IsAdministrator) return;

        try
        {
            string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FanController.exe");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
            _isExiting = true;
            Close();
        }
        catch
        {
        }
    }

    // ==================== 自动风扇校准功能 ====================

    private async void CalibrateAllFans_Click(object sender, RoutedEventArgs e)
    {
        await RunCalibrationAsync(_visibleChannels.ToList());
    }

    private async void CalibrateSelectedFan_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFan == null) return;
        await RunCalibrationAsync([_selectedFan]);
    }

    private async Task RunCalibrationAsync(IReadOnlyList<FanChannelViewModel> targets)
    {
        if (_hardwareService.IsCalibrating || targets.Count == 0) return;

        _calibrationCts = new CancellationTokenSource();
        CalibrationBanner.Visibility = Visibility.Visible;
        CalibrateAllBtn.IsEnabled = false;
        CalibrateSingleBtn.IsEnabled = false;

        var progress = new Progress<(double percent, string message)>(report =>
        {
            CalibrationProgressBar.Value = report.percent;
            CalibrationStatusText.Text = report.message;
            StatusFooterText.Text = report.message;
            DrawFanCurve();
        });

        try
        {
            await _hardwareService.CalibrateFansAsync(targets, progress, _calibrationCts.Token);
            SaveCurrentConfigSilently();
            RefreshFilteredFanList();
        }
        catch (OperationCanceledException)
        {
            StatusFooterText.Text = "已取消风扇校准，恢复正常温控";
        }
        finally
        {
            CalibrationBanner.Visibility = Visibility.Collapsed;
            CalibrateAllBtn.IsEnabled = true;
            CalibrateSingleBtn.IsEnabled = true;
            _calibrationCts?.Dispose();
            _calibrationCts = null;
            DrawFanCurve();
        }
    }

    private void CancelCalibration_Click(object sender, RoutedEventArgs e)
    {
        _calibrationCts?.Cancel();
    }

    // ==================== 预设与保存 ====================

    private void GlobalSilentPreset_Click(object sender, RoutedEventArgs e)
    {
        foreach (FanChannelViewModel fan in _allChannels)
        {
            fan.Mode = FanControlMode.SmartLoadCurve;
            fan.LowLoadTemp = 48;
            fan.LowLoadSpeed = fan.IsCalibrated && fan.CalibratedMinPwm > 0 ? Math.Max(25, fan.CalibratedMinPwm) : 25;
            fan.MedLoadTemp = 64;
            fan.MedLoadSpeed = 45;
            fan.HighLoadTemp = 78;
            fan.HighLoadSpeed = 68;
            fan.CriticalTemp = 86;
            fan.CriticalSpeed = 100;
        }
        DrawFanCurve();
        StatusFooterText.Text = "已切换：静音模式";
    }

    private void GlobalBalancedPreset_Click(object sender, RoutedEventArgs e)
    {
        foreach (FanChannelViewModel fan in _allChannels)
        {
            fan.Mode = FanControlMode.SmartLoadCurve;
            fan.LowLoadTemp = 45;
            fan.LowLoadSpeed = fan.IsCalibrated && fan.CalibratedMinPwm > 0 ? Math.Max(32, fan.CalibratedMinPwm) : 32;
            fan.MedLoadTemp = 60;
            fan.MedLoadSpeed = 55;
            fan.HighLoadTemp = 75;
            fan.HighLoadSpeed = 80;
            fan.CriticalTemp = 85;
            fan.CriticalSpeed = 100;
        }
        DrawFanCurve();
        StatusFooterText.Text = "已切换：均衡模式";
    }

    private void GlobalPerformancePreset_Click(object sender, RoutedEventArgs e)
    {
        foreach (FanChannelViewModel fan in _allChannels)
        {
            fan.Mode = FanControlMode.SmartLoadCurve;
            fan.LowLoadTemp = 40;
            fan.LowLoadSpeed = 45;
            fan.MedLoadTemp = 55;
            fan.MedLoadSpeed = 70;
            fan.HighLoadTemp = 70;
            fan.HighLoadSpeed = 95;
            fan.CriticalTemp = 80;
            fan.CriticalSpeed = 100;
        }
        DrawFanCurve();
        StatusFooterText.Text = "已切换：性能模式";
    }

    private void GlobalBiosDefault_Click(object sender, RoutedEventArgs e)
    {
        foreach (FanChannelViewModel fan in _allChannels)
        {
            fan.Mode = FanControlMode.BiosAuto;
        }
        _hardwareService.RestoreAllFansToBiosDefault();
        DrawFanCurve();
        StatusFooterText.Text = "已恢复 BIOS 自动控制";
    }

    private void CopyCurveToAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFan == null) return;

        foreach (FanChannelViewModel fan in _allChannels)
        {
            if (fan.Id == _selectedFan.Id) continue;

            fan.Mode = _selectedFan.Mode;
            fan.ManualSpeedPercent = _selectedFan.ManualSpeedPercent;
            fan.LowLoadTemp = _selectedFan.LowLoadTemp;
            fan.LowLoadSpeed = _selectedFan.LowLoadSpeed;
            fan.MedLoadTemp = _selectedFan.MedLoadTemp;
            fan.MedLoadSpeed = _selectedFan.MedLoadSpeed;
            fan.HighLoadTemp = _selectedFan.HighLoadTemp;
            fan.HighLoadSpeed = _selectedFan.HighLoadSpeed;
            fan.CriticalTemp = _selectedFan.CriticalTemp;
            fan.CriticalSpeed = _selectedFan.CriticalSpeed;
            fan.HysteresisTemp = _selectedFan.HysteresisTemp;
        }

        StatusFooterText.Text = "已同步当前曲线到全部风扇";
    }

    private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfigSilently();
        StatusFooterText.Text = $"已保存 ({DateTime.Now:HH:mm:ss})";
    }

    private void SaveCurrentConfigSilently()
    {
        var cfg = new AppConfig
        {
            RestoreBiosOnExit = RestoreBiosOnExitCheckBox.IsChecked == true,
            HideEmptyHeaders = HideZeroRpmCheckBox.IsChecked == true,
            CloseAction = (CloseBehavior)Math.Clamp(CloseBehaviorCombo.SelectedIndex, 0, 2),
            FanProfiles = _allChannels.Select(f => f.ToConfig()).ToList()
        };
        ConfigStorage.Save(cfg);
    }

    // ==================== 简洁曲线绘制 ====================

    private void CurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawFanCurve();

    private void DrawFanCurve()
    {
        CurveCanvas.Children.Clear();
        if (_selectedFan == null) return;

        double w = CurveCanvas.ActualWidth;
        double h = CurveCanvas.ActualHeight;
        if (w < 80 || h < 50) return;

        const double padLeft = 34;
        const double padRight = 14;
        const double padTop = 12;
        const double padBottom = 20;

        double plotW = w - padLeft - padRight;
        double plotH = h - padTop - padBottom;

        double TempToX(double temp) => padLeft + Math.Clamp((temp - 20.0) / 80.0, 0.0, 1.0) * plotW;
        double PwmToY(double pwm) => padTop + (1.0 - Math.Clamp(pwm / 100.0, 0.0, 1.0)) * plotH;

        var gridBrush = new SolidColorBrush(Color.FromRgb(26, 38, 56));
        var labelBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));

        for (int pwm = 0; pwm <= 100; pwm += 25)
        {
            double y = PwmToY(pwm);
            CurveCanvas.Children.Add(new Line { X1 = padLeft, Y1 = y, X2 = w - padRight, Y2 = y, Stroke = gridBrush, StrokeThickness = 1 });
            var txt = new TextBlock { Text = $"{pwm}%", Foreground = labelBrush, FontSize = 10 };
            Canvas.SetLeft(txt, 2);
            Canvas.SetTop(txt, y - 7);
            CurveCanvas.Children.Add(txt);
        }

        for (int temp = 20; temp <= 100; temp += 20)
        {
            double x = TempToX(temp);
            CurveCanvas.Children.Add(new Line { X1 = x, Y1 = padTop, X2 = x, Y2 = h - padBottom, Stroke = gridBrush, StrokeThickness = 1 });
            var txt = new TextBlock { Text = $"{temp}°C", Foreground = labelBrush, FontSize = 10 };
            Canvas.SetLeft(txt, x - 12);
            Canvas.SetTop(txt, h - padBottom + 3);
            CurveCanvas.Children.Add(txt);
        }

        // 如果已校准出最低起转阈值，绘制一条淡绿色虚线标注起转线
        if (_selectedFan.IsCalibrated && _selectedFan.CalibratedMinPwm > 0)
        {
            double minY = PwmToY(_selectedFan.CalibratedMinPwm);
            var minLine = new Line
            {
                X1 = padLeft,
                Y1 = minY,
                X2 = w - padRight,
                Y2 = minY,
                Stroke = new SolidColorBrush(Color.FromArgb(110, 16, 185, 129)),
                StrokeDashArray = new DoubleCollection { 4, 4 },
                StrokeThickness = 1
            };
            CurveCanvas.Children.Add(minLine);

            var minTxt = new TextBlock
            {
                Text = $"起转阈值 {_selectedFan.CalibratedMinPwm:F0}%",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 16, 185, 129)),
                FontSize = 10
            };
            Canvas.SetLeft(minTxt, w - padRight - 72);
            Canvas.SetTop(minTxt, Math.Max(2, minY - 15));
            CurveCanvas.Children.Add(minTxt);
        }

        var polyPoints = new PointCollection();
        var fillPoints = new PointCollection { new Point(TempToX(20), PwmToY(0)) };

        for (int t = 20; t <= 100; t += 1)
        {
            double pwm = _selectedFan.EvaluateCurveAtTemp(t);
            var pt = new Point(TempToX(t), PwmToY(pwm));
            polyPoints.Add(pt);
            fillPoints.Add(pt);
        }
        fillPoints.Add(new Point(TempToX(100), PwmToY(0)));

        CurveCanvas.Children.Add(new Polygon
        {
            Points = fillPoints,
            Fill = new SolidColorBrush(Color.FromArgb(30, 56, 189, 248))
        });

        CurveCanvas.Children.Add(new Polyline
        {
            Points = polyPoints,
            Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            StrokeThickness = 2
        });

        if (_selectedFan.Mode == FanControlMode.SmartLoadCurve)
        {
            DrawNodeDot(TempToX(_selectedFan.LowLoadTemp), PwmToY(_selectedFan.LowLoadSpeed), Color.FromRgb(16, 185, 129));
            DrawNodeDot(TempToX(_selectedFan.MedLoadTemp), PwmToY(_selectedFan.MedLoadSpeed), Color.FromRgb(56, 189, 248));
            DrawNodeDot(TempToX(_selectedFan.HighLoadTemp), PwmToY(_selectedFan.HighLoadSpeed), Color.FromRgb(245, 158, 11));
            DrawNodeDot(TempToX(_selectedFan.CriticalTemp), PwmToY(_selectedFan.CriticalSpeed), Color.FromRgb(239, 68, 68));
        }

        // 当前实时工况点
        double curX = TempToX(_selectedFan.CurrentSourceTemp);
        double curY = PwmToY(_selectedFan.TargetPwm);

        var liveDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(250, 204, 21)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(liveDot, curX - 4);
        Canvas.SetTop(liveDot, curY - 4);
        CurveCanvas.Children.Add(liveDot);
    }

    private void DrawNodeDot(double x, double y, Color color)
    {
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(color),
            Stroke = Brushes.White,
            StrokeThickness = 1
        };
        Canvas.SetLeft(dot, x - 3.5);
        Canvas.SetTop(dot, y - 3.5);
        CurveCanvas.Children.Add(dot);
    }

    // ==================== 系统托盘与退出 ====================

    private void InitializeTrayIcon()
    {
        System.Drawing.Icon trayIconImage = System.Drawing.SystemIcons.Application;
        try
        {
            string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(icoPath))
            {
                trayIconImage = new System.Drawing.Icon(icoPath);
            }
            else if (!string.IsNullOrEmpty(Environment.ProcessPath) && System.IO.File.Exists(Environment.ProcessPath))
            {
                trayIconImage = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? trayIconImage;
            }
        }
        catch
        {
        }

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = trayIconImage,
            Visible = true,
            Text = "风扇温控中心"
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("静音模式", null, (_, _) => Dispatcher.Invoke(() => GlobalSilentPreset_Click(this, new RoutedEventArgs())));
        menu.Items.Add("均衡模式", null, (_, _) => Dispatcher.Invoke(() => GlobalBalancedPreset_Click(this, new RoutedEventArgs())));
        menu.Items.Add("性能模式", null, (_, _) => Dispatcher.Invoke(() => GlobalPerformancePreset_Click(this, new RoutedEventArgs())));
        menu.Items.Add("BIOS 自动", null, (_, _) => Dispatcher.Invoke(() => GlobalBiosDefault_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) =>
        {
            _isExiting = true;
            Dispatcher.Invoke(Close);
        });

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            var behavior = (CloseBehavior)Math.Clamp(CloseBehaviorCombo.SelectedIndex, 0, 2);

            if (behavior == CloseBehavior.AskEveryTime)
            {
                var (selectedBehavior, remember) = ShowCloseConfirmationDialog();
                if (selectedBehavior == null)
                {
                    // 用户点击了取消或关闭了询问框
                    e.Cancel = true;
                    return;
                }

                behavior = selectedBehavior.Value;
                if (remember)
                {
                    CloseBehaviorCombo.SelectedIndex = (int)behavior;
                }
            }

            if (behavior == CloseBehavior.MinimizeToTray)
            {
                e.Cancel = true;
                SaveCurrentConfigSilently();
                Hide();
                return;
            }
        }

        _calibrationCts?.Cancel();
        SaveCurrentConfigSilently();
        _pollTimer.Stop();

        if (RestoreBiosOnExitCheckBox.IsChecked == true)
        {
            _hardwareService.RestoreAllFansToBiosDefault();
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _hardwareService.Dispose();
    }

    /// <summary>
    /// 弹出简洁深色确认框，询问用户是最小化到系统托盘还是直接退出，并支持记住选择
    /// </summary>
    private (CloseBehavior? choice, bool remember) ShowCloseConfirmationDialog()
    {
        CloseBehavior? result = null;

        var dlg = new Window
        {
            Title = "关闭确认",
            Width = 380,
            Height = 205,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            Icon = Icon
        };

        var root = new Grid { Margin = new Thickness(18, 16, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock
        {
            Text = "点击关闭按钮时，您希望执行什么操作？",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(promptText, 0);
        root.Children.Add(promptText);

        var rememberCheck = new System.Windows.Controls.CheckBox
        {
            Content = "记住我的选择，下次不再提示",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 14)
        };
        Grid.SetRow(rememberCheck, 1);
        root.Children.Add(rememberCheck);

        var btnPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var btnStyle = TryFindResource("ModernButtonStyle") as Style;
        var primaryStyle = TryFindResource("PrimaryButtonStyle") as Style;

        var trayBtn = new System.Windows.Controls.Button
        {
            Content = "最小化到托盘",
            Style = primaryStyle,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        trayBtn.Click += (_, _) =>
        {
            result = CloseBehavior.MinimizeToTray;
            dlg.Close();
        };

        var exitBtn = new System.Windows.Controls.Button
        {
            Content = "直接退出",
            Style = btnStyle,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        exitBtn.Click += (_, _) =>
        {
            result = CloseBehavior.ExitApplication;
            dlg.Close();
        };

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "取消",
            Style = btnStyle,
            Padding = new Thickness(10, 6, 10, 6),
            IsCancel = true
        };
        cancelBtn.Click += (_, _) =>
        {
            result = null;
            dlg.Close();
        };

        btnPanel.Children.Add(trayBtn);
        btnPanel.Children.Add(exitBtn);
        btnPanel.Children.Add(cancelBtn);
        Grid.SetRow(btnPanel, 2);
        root.Children.Add(btnPanel);

        dlg.Content = root;
        dlg.ShowDialog();

        return (result, rememberCheck.IsChecked == true);
    }
}
