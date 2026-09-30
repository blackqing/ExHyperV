using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    public partial class USBPageViewModel : PageViewModelBase
    {
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private readonly object _viewGate = new();
        private CancellationTokenSource? _viewCts;

        [ObservableProperty] private bool _isLoading;
        [ObservableProperty] private bool _isUiEnabled = true;

        public ObservableCollection<UsbDeviceViewModel> Devices { get; } = new();

        public USBPageViewModel()
        {
            UsbVmbusService.EnsureWatchdogStarted();
            _ = LoadDataAsync();
        }

        public void StartViewMonitoring()
        {
            lock (_viewGate)
            {
                if (_viewCts != null) return;
                _viewCts = new CancellationTokenSource();
                _ = SyncDevicesLoopAsync(_viewCts.Token);
            }
        }

        public void StopViewMonitoring()
        {
            CancellationTokenSource? cts;
            lock (_viewGate)
            {
                cts = _viewCts;
                _viewCts = null;
            }
            cts?.Cancel();
            cts?.Dispose();
        }

        [RelayCommand]
        private async Task LoadDataAsync()
        {
            IsLoading = true;
            try
            {
                await RefreshListInternal();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] Refresh failed; retaining current device state: {ex.Message}");
            }
            finally { IsLoading = false; }
        }

        private async Task SyncDevicesLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(3000, ct);
                    try
                    {
                        var operation = App.Current.Dispatcher.InvokeAsync(
                            async () =>
                            {
                                RefreshConnectionStates();
                                try { await RefreshListInternal(); }
                                finally { RefreshConnectionStates(); }
                            }, DispatcherPriority.Background);
                        await operation.Task.Unwrap();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        // One failed WMI/usbipd enumeration must not permanently
                        // stop status monitoring and leave a stale VM assignment.
                        Debug.WriteLine($"[ExHyperV-USB] Device refresh failed; will retry: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] Device monitor stopped: {ex.Message}");
            }
        }

        private async Task RefreshListInternal()
        {
            await _refreshGate.WaitAsync();
            try
            {
                // A failed WMI/usbipd query throws and leaves the existing UI/state intact.
                var vms = await UsbVmbusService.GetRunningVMsAsync();
                var usbDevices = await UsbVmbusService.GetUsbIpDevicesAsync();
                var vmNames = vms.Select(v => v.Name).ToList();
                var newBusIds = usbDevices.Select(d => d.BusId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var activeSnapshot = UsbVmbusService.ActiveTunnels.ToArray();

                var cleanupBusIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var device in Devices.Where(device => !newBusIds.Contains(device.BusId)))
                    cleanupBusIds.Add(device.BusId);
                foreach (var entry in activeSnapshot)
                {
                    // A disappeared USB device or a stopped target VM must not be
                    // resurrected by the watchdog.
                    if (!newBusIds.Contains(entry.Key) ||
                        !vmNames.Contains(entry.Value, StringComparer.OrdinalIgnoreCase))
                        cleanupBusIds.Add(entry.Key);
                }

                var cleanupTasks = new List<Task>();
                foreach (var busId in cleanupBusIds)
                {
                    UsbVmbusService.ClearDesiredTunnel(busId);
                    cleanupTasks.Add(StopTunnelObservedAsync(busId));
                    var item = Devices.FirstOrDefault(device => device.BusId == busId);
                    if (item != null) Devices.Remove(item);
                }

                foreach (var dev in usbDevices)
                {
                    var existing = Devices.FirstOrDefault(d => d.BusId == dev.BusId);
                    if (existing != null)
                    {
                        existing.UpdateDevice(dev);
                        existing.UpdateOptions(vmNames);
                    }
                    else
                    {
                        Devices.Add(new UsbDeviceViewModel(dev, vmNames));
                    }
                }

                RefreshConnectionStates();

                // Observe every stop initiated by refresh; no cleanup is left as an
                // untracked fire-and-forget task.
                await Task.WhenAll(cleanupTasks);
            }
            finally { _refreshGate.Release(); }
        }

        private void RefreshConnectionStates()
        {
            foreach (var device in Devices)
            {
                if (UsbVmbusService.IsTunnelCleanupFailed(device.BusId))
                    device.CurrentAssignment = Properties.Resources.USBPageViewModel_CleanupFailed;
                else if (UsbVmbusService.IsGuestConnectionUnhealthy(device.BusId))
                    device.CurrentAssignment = Properties.Resources.USBPageViewModel_GuestUnavailable;
                else if (UsbVmbusService.ActiveTunnels.TryGetValue(device.BusId, out var vm) &&
                    UsbVmbusService.IsTunnelEstablished(device.BusId, vm) &&
                    UsbVmbusService.IsUsbIpDeviceAttached(device.State))
                    device.CurrentAssignment = vm;
                else if (UsbVmbusService.ActiveTunnels.ContainsKey(device.BusId))
                    device.CurrentAssignment = Properties.Resources.USBPageViewModel_Connecting;
                else
                    device.CurrentAssignment = Properties.Resources.UsbDevice_Host;
            }
        }

        private static async Task StopTunnelObservedAsync(string busId)
        {
            try
            {
                await UsbVmbusService.StopTunnelAsync(busId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] Cleanup failed for {busId}: {ex.Message}");
            }
        }

        private static async Task<bool> ReturnDeviceToHostAsync(string busId)
        {
            UsbVmbusService.ClearDesiredTunnel(busId);
            try
            {
                return await UsbVmbusService.StopTunnelAsync(busId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] Failed to return {busId} to host: {ex.Message}");
                return false;
            }
        }

        [RelayCommand]
        private async Task ChangeAssignmentAsync(object parameter)
        {
            if (parameter is not object[] parameters || parameters.Length < 2 ||
                parameters[0] is not UsbDeviceViewModel deviceVM ||
                parameters[1] is not string selectedTarget) return;

            if (deviceVM.CurrentAssignment == selectedTarget &&
                selectedTarget != Properties.Resources.USBPageViewModel_Connecting) return;

            if (!IsUiEnabled) return;
            IsUiEnabled = false;
            await _refreshGate.WaitAsync();
            bool assignmentRequested = false;
            try
            {
                if (deviceVM.CurrentAssignment == selectedTarget &&
                    selectedTarget != Properties.Resources.USBPageViewModel_Connecting) return;

                if (selectedTarget == Properties.Resources.UsbDevice_Host)
                {
                    var stopped = await ReturnDeviceToHostAsync(deviceVM.BusId);
                    deviceVM.CurrentAssignment = stopped
                        ? Properties.Resources.UsbDevice_Host
                        : Properties.Resources.USBPageViewModel_CleanupFailed;
                }
                else
                {
                    if (!UsbVmbusService.ActiveTunnels.ContainsKey(deviceVM.BusId) &&
                        !UsbVmbusService.IsUsbIpDeviceAssignable(deviceVM.State))
                    {
                        Debug.WriteLine($"[ExHyperV-USB] Refusing assignment of {deviceVM.BusId}; usbipd state is '{deviceVM.State}'.");
                        return;
                    }

                    UsbVmbusService.SetDesiredTunnel(deviceVM.BusId, selectedTarget);
                    assignmentRequested = true;
                    deviceVM.CurrentAssignment = Properties.Resources.USBPageViewModel_Connecting;
                    var connected = await UsbVmbusService.AutoRecoverTunnel(
                        deviceVM.BusId, selectedTarget);
                    if (connected && UsbVmbusService.IsTunnelEstablished(deviceVM.BusId, selectedTarget))
                        deviceVM.CurrentAssignment = selectedTarget;
                    else
                    {
                        var stopped = await ReturnDeviceToHostAsync(deviceVM.BusId);
                        deviceVM.CurrentAssignment = stopped
                            ? Properties.Resources.UsbDevice_Host
                            : Properties.Resources.USBPageViewModel_CleanupFailed;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (assignmentRequested)
                {
                    var stopped = await ReturnDeviceToHostAsync(deviceVM.BusId);
                    deviceVM.CurrentAssignment = stopped
                        ? Properties.Resources.UsbDevice_Host
                        : Properties.Resources.USBPageViewModel_CleanupFailed;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] Assignment change failed: {ex.Message}");
                if (assignmentRequested)
                {
                    var stopped = await ReturnDeviceToHostAsync(deviceVM.BusId);
                    deviceVM.CurrentAssignment = stopped
                        ? Properties.Resources.UsbDevice_Host
                        : Properties.Resources.USBPageViewModel_CleanupFailed;
                }
            }
            finally
            {
                IsUiEnabled = true;
                _refreshGate.Release();
            }
        }

        [RelayCommand]
        private void OpenUrl(string url) => Shell.OpenUrl(url);
    }
}
