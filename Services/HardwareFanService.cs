using System.IO;
using System.Security.Principal;
using System.Text.Json;
using FanController.Models;
using LibreHardwareMonitor.Hardware;

namespace FanController.Services;

public sealed class HardwareUpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware)
        {
            subHardware.Accept(this);
        }
    }

    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}

public sealed class HardwareFanService : IDisposable
{
    private readonly Computer _computer;
    private readonly HardwareUpdateVisitor _updateVisitor = new();
    private readonly Dictionary<string, ISensor> _controlSensors = new();
    private readonly Dictionary<string, ISensor?> _rpmSensors = new();
    private bool _isOpen;

    private double _smoothedCpuTemp = 40.0;
    private double _smoothedGpuTemp = 40.0;
    private bool _firstTempSample = true;

    public string CpuName { get; private set; } = "CPU";
    public string GpuName { get; private set; } = "GPU";
    public string MotherboardName { get; private set; } = "Motherboard";
    public bool IsAdministrator { get; }
    public bool IsCalibrating { get; private set; }

    public double CpuTemp { get; private set; }
    public double CpuLoad { get; private set; }
    public double GpuTemp { get; private set; }
    public double GpuHotSpotTemp { get; private set; }
    public double GpuLoad { get; private set; }

    public HardwareFanService()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        IsAdministrator = principal.IsInRole(WindowsBuiltInRole.Administrator);

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };
    }

    public List<FanChannelViewModel> InitializeAndScanFans()
    {
        var result = new List<FanChannelViewModel>();

        try
        {
            if (!_isOpen)
            {
                _computer.Open();
                _isOpen = true;
            }
            _computer.Accept(_updateVisitor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hardware Open Error: {ex.Message}");
        }

        _controlSensors.Clear();
        _rpmSensors.Clear();

        foreach (IHardware hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Motherboard:
                    MotherboardName = hw.Name;
                    break;
                case HardwareType.Cpu:
                    CpuName = hw.Name;
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    GpuName = hw.Name;
                    break;
            }

            ScanHardwareNode(hw, parentName: null, result);
        }

        UpdateReadings(result, applyControl: false);
        return result;
    }

    private void ScanHardwareNode(IHardware hw, string? parentName, List<FanChannelViewModel> output)
    {
        string hwDisplayName = string.IsNullOrEmpty(parentName)
            ? hw.Name
            : $"{parentName} ({hw.Name})";

        var controls = hw.Sensors.Where(s => s.SensorType == SensorType.Control).OrderBy(s => s.Index).ToList();
        var fans = hw.Sensors.Where(s => s.SensorType == SensorType.Fan).OrderBy(s => s.Index).ToList();

        foreach (ISensor ctrl in controls)
        {
            string id = ctrl.Identifier.ToString();
            _controlSensors[id] = ctrl;

            ISensor? matchedFan = fans.FirstOrDefault(f => f.Index == ctrl.Index);
            if (matchedFan == null && fans.Count == 1 && controls.Count == 1)
            {
                matchedFan = fans[0];
            }
            _rpmSensors[id] = matchedFan;

            bool isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            string friendlyDefaultName = BuildFriendlyFanName(ctrl, matchedFan, isGpu);

            var vm = new FanChannelViewModel
            {
                Id = id,
                DefaultName = friendlyDefaultName,
                HardwareName = hwDisplayName,
                IsGpuFan = isGpu,
                HasRpmSensor = matchedFan != null,
                CurrentRpm = matchedFan?.Value ?? 0,
                CurrentPwm = ctrl.Value ?? 0,
                TargetPwm = ctrl.Value ?? 50,
                TempSource = isGpu
                    ? TempSourceType.GpuOnly
                    : (ctrl.Index == 0 ? TempSourceType.CpuOnly : TempSourceType.MaxCpuGpu)
            };

            output.Add(vm);
        }

        foreach (IHardware sub in hw.SubHardware)
        {
            ScanHardwareNode(sub, hw.Name, output);
        }
    }

    private static string BuildFriendlyFanName(ISensor ctrl, ISensor? rpmSensor, bool isGpu)
    {
        if (isGpu)
        {
            return $"显卡风扇 #{ctrl.Index}";
        }

        string baseName = rpmSensor != null ? rpmSensor.Name : ctrl.Name;
        return ctrl.Index switch
        {
            0 => $"CPU 散热风扇 (CPU_FAN)",
            1 => $"机箱风扇 1 (CHA_FAN1)",
            2 => $"机箱风扇 2 (CHA_FAN2)",
            3 => $"水冷/机箱风扇 3 (AIO_PUMP)",
            _ => $"主板风扇 #{ctrl.Index + 1} ({baseName})"
        };
    }

    public void UpdateReadings(IEnumerable<FanChannelViewModel> channels, bool applyControl = true)
    {
        if (!_isOpen) return;

        try
        {
            _computer.Accept(_updateVisitor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Hardware Update Error: {ex.Message}");
            return;
        }

        double? rawCpuTemp = null;
        double? fallbackMbCpuTemp = null;
        double? rawGpuTemp = null;

        foreach (IHardware hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    foreach (ISensor s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value is > 0 and < 125)
                        {
                            if (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                rawCpuTemp == null || s.Value.Value > rawCpuTemp.Value)
                            {
                                rawCpuTemp = s.Value.Value;
                            }
                        }
                        else if (s.SensorType == SensorType.Load && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)
                        {
                            CpuLoad = Math.Round(s.Value.Value, 1);
                        }
                    }
                    break;

                case HardwareType.Motherboard:
                    foreach (IHardware sub in hw.SubHardware)
                    {
                        foreach (ISensor s in sub.Sensors)
                        {
                            if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value is > 10 and < 115)
                            {
                                if (s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || fallbackMbCpuTemp == null)
                                {
                                    fallbackMbCpuTemp = s.Value.Value;
                                }
                            }
                        }
                    }
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    foreach (ISensor s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value is > 0 and < 125)
                        {
                            if (s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                            {
                                GpuHotSpotTemp = Math.Round(s.Value.Value, 1);
                            }
                            else if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || rawGpuTemp == null)
                            {
                                rawGpuTemp = s.Value.Value;
                            }
                        }
                        else if (s.SensorType == SensorType.Load && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)
                        {
                            GpuLoad = Math.Round(s.Value.Value, 1);
                        }
                    }
                    break;
            }
        }

        double latestCpu = rawCpuTemp ?? fallbackMbCpuTemp ?? 40.0;
        double latestGpu = rawGpuTemp ?? 38.0;

        if (_firstTempSample)
        {
            _smoothedCpuTemp = latestCpu;
            _smoothedGpuTemp = latestGpu;
            _firstTempSample = false;
        }
        else
        {
            _smoothedCpuTemp = _smoothedCpuTemp * 0.55 + latestCpu * 0.45;
            _smoothedGpuTemp = _smoothedGpuTemp * 0.55 + latestGpu * 0.45;
        }

        CpuTemp = Math.Round(_smoothedCpuTemp, 1);
        GpuTemp = Math.Round(_smoothedGpuTemp, 1);

        foreach (FanChannelViewModel channel in channels)
        {
            if (_rpmSensors.TryGetValue(channel.Id, out ISensor? rpmSensor) && rpmSensor != null)
            {
                channel.HasRpmSensor = true;
                channel.CurrentRpm = rpmSensor.Value ?? 0;
            }

            if (_controlSensors.TryGetValue(channel.Id, out ISensor? ctrlSensor))
            {
                channel.CurrentPwm = Math.Round(ctrlSensor.Value ?? 0, 0);

                double refTemp = channel.TempSource switch
                {
                    TempSourceType.CpuOnly => CpuTemp,
                    TempSourceType.GpuOnly => GpuTemp,
                    _ => Math.Max(CpuTemp, GpuTemp)
                };

                // 校准期间不执行常规温控覆盖
                if (IsCalibrating)
                {
                    channel.CurrentSourceTemp = refTemp;
                    continue;
                }

                double nextPwm = channel.ComputeNextPwm(refTemp);

                if (applyControl && ctrlSensor.Control != null)
                {
                    try
                    {
                        if (nextPwm < 0)
                        {
                            ctrlSensor.Control.SetDefault();
                        }
                        else
                        {
                            ctrlSensor.Control.SetSoftware((float)nextPwm);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Control Fan [{channel.Id}] Error: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 深度自动风扇校准：动态等待转速稳定（消除叶片惯性误差），分别测定满速、降速停转点与静止起转点
    /// </summary>
    public async Task CalibrateFansAsync(
        IReadOnlyList<FanChannelViewModel> targetFans,
        IProgress<(double percent, string message)> progress,
        CancellationToken ct)
    {
        if (targetFans.Count == 0 || IsCalibrating) return;

        IsCalibrating = true;
        try
        {
            foreach (var f in targetFans)
            {
                f.LoadStageText = "校准中...";
                f.LoadStageColor = "#F59E0B";
            }

            // ==================== 阶段 1：100% 满速稳定测试 ====================
            SetPwmBatch(targetFans, 100);
            await WaitUntilRpmStabilizesAsync(
                targetFans,
                minWaitMs: 7500,
                maxWaitMs: 14000,
                baseProgress: 5,
                progressSpan: 12,
                stageTitle: "阶段 1/3 [100% 满速测定]",
                progress,
                ct);

            var activeFans = new List<FanChannelViewModel>();
            var minKeepSpinPwm = new Dictionary<string, double>(); // 降速过程中能维持转动的最低 PWM
            var minKeepSpinRpm = new Dictionary<string, double>();
            var startUpPwm = new Dictionary<string, double>();     // 从静止重新起转所需的最低 PWM
            var stoppedAtZero = new HashSet<string>();

            foreach (var f in targetFans)
            {
                f.CalibratedMaxRpm = f.CurrentRpm;
                if (f.CurrentRpm >= 80)
                {
                    activeFans.Add(f);
                    minKeepSpinPwm[f.Id] = 100;
                    minKeepSpinRpm[f.Id] = f.CurrentRpm;
                    startUpPwm[f.Id] = 100;
                }
                else
                {
                    f.CalibratedMinPwm = 0;
                    f.CalibratedStopPwm = 0;
                    f.CalibratedMinRpm = 0;
                    f.IsCalibrated = true;
                }
            }

            if (activeFans.Count == 0)
            {
                progress.Report((100, "校准完成：未检测到有效的风扇转速信号"));
                return;
            }

            // ==================== 阶段 2：阶梯降速测定最低维持转速与停转点 ====================
            int[] downSteps = [85, 70, 55, 45, 35, 28, 22, 16, 10, 0];
            for (int i = 0; i < downSteps.Length; i++)
            {
                int pwm = downSteps[i];
                double basePct = 18 + i * (52.0 / downSteps.Length);
                double spanPct = 52.0 / downSteps.Length;

                SetPwmBatch(activeFans, pwm);

                // 0% 档位给更长的时间让风扇克服惯性完全停稳
                int minWait = pwm == 0 ? 8000 : 5200;
                int maxWait = pwm == 0 ? 14000 : 10500;

                await WaitUntilRpmStabilizesAsync(
                    activeFans,
                    minWaitMs: minWait,
                    maxWaitMs: maxWait,
                    baseProgress: basePct,
                    progressSpan: spanPct,
                    stageTitle: $"阶段 2/3 [降速测试 {pwm}%]",
                    progress,
                    ct);

                foreach (var f in activeFans)
                {
                    if (f.CurrentRpm >= 110)
                    {
                        minKeepSpinPwm[f.Id] = pwm;
                        minKeepSpinRpm[f.Id] = f.CurrentRpm;
                    }
                    else if (pwm == 0)
                    {
                        stoppedAtZero.Add(f.Id);
                    }
                }
            }

            // ==================== 阶段 3：从静止逐步升速测定真实起转阈值 (Start %) ====================
            var fansToStart = activeFans.Where(f => stoppedAtZero.Contains(f.Id)).ToList();
            if (fansToStart.Count > 0)
            {
                int[] upSteps = [12, 18, 24, 30, 36, 42, 50];
                for (int i = 0; i < upSteps.Length; i++)
                {
                    // 如果所有停转的风扇都已经成功起转，则提前结束阶段 3
                    if (fansToStart.All(f => startUpPwm[f.Id] < 100))
                        break;

                    int pwm = upSteps[i];
                    double basePct = 70 + i * (28.0 / upSteps.Length);
                    double spanPct = 28.0 / upSteps.Length;

                    SetPwmBatch(fansToStart, pwm);
                    await WaitUntilRpmStabilizesAsync(
                        fansToStart,
                        minWaitMs: 5500,
                        maxWaitMs: 9500,
                        baseProgress: basePct,
                        progressSpan: spanPct,
                        stageTitle: $"阶段 3/3 [静止起转测试 {pwm}%]",
                        progress,
                        ct);

                    foreach (var f in fansToStart)
                    {
                        if (startUpPwm[f.Id] >= 100 && f.CurrentRpm >= 140)
                        {
                            startUpPwm[f.Id] = pwm;
                        }
                    }
                }
            }

            // ==================== 汇总保存校准数据 ====================
            foreach (var f in activeFans)
            {
                double stopPwm = minKeepSpinPwm[f.Id];
                double startPwm = stoppedAtZero.Contains(f.Id)
                    ? Math.Min(startUpPwm[f.Id], 55)
                    : stopPwm;

                // 加上 2% 安全裕量防止临界抖动
                double safeStartPwm = Math.Clamp(startPwm == 0 ? 18 : startPwm + 2, 15, 60);

                f.CalibratedStopPwm = stopPwm;
                f.CalibratedMinPwm = safeStartPwm;
                f.CalibratedMinRpm = minKeepSpinRpm[f.Id];
                f.IsCalibrated = true;

                if (f.LowLoadSpeed < safeStartPwm)
                {
                    f.LowLoadSpeed = safeStartPwm;
                }
            }

            progress.Report((100, $"深度校准完成：已测定 {activeFans.Count} 把风扇的起转/停转阈值与最高转速"));
        }
        finally
        {
            IsCalibrating = false;
            UpdateReadings(targetFans, applyControl: true);
        }
    }

    private void SetPwmBatch(IEnumerable<FanChannelViewModel> fans, float pwm)
    {
        foreach (var f in fans)
        {
            f.TargetPwm = pwm;
            if (_controlSensors.TryGetValue(f.Id, out ISensor? ctrl) && ctrl.Control != null)
            {
                try
                {
                    ctrl.Control.SetSoftware(pwm);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// 等待风扇克服叶片转动惯性并达到转速稳定（至少等待 minWaitMs，若转速仍在变化则继续等待至稳定或达到 maxWaitMs）
    /// </summary>
    private async Task WaitUntilRpmStabilizesAsync(
        IReadOnlyList<FanChannelViewModel> fans,
        int minWaitMs,
        int maxWaitMs,
        double baseProgress,
        double progressSpan,
        string stageTitle,
        IProgress<(double percent, string message)> progress,
        CancellationToken ct)
    {
        int elapsed = 0;
        const int sampleInterval = 750;
        var prevRpms = fans.ToDictionary(f => f.Id, f => f.CurrentRpm);
        int stableCount = 0;

        while (elapsed < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(sampleInterval, ct);
            elapsed += sampleInterval;

            UpdateReadings(fans, applyControl: false);

            // 检查所有被测风扇的转速变化率是否已经收敛 (< 18 RPM)
            bool allConverged = true;
            foreach (var f in fans)
            {
                double prev = prevRpms.GetValueOrDefault(f.Id, 0);
                double diff = Math.Abs(f.CurrentRpm - prev);
                prevRpms[f.Id] = f.CurrentRpm;

                if (diff > 18)
                {
                    allConverged = false;
                }
            }

            if (allConverged)
                stableCount++;
            else
                stableCount = 0;

            double subRatio = Math.Clamp((double)elapsed / maxWaitMs, 0.0, 1.0);
            double currentPct = baseProgress + subRatio * progressSpan;

            string rpmPreview = fans.Count == 1
                ? $"当前 {fans[0].CurrentRpm:F0} RPM"
                : $"最高 {fans.Max(x => x.CurrentRpm):F0} RPM";

            string stateNote = elapsed < minWaitMs
                ? "等待叶片加减速..."
                : (stableCount >= 2 ? "转速已稳定" : "等待惯性收敛...");

            progress.Report((currentPct, $"{stageTitle} · {rpmPreview} ({stateNote})"));

            // 已经超过最短等待时间，且连续两次采样转速波动小于 18 RPM，判定已稳定
            if (elapsed >= minWaitMs && stableCount >= 2)
            {
                break;
            }
        }
    }

    public void RestoreAllFansToBiosDefault()
    {
        foreach (ISensor ctrlSensor in _controlSensors.Values)
        {
            try
            {
                ctrlSensor.Control?.SetDefault();
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_isOpen)
        {
            try
            {
                _computer.Close();
            }
            catch
            {
            }
            _isOpen = false;
        }
    }
}

public static class ConfigStorage
{
    private static readonly string ConfigFilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "fan_config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch
        {
        }
    }
}

/// <summary>
/// 开机自启管理服务（通过 Windows 任务计划程序实现最高权限静默自启，无需开机弹出 UAC 确认框）
/// </summary>
public static class StartupManager
{
    private const string TaskName = "FanController_AutoStart";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enable, out string statusMessage)
    {
        try
        {
            if (!enable)
            {
                var delPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Delete /F /TN \"{TaskName}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var delProc = System.Diagnostics.Process.Start(delPsi);
                delProc?.WaitForExit(4000);
                statusMessage = "已取消开机自动启动";
                return true;
            }

            string workDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string exePath = Path.Combine(workDir, "FanController.exe");
            if (!File.Exists(exePath) && !string.IsNullOrEmpty(Environment.ProcessPath))
            {
                exePath = Environment.ProcessPath;
            }

            string xmlContent = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT3S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{System.Security.SecurityElement.Escape(exePath)}</Command>
      <Arguments>--minimized</Arguments>
      <WorkingDirectory>{System.Security.SecurityElement.Escape(workDir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";

            string tempXml = Path.Combine(Path.GetTempPath(), "FanController_AutoStart.xml");
            File.WriteAllText(tempXml, xmlContent, System.Text.Encoding.Unicode);

            var createPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /F /TN \"{TaskName}\" /XML \"{tempXml}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var createProc = System.Diagnostics.Process.Start(createPsi);
            createProc?.WaitForExit(5000);

            try { File.Delete(tempXml); } catch { }

            if (createProc?.ExitCode == 0)
            {
                statusMessage = "已开启开机自启 (登录系统后以管理员权限静默驻留托盘)";
                return true;
            }

            statusMessage = "设置开机自启失败：请先确保以管理员身份运行本软件";
            return false;
        }
        catch (Exception ex)
        {
            statusMessage = $"设置开机自启异常: {ex.Message}";
            return false;
        }
    }
}
