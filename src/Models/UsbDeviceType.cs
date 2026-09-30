using System;

namespace ExHyperV.Models
{
    /// <summary>功能设备可能同时包含多个 USB 接口类型。</summary>
    [Flags]
    public enum UsbDeviceType
    {
        Unknown = 0,
        Keyboard = 1 << 0,
        Mouse = 1 << 1,
        Camera = 1 << 2,
        Audio = 1 << 3,
        Storage = 1 << 4,
        Network = 1 << 5,
        Printer = 1 << 6,
        Serial = 1 << 7,
        SmartCard = 1 << 8,
        GameController = 1 << 9,
        HumanInterface = 1 << 10,
        Bluetooth = 1 << 11,
        Composite = 1 << 12
    }
}
