using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;

namespace FanController.Models;

public enum FanControlMode
{
    SmartLoadCurve = 0,
    ManualFixed = 1,
    BiosAuto = 2
}

public enum TempSourceType
{
    MaxCpuGpu = 0,
    CpuOnly = 1,
    GpuOnly = 2
}

public enum CloseBehavior
{
    AskEveryTime = 0,
    MinimizeToTray = 1,
    ExitApplication = 2
}

public class FanProfileConfig
{
    public string Id { get; set; } = string.Empty;
    public string CustomName { get; set; } = string.Empty;
    public FanControlMode Mode { get; set; } = FanControlMode.SmartLoadCurve;
    public TempSourceType TempSource { get; set; } = TempSourceType.MaxCpuGpu;
    public double ManualSpeedPercent { get; set; } = 50;

    public double LowLoadTemp { get; set; } = 45;
    public double LowLoadSpeed { get; set; } = 30;

    public double MedLoadTemp { get; set; } = 60;
    public double MedLoadSpeed { get; set; } = 55;

    public double HighLoadTemp { get; set; } = 75;
    public double HighLoadSpeed { get; set; } = 80;

    public double CriticalTemp { get; set; } = 85;
    public double CriticalSpeed { get; set; } = 100;

    public double HysteresisTemp { get; set; } = 3.0;

    // 校准数据持久化
    public bool IsCalibrated { get; set; }
    public double CalibratedMinPwm { get; set; }
    public double CalibratedStopPwm { get; set; }
    public double CalibratedMinRpm { get; set; }
    public double CalibratedMaxRpm { get; set; }
}

public class AppConfig
{
    public bool RestoreBiosOnExit { get; set; } = true;
    public bool HideEmptyHeaders { get; set; } = true;
    public CloseBehavior CloseAction { get; set; } = CloseBehavior.AskEveryTime;
    public List<FanProfileConfig> FanProfiles { get; set; } = new();
}

public class FanChannelViewModel : INotifyPropertyChanged
{
    private string _customName = string.Empty;
    private double _currentRpm;
    private bool _hasRpmSensor;
    private double _currentPwm;
    private double _targetPwm;
    private double _currentSourceTemp;
    private string _loadStageText = "待机";
    private string _loadStageColor = "#38BDF8";

    private FanControlMode _mode = FanControlMode.SmartLoadCurve;
    private TempSourceType _tempSource = TempSourceType.MaxCpuGpu;
    private double _manualSpeedPercent = 50;

    private double _lowLoadTemp = 45;
    private double _lowLoadSpeed = 30;
    private double _medLoadTemp = 60;
    private double _medLoadSpeed = 55;
    private double _highLoadTemp = 75;
    private double _highLoadSpeed = 80;
    private double _criticalTemp = 85;
    private double _criticalSpeed = 100;
    private double _hysteresisTemp = 3.0;

    // 校准相关字段
    private bool _isCalibrated;
    private double _calibratedMinPwm;
    private double _calibratedStopPwm;
    private double _calibratedMinRpm;
    private double _calibratedMaxRpm;

    private double _effectiveTemp = 40;
    private double _lastAppliedPwm = -1;

    public string Id { get; init; } = string.Empty;
    public string DefaultName { get; init; } = string.Empty;
    public string HardwareName { get; init; } = string.Empty;
    public bool IsGpuFan { get; init; }

    public string CustomName
    {
        get => string.IsNullOrWhiteSpace(_customName) ? DefaultName : _customName;
        set
        {
            if (_customName != value)
            {
                _customName = value;
                OnPropertyChanged();
            }
        }
    }

    public double CurrentRpm
    {
        get => _currentRpm;
        set
        {
            if (Math.Abs(_currentRpm - value) > 0.5)
            {
                _currentRpm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RpmDisplayText));
            }
        }
    }

    public bool HasRpmSensor
    {
        get => _hasRpmSensor;
        set
        {
            if (_hasRpmSensor != value)
            {
                _hasRpmSensor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RpmDisplayText));
            }
        }
    }

    public string RpmDisplayText => HasRpmSensor ? $"{CurrentRpm:F0} RPM" : "无测速信号";

    public double CurrentPwm
    {
        get => _currentPwm;
        set
        {
            if (Math.Abs(_currentPwm - value) > 0.1)
            {
                _currentPwm = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
            }
        }
    }

    public double TargetPwm
    {
        get => _targetPwm;
        set
        {
            if (Math.Abs(_targetPwm - value) > 0.1)
            {
                _targetPwm = Math.Clamp(value, 0, 100);
                OnPropertyChanged();
            }
        }
    }

    public double CurrentSourceTemp
    {
        get => _currentSourceTemp;
        set
        {
            if (Math.Abs(_currentSourceTemp - value) > 0.1)
            {
                _currentSourceTemp = value;
                OnPropertyChanged();
            }
        }
    }

    public string LoadStageText
    {
        get => _loadStageText;
        set
        {
            if (_loadStageText != value)
            {
                _loadStageText = value;
                OnPropertyChanged();
            }
        }
    }

    public string LoadStageColor
    {
        get => _loadStageColor;
        set
        {
            if (_loadStageColor != value)
            {
                _loadStageColor = value;
                OnPropertyChanged();
            }
        }
    }

    // 校准属性
    public bool IsCalibrated
    {
        get => _isCalibrated;
        set
        {
            if (_isCalibrated != value)
            {
                _isCalibrated = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CalibrationBadgeText));
            }
        }
    }

    public double CalibratedMinPwm
    {
        get => _calibratedMinPwm;
        set
        {
            _calibratedMinPwm = Math.Round(Math.Clamp(value, 0, 100));
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationBadgeText));
        }
    }

    public double CalibratedStopPwm
    {
        get => _calibratedStopPwm;
        set
        {
            _calibratedStopPwm = Math.Round(Math.Clamp(value, 0, 100));
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationBadgeText));
        }
    }

    public double CalibratedMinRpm
    {
        get => _calibratedMinRpm;
        set
        {
            _calibratedMinRpm = Math.Round(Math.Max(0, value));
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationBadgeText));
        }
    }

    public double CalibratedMaxRpm
    {
        get => _calibratedMaxRpm;
        set
        {
            _calibratedMaxRpm = Math.Round(Math.Max(0, value));
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationBadgeText));
        }
    }

    public string CalibrationBadgeText => IsCalibrated
        ? (CalibratedMaxRpm > 50
            ? $"已校准 · 起转 {CalibratedMinPwm:F0}% / 停转 {CalibratedStopPwm:F0}% ({CalibratedMinRpm:F0}~{CalibratedMaxRpm:F0} RPM)"
            : "已校准 · 未检测到风扇转速信号")
        : "未校准 (点击右侧按钮深度测定起转/停转阈值与满速)";

    public FanControlMode Mode
    {
        get => _mode;
        set
        {
            if (_mode != value)
            {
                _mode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSmartMode));
                OnPropertyChanged(nameof(IsManualMode));
                OnPropertyChanged(nameof(IsBiosMode));
                OnPropertyChanged(nameof(SmartPanelVisibility));
                OnPropertyChanged(nameof(ManualPanelVisibility));
                OnPropertyChanged(nameof(BiosPanelVisibility));
                NotifyCurveUpdated();
            }
        }
    }

    [JsonIgnore]
    public bool IsSmartMode
    {
        get => Mode == FanControlMode.SmartLoadCurve;
        set { if (value) Mode = FanControlMode.SmartLoadCurve; }
    }

    [JsonIgnore]
    public bool IsManualMode
    {
        get => Mode == FanControlMode.ManualFixed;
        set { if (value) Mode = FanControlMode.ManualFixed; }
    }

    [JsonIgnore]
    public bool IsBiosMode
    {
        get => Mode == FanControlMode.BiosAuto;
        set { if (value) Mode = FanControlMode.BiosAuto; }
    }

    [JsonIgnore]
    public Visibility SmartPanelVisibility => Mode == FanControlMode.SmartLoadCurve ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility ManualPanelVisibility => Mode == FanControlMode.ManualFixed ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility BiosPanelVisibility => Mode == FanControlMode.BiosAuto ? Visibility.Visible : Visibility.Collapsed;

    public TempSourceType TempSource
    {
        get => _tempSource;
        set
        {
            if (_tempSource != value)
            {
                _tempSource = value;
                OnPropertyChanged();
            }
        }
    }

    public double ManualSpeedPercent
    {
        get => _manualSpeedPercent;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (Math.Abs(_manualSpeedPercent - clamped) > 0.1)
            {
                _manualSpeedPercent = clamped;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double LowLoadTemp
    {
        get => _lowLoadTemp;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 20, 70));
            if (Math.Abs(_lowLoadTemp - clamped) > 0.1)
            {
                _lowLoadTemp = clamped;
                if (_medLoadTemp <= _lowLoadTemp) MedLoadTemp = _lowLoadTemp + 5;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double LowLoadSpeed
    {
        get => _lowLoadSpeed;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (Math.Abs(_lowLoadSpeed - clamped) > 0.1)
            {
                _lowLoadSpeed = clamped;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double MedLoadTemp
    {
        get => _medLoadTemp;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, _lowLoadTemp + 2, 85));
            if (Math.Abs(_medLoadTemp - clamped) > 0.1)
            {
                _medLoadTemp = clamped;
                if (_highLoadTemp <= _medLoadTemp) HighLoadTemp = _medLoadTemp + 5;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double MedLoadSpeed
    {
        get => _medLoadSpeed;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (Math.Abs(_medLoadSpeed - clamped) > 0.1)
            {
                _medLoadSpeed = clamped;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double HighLoadTemp
    {
        get => _highLoadTemp;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, _medLoadTemp + 2, 95));
            if (Math.Abs(_highLoadTemp - clamped) > 0.1)
            {
                _highLoadTemp = clamped;
                if (_criticalTemp <= _highLoadTemp) CriticalTemp = _highLoadTemp + 5;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double HighLoadSpeed
    {
        get => _highLoadSpeed;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (Math.Abs(_highLoadSpeed - clamped) > 0.1)
            {
                _highLoadSpeed = clamped;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double CriticalTemp
    {
        get => _criticalTemp;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, _highLoadTemp + 2, 105));
            if (Math.Abs(_criticalTemp - clamped) > 0.1)
            {
                _criticalTemp = clamped;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double CriticalSpeed
    {
        get => _criticalSpeed;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 50, 100));
            if (Math.Abs(_criticalSpeed - clamped) > 0.1)
            {
                _criticalSpeed = clamped;
                OnPropertyChanged();
                NotifyCurveUpdated();
            }
        }
    }

    public double HysteresisTemp
    {
        get => _hysteresisTemp;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0, 10), 1);
            if (Math.Abs(_hysteresisTemp - clamped) > 0.05)
            {
                _hysteresisTemp = clamped;
                OnPropertyChanged();
            }
        }
    }

    public event Action? CurveChanged;

    private void NotifyCurveUpdated() => CurveChanged?.Invoke();

    public double EvaluateCurveAtTemp(double temp)
    {
        if (Mode == FanControlMode.ManualFixed)
            return ManualSpeedPercent;

        if (temp <= LowLoadTemp)
            return LowLoadSpeed;

        if (temp >= CriticalTemp)
            return CriticalSpeed;

        if (temp <= MedLoadTemp)
        {
            double ratio = (temp - LowLoadTemp) / Math.Max(1.0, MedLoadTemp - LowLoadTemp);
            return LowLoadSpeed + ratio * (MedLoadSpeed - LowLoadSpeed);
        }

        if (temp <= HighLoadTemp)
        {
            double ratio = (temp - MedLoadTemp) / Math.Max(1.0, HighLoadTemp - MedLoadTemp);
            return MedLoadSpeed + ratio * (HighLoadSpeed - MedLoadSpeed);
        }

        {
            double ratio = (temp - HighLoadTemp) / Math.Max(1.0, CriticalTemp - HighLoadTemp);
            return HighLoadSpeed + ratio * (CriticalSpeed - HighLoadSpeed);
        }
    }

    public double ComputeNextPwm(double currentSensorTemp)
    {
        CurrentSourceTemp = currentSensorTemp;

        if (Mode == FanControlMode.BiosAuto)
        {
            LoadStageText = "BIOS 自动";
            LoadStageColor = "#64748B";
            TargetPwm = CurrentPwm;
            _lastAppliedPwm = -1;
            return -1;
        }

        if (Mode == FanControlMode.ManualFixed)
        {
            LoadStageText = $"固定 {ManualSpeedPercent:F0}%";
            LoadStageColor = "#A855F7";
            TargetPwm = ManualSpeedPercent;
            _lastAppliedPwm = ManualSpeedPercent;
            return ManualSpeedPercent;
        }

        if (currentSensorTemp >= CriticalTemp)
        {
            _effectiveTemp = currentSensorTemp;
        }
        else if (currentSensorTemp > _effectiveTemp)
        {
            _effectiveTemp = currentSensorTemp;
        }
        else if (_effectiveTemp - currentSensorTemp >= HysteresisTemp)
        {
            _effectiveTemp = currentSensorTemp;
        }

        double rawTarget = EvaluateCurveAtTemp(_effectiveTemp);

        if (_effectiveTemp <= LowLoadTemp + 1)
        {
            LoadStageText = "低负载";
            LoadStageColor = "#10B981";
        }
        else if (_effectiveTemp <= MedLoadTemp + 1)
        {
            LoadStageText = "中负载";
            LoadStageColor = "#38BDF8";
        }
        else if (_effectiveTemp < CriticalTemp)
        {
            LoadStageText = "高负载";
            LoadStageColor = "#F59E0B";
        }
        else
        {
            LoadStageText = "满载保护";
            LoadStageColor = "#EF4444";
        }

        double smoothedPwm = rawTarget;
        if (_lastAppliedPwm >= 0 && _effectiveTemp < CriticalTemp)
        {
            const double maxStepUp = 12.0;
            const double maxStepDown = 6.0;
            double diff = rawTarget - _lastAppliedPwm;
            if (diff > maxStepUp)
                smoothedPwm = _lastAppliedPwm + maxStepUp;
            else if (diff < -maxStepDown)
                smoothedPwm = _lastAppliedPwm - maxStepDown;
        }

        smoothedPwm = Math.Round(Math.Clamp(smoothedPwm, 0, 100));
        TargetPwm = smoothedPwm;
        _lastAppliedPwm = smoothedPwm;
        return smoothedPwm;
    }

    public FanProfileConfig ToConfig() => new()
    {
        Id = Id,
        CustomName = _customName,
        Mode = Mode,
        TempSource = TempSource,
        ManualSpeedPercent = ManualSpeedPercent,
        LowLoadTemp = LowLoadTemp,
        LowLoadSpeed = LowLoadSpeed,
        MedLoadTemp = MedLoadTemp,
        MedLoadSpeed = MedLoadSpeed,
        HighLoadTemp = HighLoadTemp,
        HighLoadSpeed = HighLoadSpeed,
        CriticalTemp = CriticalTemp,
        CriticalSpeed = CriticalSpeed,
        HysteresisTemp = HysteresisTemp,
        IsCalibrated = IsCalibrated,
        CalibratedMinPwm = CalibratedMinPwm,
        CalibratedStopPwm = CalibratedStopPwm,
        CalibratedMinRpm = CalibratedMinRpm,
        CalibratedMaxRpm = CalibratedMaxRpm
    };

    public void ApplyConfig(FanProfileConfig cfg)
    {
        _customName = cfg.CustomName;
        LowLoadTemp = cfg.LowLoadTemp;
        LowLoadSpeed = cfg.LowLoadSpeed;
        MedLoadTemp = cfg.MedLoadTemp;
        MedLoadSpeed = cfg.MedLoadSpeed;
        HighLoadTemp = cfg.HighLoadTemp;
        HighLoadSpeed = cfg.HighLoadSpeed;
        CriticalTemp = cfg.CriticalTemp;
        CriticalSpeed = cfg.CriticalSpeed;
        HysteresisTemp = cfg.HysteresisTemp;
        ManualSpeedPercent = cfg.ManualSpeedPercent;
        TempSource = cfg.TempSource;
        Mode = cfg.Mode;
        IsCalibrated = cfg.IsCalibrated;
        CalibratedMinPwm = cfg.CalibratedMinPwm;
        CalibratedStopPwm = cfg.CalibratedStopPwm;
        CalibratedMinRpm = cfg.CalibratedMinRpm;
        CalibratedMaxRpm = cfg.CalibratedMaxRpm;
        OnPropertyChanged(nameof(CustomName));
        NotifyCurveUpdated();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
