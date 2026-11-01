using System.Windows;
using FanController.Services;

namespace FanController;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--dump-sensors", StringComparer.OrdinalIgnoreCase))
        {
            using var svc = new HardwareFanService();
            var fans = svc.InitializeAndScanFans();
            Console.WriteLine($"IsAdmin: {svc.IsAdministrator}");
            Console.WriteLine($"MB: {svc.MotherboardName} | CPU: {svc.CpuName} ({svc.CpuTemp:F1}°C, {svc.CpuLoad:F0}%) | GPU: {svc.GpuName} ({svc.GpuTemp:F1}°C, HotSpot {svc.GpuHotSpotTemp:F1}°C, {svc.GpuLoad:F0}%)");
            Console.WriteLine($"Detected Controllable Fans: {fans.Count}");
            foreach (var f in fans)
            {
                Console.WriteLine($"  - [{f.Id}] {f.CustomName} | HW: {f.HardwareName} | RPM: {f.CurrentRpm:F0} | PWM: {f.CurrentPwm:F0}%");
            }
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }
}
