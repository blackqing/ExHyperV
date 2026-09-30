namespace ExHyperV.Models
{
    /// <summary>USB 设备原始数据（来自 usbipd-win 列表查询，每次刷新重建，不可变）。</summary>
    public class UsbDevice
    {
        public string BusId { get; init; } = string.Empty;
        public string VidPid { get; init; } = string.Empty;
        /// <summary>原始 USB 节点的系统名称；不与总线描述混合选择。</summary>
        public string Name { get; init; } = string.Empty;
        public string BusReportedDescription { get; init; } = string.Empty;
        public string InstanceId { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public UsbDeviceType DeviceType { get; init; }
    }
}
