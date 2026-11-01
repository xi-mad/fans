# FanController - 机箱与显卡风扇智能负载温控中心

基于 **C# (.NET 10 + WPF)** 与 **[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)** 开发的轻量级 Windows 电脑风扇智能温控工具，支持台式机主板 Super I/O PWM 风扇通道与 NVIDIA / AMD / Intel 独立显卡风扇。

## 核心功能

- **多通道硬件自动识别**：自动扫描主板（CPU_FAN、CHA_FAN、AIO_PUMP 等）及独立显卡的全部可控风扇通道。
- **四段式智能负载温控**：每个风扇可独立设置 **低负载（待机）**、**中负载（日常）**、**高负载（游戏/渲染）**、**满载（高温保护）** 的温度阈值与目标转速百分比，区间内线性平滑插值。
- **防抖迟滞与平滑步进**：内置温度 EMA 滤波、降温迟滞（Hysteresis）与加减速步进限制，避免温度小幅波动时风扇忽快忽慢。
- **三阶段深度风扇校准**：动态监测叶片转速收敛，自动测定每把风扇的 **100% 满载转速**、**降速停转阈值（Stop %）** 与 **静止起转阈值（Start %）**。
- **开机静默自启与安全兜底**：支持通过 Windows 任务计划程序实现最高权限无弹窗开机自启并静默驻留系统托盘；退出软件时可自动恢复主板 BIOS 默认控制。

## 编译与运行

```bash
dotnet build -c Release
```

> **注意**：读写主板 Super I/O 芯片需要管理员权限（项目已通过 `app.manifest` 配置自动请求管理员权限）。
