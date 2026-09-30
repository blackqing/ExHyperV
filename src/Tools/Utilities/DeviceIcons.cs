using Wpf.Ui.Controls;
using ExHyperV.Models;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 把设备类型映射到 Segoe Fluent 图标 glyph，并构造对应的 FontIcon UI 元素。
    /// </summary>
    public static class DeviceIcons
    {
        /// <summary>USB 图标及其字体必须成对选择；复合设备不按功能优先级抢图标。</summary>
        public static (string Glyph, bool UsesSegoe) GetUsbIcon(UsbDeviceType type)
        {
            // HID 是键鼠/手柄的上层类别，不算额外功能。
            if ((type & (UsbDeviceType.Keyboard | UsbDeviceType.Mouse | UsbDeviceType.GameController)) != 0)
                type &= ~UsbDeviceType.HumanInterface;

            var flags = (uint)type;
            if ((type & UsbDeviceType.Composite) != 0 || (flags & (flags - 1)) != 0)
                return Fluent(SymbolRegular.UsbPlug24);

            return type switch
            {
                UsbDeviceType.Mouse => ("\uE962", true),
                UsbDeviceType.Audio => ("\uE994", true),
                UsbDeviceType.Network => ("\uE839", true),
                UsbDeviceType.Storage => ("\uEDA2", true),
                UsbDeviceType.Keyboard => Fluent(SymbolRegular.Keyboard24),
                UsbDeviceType.Camera => Fluent(SymbolRegular.Camera24),
                UsbDeviceType.Printer => Fluent(SymbolRegular.Print24),
                UsbDeviceType.Bluetooth => Fluent(SymbolRegular.Bluetooth24),
                UsbDeviceType.Serial => Fluent(SymbolRegular.SerialPort24),
                _ => Fluent(SymbolRegular.UsbPlug24)
            };

            static (string, bool) Fluent(SymbolRegular symbol) => (char.ConvertFromUtf32((int)symbol), false);
        }

        /// <summary>根据设备类型/友好名返回单字符 Segoe Fluent glyph。</summary>
        public static string GetGlyph(string deviceType, string friendlyName)
        {
            switch (deviceType)
            {
                case "Switch":
                    return "\xF597";
                case "Upstream":
                    return "";
                case "Display":
                    return "\xF211";
                case "Net":
                    return "\xE839";
                case "USB":
                    return friendlyName.Contains("USB4")
                        ? "\xE945"
                        : "\xECF0";
                case "HIDClass":
                    return "\xE928";
                case "SCSIAdapter":
                case "HDC":
                    return "\xEDA2";
                case "ComputeAccelerator": // NPU/AI 加速器（Intel AI Boost / AMD Ryzen AI / 高通 Hexagon 在 Windows 上均归此 PnP 类）
                    return "\xEEA1";       // Segoe Fluent “CPU” 处理器图标
                default:
                    return friendlyName.Contains("Audio")
                        ? "\xE995"
                        : "\xE950";
            }
        }

        /// <summary>构造一个 FontIcon 控件，glyph 来自 <see cref="GetGlyph"/>。</summary>
        public static FontIcon CreateFontIcon(string classType, string friendlyName)
        {
            return new FontIcon
            {
                FontSize = 24,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = GetGlyph(classType, friendlyName)
            };
        }
    }
}
