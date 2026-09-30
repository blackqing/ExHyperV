using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    /// <summary>USB 设备及其当前分配目标。</summary>
    public partial class UsbDeviceViewModel : ObservableObject
    {
        public string BusId { get; }

        // 手机切换 USB 模式时，描述和 VID/PID 可能变化。
        [ObservableProperty] private string _vidPid = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Name), nameof(SecondaryName), nameof(HasSecondaryName))]
        private string _systemName = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Name), nameof(SecondaryName), nameof(HasSecondaryName))]
        private string _busReportedDescription = string.Empty;
        [ObservableProperty] private string _instanceId = string.Empty;
        [ObservableProperty] private string _description = string.Empty;
        [ObservableProperty] private string _state = string.Empty;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IconGlyph), nameof(UsesSegoeIcon))]
        private UsbDeviceType _deviceType;
        [ObservableProperty] private string _typeDisplayName = string.Empty;

        [ObservableProperty] private string _currentAssignment;

        public ObservableCollection<string> AssignmentOptions { get; } = new();

        public string Name => !string.IsNullOrWhiteSpace(SystemName)
            ? SystemName
            : !string.IsNullOrWhiteSpace(BusReportedDescription)
                ? BusReportedDescription
                : Properties.Resources.Common_Unknown;

        public string SecondaryName => !string.IsNullOrWhiteSpace(SystemName) &&
            !string.Equals(SystemName, BusReportedDescription, StringComparison.OrdinalIgnoreCase)
                ? BusReportedDescription
                : string.Empty;

        public bool HasSecondaryName => !string.IsNullOrWhiteSpace(SecondaryName);

        public string IconGlyph => DeviceIcons.GetUsbIcon(DeviceType).Glyph;
        public bool UsesSegoeIcon => DeviceIcons.GetUsbIcon(DeviceType).UsesSegoe;

        public UsbDeviceViewModel(UsbDevice model, List<string> runningVmNames)
        {
            BusId = model.BusId;
            UpdateDevice(model);
            _currentAssignment = Properties.Resources.UsbDevice_Host;

            UpdateOptions(runningVmNames);
        }

        public void UpdateDevice(UsbDevice model)
        {
            VidPid = model.VidPid;
            SystemName = model.Name?.Trim() ?? string.Empty;
            BusReportedDescription = model.BusReportedDescription?.Trim() ?? string.Empty;
            InstanceId = string.IsNullOrWhiteSpace(model.InstanceId) ? model.VidPid : model.InstanceId;
            Description = model.Description;
            State = model.State;
            DeviceType = model.DeviceType;
            TypeDisplayName = FormatDeviceType(model.DeviceType);
        }

        public void UpdateOptions(List<string> runningVmNames)
        {
            var current = CurrentAssignment;

            AssignmentOptions.Clear();
            AssignmentOptions.Add(Properties.Resources.UsbDevice_Host);
            foreach (var name in runningVmNames)
            {
                AssignmentOptions.Add(name);
            }

            if (AssignmentOptions.Contains(current))
                CurrentAssignment = current;
            else
                CurrentAssignment = Properties.Resources.UsbDevice_Host;
        }

        public static string FormatDeviceType(UsbDeviceType type)
        {
            var labels = new List<string>();
            AddLabel(UsbDeviceType.Keyboard, Properties.Resources.UsbDevice_TypeKeyboard);
            AddLabel(UsbDeviceType.Mouse, Properties.Resources.UsbDevice_TypeMouse);
            AddLabel(UsbDeviceType.GameController, Properties.Resources.UsbDevice_TypeGameController);
            AddLabel(UsbDeviceType.Camera, Properties.Resources.UsbDevice_TypeCamera);
            AddLabel(UsbDeviceType.Audio, Properties.Resources.UsbDevice_TypeAudio);
            AddLabel(UsbDeviceType.Storage, Properties.Resources.UsbDevice_TypeStorage);
            AddLabel(UsbDeviceType.Network, Properties.Resources.UsbDevice_TypeNetwork);
            AddLabel(UsbDeviceType.Printer, Properties.Resources.UsbDevice_TypePrinter);
            AddLabel(UsbDeviceType.Serial, Properties.Resources.UsbDevice_TypeSerial);
            AddLabel(UsbDeviceType.SmartCard, Properties.Resources.UsbDevice_TypeSmartCard);

            if ((type & UsbDeviceType.HumanInterface) != 0 &&
                (type & (UsbDeviceType.Keyboard | UsbDeviceType.Mouse |
                         UsbDeviceType.GameController)) == 0)
                labels.Add(Properties.Resources.UsbDevice_TypeHid);

            return labels.Count == 0
                ? Properties.Resources.UsbDevice_TypeUnknown
                : string.Join(Properties.Resources.UsbDevice_TypeSeparator, labels);

            void AddLabel(UsbDeviceType flag, string label)
            {
                if ((type & flag) != 0) labels.Add(label);
            }
        }
    }
}
