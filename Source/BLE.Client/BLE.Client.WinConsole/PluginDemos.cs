using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.Extensions;
using Plugin.BLE.Extensions;
using Plugin.BLE.Windows;
using ProtoBuf.Meta;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using static BLE.Client.WinConsole.PluginDemos;
using static System.Runtime.InteropServices.JavaScript.JSType;

/*
 * This is the Cygnus C1Ex Discover and Connect Demo.
 * By DMG.
 * */

namespace BLE.Client.WinConsole
{
    internal class PluginDemos
    {
        // Test Program options.
        const bool connectToFistC1ExFound = true;
        const bool addDiscoveredC1ExDevicesToList = true;
        const bool scanFilterForC1ExUUIDs = true;

        // Cygnus C1Ex GUID or UUID
        private readonly Guid cygnusServiceGuid = new Guid("DE670901-8025-4C69-A40E-ECCD60563713");

        // See method AdvertisementReceived() for displaying BLE device info when discovered.


        private readonly IBluetoothLE bluetoothLE;
        public IAdapter Adapter { get; }
        private readonly Action<string, object[]>? writer;
        private readonly List<IDevice> discoveredDevices;
        private bool scanningDone = false;
        private ConsoleKey consoleKey = ConsoleKey.None;
        private IDevice? reconnectDevice;
        private CancellationTokenSource escKeyCancellationTokenSource = new CancellationTokenSource();
               

        public PluginDemos(Action<string, object[]>? writer = null)
        {
            discoveredDevices = new List<IDevice>();
            bluetoothLE = CrossBluetoothLE.Current;
            Adapter = CrossBluetoothLE.Current.Adapter;
            Adapter.DeviceConnected += Adapter_DeviceConnected;
            Adapter.DeviceDisconnected += Adapter_DeviceDisconnected;
            Adapter.DeviceConnectionLost += Adapter_DeviceConnectionLost;
            Adapter.DeviceConnectionError += Adapter_DeviceConnectionError;
            this.writer = writer;
        }

        private void Adapter_DeviceConnectionError(object? sender, Plugin.BLE.Abstractions.EventArgs.DeviceErrorEventArgs e)
        {
            Write($"Adapter_DeviceConnectionError {e.Device.Id.ToHexBleAddress()} with name: {e.Device.Name}");
        }

        private void Adapter_DeviceDisconnected(object? sender, Plugin.BLE.Abstractions.EventArgs.DeviceEventArgs e)
        {
            Write($"Adapter_DeviceDisconnected {e.Device.Id.ToHexBleAddress()} with name: {e.Device.Name}");
        }

        private void Adapter_DeviceConnected(object? sender, Plugin.BLE.Abstractions.EventArgs.DeviceEventArgs e)
        {
            Write($"Adapter_DeviceConnected {e.Device.Id.ToHexBleAddress()} with name:  {e.Device.Name}");
        }

        private void Write(string format, params object[] args)
        {
            writer?.Invoke(format, args);
        }

        public async Task TurnBluetoothOn()
        {
            await bluetoothLE.TrySetStateAsync(true);
        }

        public async Task TurnBluetoothOff()
        {
            await bluetoothLE.TrySetStateAsync(false);
        }

        public IDevice ConnectToKnown(Guid id)
        {
            IDevice dev = Adapter.ConnectToKnownDeviceAsync(id).Result;
            return dev;
        }

        public async Task Connect_Disconnect()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.ThroughputOptimized);
            IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id, connectParameters);
            Write("Waiting 5 secs");
            await Task.Delay(5000);
            Write("Disconnecting");
            await Adapter.DisconnectDeviceAsync(dev);
            dev.Dispose();
            Write("Test_Connect_Disconnect done");
        }

        public async Task ShowBondState()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id);
            Write("BondState: " + dev.BondState);
            dev.Dispose();
        }


        public delegate Task<int> CharWriteDataAsync(byte[] data, CancellationToken cancellationToken = default);
        public CharWriteDataAsync charWriteDataAsync;

        /// <summary>
        /// Discover C1Ex gauges and connect to the first one found.
        /// </summary>
        /// <returns></returns>
        public async Task DiscoverC1ExConnectAndGetNotifications()
        {
            await DoTheScanningForC1ExDevices(ScanMode.LowPower, 2000);

            if (connectToFistC1ExFound)
            {
                // If we found a device, try and connect to it.AdvReceived
                if (discoveredDevices.Count > 0)
                {
                    const int index = 0;
                    bool foundCygnusService = false;
                    consoleKey = ConsoleKey.None;
                    new Task(ConsoleKeyReader).Start();

                    Write($"Found {discoveredDevices.Count} C1Ex Devices");

                    //
                    // Connect to the Device.
                    //
                    var id = discoveredDevices[index].Id;
                    var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
                    await Task.Delay(100);
                    IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id, connectParameters);

                    //
                    // Read the all the Services from the connected Cygnus1Ex device.
                    // Subscribe to Characteristic with Notify.
                    //
                    await Task.Delay(500);
                    Write($"GetServicesAsync");
                    var services = await dev.GetServicesAsync();
                    Write($"Found {services.Count} Services");
                    List<ICharacteristic> charlist = new List<ICharacteristic>();
                    foreach (var service in services)
                    {
                        if (service.Id == cygnusServiceGuid)
                        {
                            Write($"C1Ex Service ID = {service.Id}");
                            foundCygnusService = true;

                            //
                            // Read all the Service Characteristics and subscribe to the one with Notify.
                            //
                            var characteristics = await service.GetCharacteristicsAsync();
                            charlist.AddRange(characteristics);
                            foreach (Characteristic characteristic in characteristics)
                            {
                                if (characteristic.Properties.HasFlag(CharacteristicPropertyType.Write))
                                {
                                    Write($"Write Characteristic ID = {characteristic.Id}");
                                    charWriteDataAsync = characteristic.WriteAsync;
                                }

                                if (characteristic.Properties.HasFlag(CharacteristicPropertyType.Notify))
                                {
                                    Write($"Notify Characteristic ID = {characteristic.Id}");
                                    try
                                    {
                                        await characteristic.StartUpdatesAsync();
                                        characteristic.ValueUpdated += (sender, args) =>
                                        {
                                            //
                                            // When we receive a BLE Message object, first deserialize the Message Header so we can
                                            // decide what kind of message to deseruialize next.
                                            // Need to convert the byte[] to a stream so we can pull data from it.
                                            //
                                            Plugin.BLE.Abstractions.EventArgs.CharacteristicUpdatedEventArgs eventArgs = args;

                                            // Display the ASCII data.
                                            //byte[] ba = eventArgs.Characteristic.Value;
                                            //Write($"!Characteristic.Value: {ba} Len={ba.Length}");
                                            //string asciiString = Encoding.ASCII.GetString(ba, 0, ba.Length);
                                            //Write($"!Notify Characteristic.Value: {asciiString}");

                                            //
                                            // Deserialize the MessageHeader first so we know the commandType.
                                            // The protobuf MessageHeader is always 5 bytes long.
                                            //
                                            const int headerSize = 5;
                                            using var chs = new MemoryStream(eventArgs.Characteristic.Value, 0, headerSize);
                                            var messageHeader = ProtoBuf.Serializer.Deserialize<Cygnus.MessageHeader>(chs);
                                            Write($"commandType = {messageHeader.commandType}");
                                            switch (messageHeader.commandType)
                                            {
                                                case Cygnus.CommandType.GetRecordList:
                                                    {
                                                        Write($"Message Record List");

                                                        // Deserialize the Message payload from the buffer AFTER the MessageHeader.
                                                        int balen = eventArgs.Characteristic.Value.Length;
                                                        using var rls = new MemoryStream(eventArgs.Characteristic.Value, headerSize, balen - headerSize);
                                                        var messageRecordList = ProtoBuf.Serializer.Deserialize<Cygnus.MessageRecordList>(rls);

                                                        Write($"numRecords = {messageRecordList.numRecords}");
                                                        foreach (var rn in messageRecordList.recordListItems)
                                                        {
                                                            Write($"RecordName = {rn.recordName} RecordType = {rn.recordType} Req. = {rn.numPointsRequired} Taken = {rn.numPointsTaken}");
                                                        }
                                                        break;
                                                    }

                                                default:
                                                    {
                                                        Write($"Message Error");
                                                        break;
                                                    }
                                            }
                                        };
                                    }
                                    catch (Exception)
                                    {
                                    }
                                }
                            }


                        }
                    }


                    if (foundCygnusService)
                    {
                        while (true)
                        {
                            await Task.Delay(100);

                            if (consoleKey == ConsoleKey.S)
                            {
                                consoleKey = ConsoleKey.Spacebar;
                                // Send N bytes of data to the device.

                                Write($"Sending a CommandHeader to the C1Ex via protobuf..");
                                MemoryStream ms = new MemoryStream();
                                Cygnus.CommandHeader commandHeader = new Cygnus.CommandHeader { commandType = Cygnus.CommandType.GetRecordList };
                                ProtoBuf.Serializer.Serialize<Cygnus.CommandHeader>(ms, commandHeader);
                                await charWriteDataAsync( ms.GetBuffer() );
                                Write($"CommandType.GetRecordList");
                                Write($"Sent {ms.Position} bytes");

                                //const int numBytes = 500;
                                //byte[] bytes = Enumerable.Repeat((byte)0x43, numBytes).ToArray();
                                //Write($"Sending {numBytes} bytes to the C1Ex..");
                                //await charWriteDataAsync(bytes);
                                //Write($"Sent");
                            }

                            if (consoleKey == ConsoleKey.Escape)
                            {
                                break;
                            }
                        }

                        Write($"Disconnecting from C1Ex Device");

                        foreach (var service in services)
                        {
                            service.Dispose();
                        }
                        foreach (Characteristic characteristic in charlist)
                        {
                            try
                            {
                                await characteristic.StopUpdatesAsync();
                            }
                            catch { }
                        }
                        await Task.Delay(1500);
                        await Adapter.DisconnectDeviceAsync(dev);
                    }
                }
                else
                {
                    Write($"Found {discoveredDevices.Count} C1Ex Devices");
                }
            }
        }


        public async Task Connect_Read_Services_Disconnect_Loop()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
            new Task(ConsoleKeyReader).Start();
            using (IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id, connectParameters))
            {
                int count = 1;
                while(true)
                {
                    await Task.Delay(100);
                    Write($"---------------- {count++} ------- (Esc to stop) ------");
                    if (dev.State != DeviceState.Connected)
                    {
                        Write("Connecting");
                        await Adapter.ConnectToDeviceAsync(dev);
                    }
                    Write("Reading services");

                    var services = await dev.GetServicesAsync();
                    List<ICharacteristic> charlist = new List<ICharacteristic>();
                    foreach (var service in services)
                    {
                        var characteristics = await service.GetCharacteristicsAsync();
                        charlist.AddRange(characteristics);
                    }

                    foreach (var service in services)
                    {
                        service.Dispose();
                    }
                    charlist.Clear();
                    Write("Waiting 3 secs");
                    await Task.Delay(3000);
                    Write("Disconnecting");
                    await Adapter.DisconnectDeviceAsync(dev);
                    Write("Test_Connect_Disconnect done");
                    if (consoleKey == ConsoleKey.Escape)
                    {
                        break;
                    }
                }
            }
        }

        public async Task Connect_Read_Services_Dispose_Loop()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
            new Task(ConsoleKeyReader).Start();
            int count = 1;
            bool dontDispose = true;
            while (true)
            {
                await Task.Delay(100);
                Write($"---------------- {count++} ------- (Esc to stop) ------");
                IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id, connectParameters);
                Write("Reading services");
                var services = await dev.GetServicesAsync();
                List<ICharacteristic> charlist = new List<ICharacteristic>();
                foreach (var service in services)
                {
                    Write($"ServiceId:{service.Id}");

                    Guid CygnusServiceId = new Guid("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
                    if (service.Id == CygnusServiceId)
                    {
                        Write($"Cygnus Service !");
                    }

                    var characteristics = await service.GetCharacteristicsAsync();
                    charlist.AddRange(characteristics);
                    foreach (Characteristic characteristic in characteristics)
                    {
                        Write($"Characteristic Props:{characteristic.Properties}, " +
                            $"Id:{characteristic.Id}, "+
                            $"RD:{characteristic.CanRead}, "+
                            $"WR:{characteristic.CanWrite}, "+
                            $"CU:{characteristic.CanUpdate}"
                            );
                        
                        if (characteristic.Properties.HasFlag(CharacteristicPropertyType.Indicate)
                            || characteristic.Properties.HasFlag(CharacteristicPropertyType.Notify))
                        {
                            //Write($"Characteristic.Properties: {characteristic.Properties}");
                            try
                            {
                                await characteristic.StartUpdatesAsync();

                                characteristic.ValueUpdated += (sender, args) =>
                                {
                                    Plugin.BLE.Abstractions.EventArgs.CharacteristicUpdatedEventArgs eventArgs = args;
                                    byte[] ba = eventArgs.Characteristic.Value;
                                    Write($"!Characteristic.Value: {ba} Len={ba.Length}");
                                    string asciiString = Encoding.ASCII.GetString(ba, 0, ba.Length);
                                    Write($"!Characteristic.Value: {asciiString}");
                                };

                            } catch { }
                        }
                    }
                }

                while (true)
                {
                    await Task.Delay(3000);
                }

                if (!dontDispose)
                {
                    foreach (var service in services)
                    {
                        service.Dispose();
                    }
                    foreach (Characteristic characteristic in charlist)
                    {
                        try
                        {
                            await characteristic.StopUpdatesAsync();
                        }
                        catch { }
                    }
                    charlist.Clear();
                    Write("Waiting 3 secs");
                    await Task.Delay(3000);
                    await Adapter.DisconnectDeviceAsync(dev);
                    Write("Disposing");
                    dev.Dispose();
                }
            }
        }

        private void Characteristic_ValueUpdated(object? sender, Plugin.BLE.Abstractions.EventArgs.CharacteristicUpdatedEventArgs e)
        {
            throw new NotImplementedException();
        }

        private void ConsoleKeyReader()
        {
            while (consoleKey != ConsoleKey.Escape)
            {
                consoleKey = Console.ReadKey().Key;
            }
            Write("Escape key pressed - stopping...");
            escKeyCancellationTokenSource.Cancel();
        }

        private async Task ConnectWorker(Guid id)
        {
            while (consoleKey != ConsoleKey.Escape)
            {
                try
                {
                    Write("Trying to connect to device (Escape key to abort)");
                    reconnectDevice = await Adapter.ConnectToKnownDeviceAsync(id, cancellationToken: escKeyCancellationTokenSource.Token);
                    Write("Reading all services and characteristics");
                    var services = await reconnectDevice.GetServicesAsync();
                    List<ICharacteristic> characteristics = new List<ICharacteristic>();
                    foreach (var service in services)
                    {
                        var newcharacteristics = await service.GetCharacteristicsAsync();
                        characteristics.AddRange(newcharacteristics);
                    }
                    await Task.Delay(1000);
                    Write(new string('-', 80));
                    Write("Connected successfully!");
                    Write("To test connection lost: Move the device out of range / power off the device");
                    Write(new string('-', 80));
                    break;
                }
                catch
                {
                }
            }
        }

        public async Task Connect_ConnectionLost_Reconnect()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            var consoleReaderTask = new Task(ConsoleKeyReader);
            consoleReaderTask.Start();
            await ConnectWorker(id);
            consoleReaderTask.Wait();
        }

        private async void Adapter_DeviceConnectionLost(object? sender, Plugin.BLE.Abstractions.EventArgs.DeviceErrorEventArgs e)
        {
            Write($"Adapter_DeviceConnectionLost {e.Device.Id.ToHexBleAddress()} with name: {e.Device.Name}");
            if (reconnectDevice is not null && reconnectDevice.Id == e.Device.Id)
            {
                reconnectDevice.Dispose();
                reconnectDevice = null;
                await Task.Delay(1000);
                Write(new string('-', 80));
                Write("Lost connection!");
                Write("To test reconnect: Move the device back in range / power on the device");
                Write(new string('-', 80));
                _ = ConnectWorker(e.Device.Id);
            }
        }

        public async Task Connect_Change_Parameters_Disconnect()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            var connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
            IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id, connectParameters);
            Write("Waiting 5 secs");
            await Task.Delay(5000);
            connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.ThroughputOptimized);
            dev.UpdateConnectionParameters(connectParameters);
            Write("Waiting 5 secs");
            await Task.Delay(5000);
            connectParameters = new ConnectParameters(connectionParameterSet: ConnectionParameterSet.Balanced);
            dev.UpdateConnectionParameters(connectParameters);
            Write("Waiting 5 secs");
            await Task.Delay(5000);
            Write("Disconnecting");
            await Adapter.DisconnectDeviceAsync(dev);
            dev.Dispose();
            Write("Test_Connect_Disconnect done");
        }

        public async Task BondAsync()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id);
            await Adapter.BondAsync(dev);
        }

        public Task GetBondedDevices()
        {
            int idx = 0;
            foreach (var dev in Adapter.BondedDevices)
            {
                Write($"{idx++} Bonded device: {dev.Name} : {dev.Id}");
            }
            return Task.FromResult(true);
        }

        public async Task Pair_Connect_Disconnect()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            var id = bleaddress.ToBleDeviceGuid();
            ulong bleAddressulong = id.ToBleAddress();
            DeviceInformation? deviceInformation = null;
            using (BluetoothLEDevice nativeDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bleAddressulong))
            {
                deviceInformation = await DeviceInformation.CreateFromIdAsync(nativeDevice.DeviceId);
            }

            if (!deviceInformation.Pairing.IsPaired && deviceInformation.Pairing.CanPair)
            {
                Write("Starting custom pairing...");
                deviceInformation.Pairing.Custom.PairingRequested += Custom_PairingRequested;
                DevicePairingResult result = await deviceInformation.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Encryption);
                Write("Pairing result: " + result.Status);
            }
            else
            {
                Write("Already paired");
            }
            Write("Calling Adapter.ConnectToKnownDeviceAsync");
            IDevice dev = await Adapter.ConnectToKnownDeviceAsync(id);
            Write($"Calling Adapter.ConnectToKnownDeviceAsync done with {dev.Name}");
            await Task.Delay(1000);
            await dev.RequestMtuAsync(517);
            Write("Waiting 3 secs");
            await Task.Delay(3000);
            Write("Disconnecting");
            await Adapter.DisconnectDeviceAsync(dev);
            dev.Dispose();
            Write("Custom_Pair_Connect_Disconnect done");
        }

        private void Custom_PairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            Write("Custom_PairingRequested -> Accept");
            args.Accept();
        }

        public async Task DoTheScanning(ScanMode scanMode = ScanMode.LowPower, int time_ms = 4000)
        {

            if (!bluetoothLE.IsOn)
            {
                Write("Bluetooth is not On - it is {0}", bluetoothLE.State);
                return;
            }
            Write("Bluetooth is on");
            Write("Scanning now for " + time_ms + " ms...");
            var cancellationTokenSource = new CancellationTokenSource(time_ms);
            discoveredDevices.Clear();

            int index = 1;
            Adapter.DeviceDiscovered += (s, a) =>
            {
                if (scanningDone)
                {
                    return;
                }
                var dev = a.Device;
                Write($"{index++}: BLE Address=0x{0}, Name={1}", dev.Id.ToHexBleAddress(), dev.Name);
                discoveredDevices.Add(a.Device);
            };
            Adapter.ScanMode = scanMode;
            await Adapter.StartScanningForDevicesAsync(cancellationToken: cancellationTokenSource.Token);
            await Adapter.StopScanningForDevicesAsync();
            scanningDone = true;
        }


        public async Task DoTheScanningForC1ExDevices(ScanMode scanMode = ScanMode.LowPower, int time_ms = 2000)
        {
            if (!bluetoothLE.IsOn)
            {
                Write("Bluetooth is not On - it is {0}", bluetoothLE.State);
                return;
            }
            Write("Scanning for C1Ex devices for " + time_ms + " ms...");
            var cancellationTokenSource = new CancellationTokenSource(time_ms);
            discoveredDevices.Clear();
            scanningDone = false;


            /* Add each discovered C1Ex devices to a list, 
             * but stop scanning when we have found one C1Ex. */
            if (addDiscoveredC1ExDevicesToList)
            {
                int index = 1;
                Adapter.DeviceDiscovered += (s, a) =>
                {
                    if (scanningDone)
                    {
                        return;
                    }
                    var dev = a.Device;
                    Write($"{index++}: BLE Address=0x{dev.Id.ToHexBleAddress()}, Name={dev.Name}");
                    discoveredDevices.Add(a.Device);
                    // Once we find a C1Ex, we can cancel the scan.
                    cancellationTokenSource.Cancel();
                };
                Adapter.ScanMode = scanMode;
            }
            

            /* Filter for Cygnus 1Ex devices with a specific Service UUID.
             * scanFilterOptions.ServiceUuids is actually a "cross platform filter"
             * */
            var scanFilterOptions = new ScanFilterOptions();

            if (scanFilterForC1ExUUIDs)
            {
                scanFilterOptions.ServiceUuids = new[] { cygnusServiceGuid }; // cross platform filter
            }

            await Adapter.StartScanningForDevicesAsync(
                scanFilterOptions,
                deviceFilter: null,
                cancellationToken: cancellationTokenSource.Token
                );

            scanningDone = true;
        }


        internal async Task DiscoverAndSelect()
        {
            if (!bluetoothLE.IsOn)
            {
                Console.WriteLine("Bluetooth is off - cannot discover");
                return;
            }
            await DoTheScanning();
            int index = 1;
            await Task.Delay(200);
            Console.WriteLine();
            foreach (var dev in discoveredDevices)
            {
                Console.WriteLine($"{index++}: BLE Address=0x{dev.Id.ToHexBleAddress()}, Name={dev.Name}");
            }
            if (discoveredDevices.Count == 0)
            {
                Console.Write("NO BLE Devices discovered");
                return;
            }
            Console.WriteLine();
            Console.Write($"Select BLE address index with value {1} to {discoveredDevices.Count}: ");
            if (int.TryParse(Console.ReadLine(), out int selectedIndex))
            {
                IDevice selecteddev = discoveredDevices[selectedIndex - 1];
                Console.WriteLine($"Selected {selectedIndex}: {selecteddev.Id.ToHexBleAddress()} with Name = {selecteddev.Name}");
                BleAddressSelector.SetBleAddress(selecteddev.Id.ToHexBleAddress());
            }
        }

        private void WriteAdvertisementRecords(IDevice device)
        {
            if (device.AdvertisementRecords is null)
            {
                Write("{0} {1} has no AdvertisementRecords...", device.Name, device.State);
                return;
            }
            Write("{0} {1} with {2} AdvertisementRecords", device.Name, device.State, device.AdvertisementRecords.Count);
            foreach (var ar in device.AdvertisementRecords)
            {
                switch (ar.Type)
                {
                    case AdvertisementRecordType.CompleteLocalName:
                        Write(ar.ToString() + " = " + Encoding.UTF8.GetString(ar.Data));
                        break;
                    default:
                        Write(ar.ToString());
                        break;
                }
            }
        }

        /// <summary>
        /// Connect to a device with a specific name
        /// Assumes that DoTheScanning has been called and that the device is advertising 
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public async Task<IDevice?> ConnectTest(string name)
        {
            if (!scanningDone)
            {
                Write("ConnectTest({0}) Failed - Call the DoTheScanning() method first!");
                return null;
            }
            Thread.Sleep(10);
            foreach (var device in discoveredDevices)
            {
                if (device.Name.Contains(name))
                {
                    await Adapter.ConnectToDeviceAsync(device);
                    return device;
                }
            }
            return null;
        }

        public Task RunGetSystemConnectedOrPairedDevices()
        {
            IReadOnlyList<IDevice> devs = Adapter.GetSystemConnectedOrPairedDevices();
            Task.Delay(200);
            Write($"GetSystemConnectedOrPairedDevices found {devs.Count} devices:");
            foreach (var dev in devs)
            {
                Write("{0}: {1}", dev.Id.ToHexBleAddress(), dev.Name);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// This demonstrates a bug where the known services is not cleared at disconnect (2023-11-03)
        /// </summary>        
        public async Task ShowNumberOfServices()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            Write("Connecting to device with address = {0}", bleaddress);
            IDevice dev = await Adapter.ConnectToKnownDeviceAsync(bleaddress.ToBleDeviceGuid()) ?? throw new Exception("null");
            string name = dev.Name;
            Write("Connected to {0} {1} {2}", name, dev.Id.ToHexBleAddress(), dev.State);
            Write("Calling dev.GetServicesAsync()...");
            var services = await dev.GetServicesAsync();
            Write("Found {0} services", services.Count);
            Thread.Sleep(1000);
            Write("Disconnecting from {0} {1}", name, dev.Id.ToHexBleAddress());
            await Adapter.DisconnectDeviceAsync(dev);
            Thread.Sleep(1000);
            Write("ReConnecting to device {0} {1}...", name, dev.Id.ToHexBleAddress());
            await Adapter.ConnectToDeviceAsync(dev);
            Write("Connect Done.");
            Thread.Sleep(1000);
            Write("Calling dev.GetServicesAsync()...");
            services = await dev.GetServicesAsync();
            Write("Found {0} services", services.Count);
            await Adapter.DisconnectDeviceAsync(dev);
            Thread.Sleep(1000);
        }

        internal Task Disconnect(IDevice dev)
        {
            return Adapter.DisconnectDeviceAsync(dev);
        }


    }
}
