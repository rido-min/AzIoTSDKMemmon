using Humanizer;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AzIoTSDKMemmon
{
    public enum DiagnosticsMode
    {
        minimal = 0,
        complete = 1,
        full = 2
    }

    public class Device : BackgroundService
    {
        private readonly ILogger<Device> _logger;
        private readonly IConfiguration _configuration;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int telemetryCounter = 0;
        private int commandCounter = 0;
        private int twinRecCounter = 0;
        private int reconnectCounter = 0;

        private double telemetryWorkingSet = 0;
        private double managedMemory = 0;
        private const bool default_enabled = false;
        private const int default_interval = 500;

        string connectionString;
        private DeviceClient? client = null;

        const string modelId = "dtmi:rido:memmon;2";

        bool Enabled = default_enabled;
        int Interval = default_interval;
        private string infoVersion = string.Empty;
        
        public Device(ILogger<Device> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration; 
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            connectionString = _configuration.GetConnectionString("cs");
            client = DeviceClient.CreateFromConnectionString(connectionString, TransportType.Mqtt_Tcp_Only, new ClientOptions { ModelId = modelId });
            var twin = await client.GetTwinAsync();
            infoVersion = client.ProductInfo;

            await client.SetDesiredPropertyUpdateCallbackAsync(DesiredPropertiesHanlder, null, stoppingToken);
            await client.SetMethodHandlerAsync("getRuntimeStats", Command_getRuntimeStats_Handler, null, stoppingToken);
            await client.SetMethodHandlerAsync("isPrime", Command_isPrime_Handler, null, stoppingToken);
            await client.SetMethodHandlerAsync("malloc", Command_malloc_Handler, null, stoppingToken);
            await client.SetMethodHandlerAsync("free", Command_free_Handler, null, stoppingToken);

            Enabled = InitProperty<bool>(twin, "enabled", default_enabled);
            Interval = (int)InitProperty<long>(twin, "interval", default_interval);

            TwinCollection reported = new TwinCollection();
            reported["started"] = DateTime.Now;
            reported["interval"] = new
            {
                value = Interval,
                ac = 200,
                av = twin.Properties.Reported.Version,
                ad = "prop initialized"
            };
            reported["enabled"] = new
            {
                value = Enabled,
                ac = 200,
                av = twin.Properties.Reported.Version,
                ad = "prop initialized"
            };

            await client.UpdateReportedPropertiesAsync(reported, stoppingToken);

            RefreshScreen(this);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (Enabled)
                {
                    telemetryCounter++;
                    telemetryWorkingSet = Environment.WorkingSet.Bytes().Megabytes;
                    managedMemory = GC.GetTotalMemory(true).Bytes().Megabytes;

                    var telemetry = new Message(
                        Encoding.UTF8.GetBytes(
                               JsonSerializer.Serialize(new 
                                    { 
                                       workingSet = telemetryWorkingSet,
                                       managedMemory = managedMemory 
                                    }
                               )));

                    await client.SendEventAsync(telemetry);
                }
            await Task.Delay(Interval, stoppingToken);
            }
        }

        private T InitProperty<T>(Twin twin, string propName, T default_value)
        {
            T desValue = twin.Properties.Desired.GetPropertyValue<T>(propName);
            if (desValue != null)
            {
                return desValue;
            }
            T repValue = twin.Properties.Reported.GetPropertyValue<T>(propName);
            if (repValue != null)
            {
                return repValue;
            }
            return default_value;
        }

        private Task<MethodResponse> Command_getRuntimeStats_Handler(MethodRequest methodRequest, object userContext)
        {
            commandCounter++;
            Dictionary<string, string> result = new()
            {
                { "machine name", Environment.MachineName },
                { "os version", Environment.OSVersion.ToString() },
                { "started", TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds).Humanize(3) }
            };

            DiagnosticsMode req = JsonSerializer.Deserialize<DiagnosticsMode>(methodRequest.DataAsJson);
            if (req == DiagnosticsMode.complete)
            {
                result.Add("sdk info:", infoVersion);
            }
            if (req == DiagnosticsMode.full)
            {
                result.Add("sdk info:", infoVersion);
                result.Add("interval: ", Interval.ToString());
                result.Add("enabled: ", Enabled.ToString());
                result.Add("twin receive: ", twinRecCounter.ToString());
                //result.diagnosticResults.Add($"twin sends: ", RidCounter.Current.ToString());
                result.Add("telemetry: ", telemetryCounter.ToString());
                result.Add("command: ", commandCounter.ToString());
                result.Add("reconnects: ", reconnectCounter.ToString());
                result.Add("workingSet", Environment.WorkingSet.Bytes().ToString());
                result.Add("GC Memmory", GC.GetTotalAllocatedBytes().Bytes().ToString());
            }
            byte[] respBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
            return Task.FromResult(new MethodResponse(respBytes, 200));
        }

        private async Task DesiredPropertiesHanlder(TwinCollection desiredProperties, object userContext)
        {
            twinRecCounter++;
            TwinCollection ack = new TwinCollection();
            if (desiredProperties.Contains("enabled"))
            {
                Enabled = desiredProperties.GetPropertyValue<bool>("enabled");
                ack["enabled"] = new
                {
                    value = this.Enabled,
                    ac = 200,
                    av = desiredProperties.Version,
                    ad = "prop accepted"
                };
                
            }   
            if (desiredProperties.Contains("interval"))
            {
                Interval = (int)desiredProperties.GetPropertyValue<long>("interval");
                ack["interval"] = new
                {
                    value = this.Interval,
                    ac = 200,
                    av = desiredProperties.Version,
                    ad = "prop accepted"
                };
                
            }
            await client!.UpdateReportedPropertiesAsync(ack);
        }

        List<string> memory = new();
        IntPtr memoryPtr = IntPtr.Zero;
        private Task<MethodResponse> Command_malloc_Handler(MethodRequest methodRequest, object userContext)
        {
            commandCounter++;
            int number = JsonSerializer.Deserialize<int>(methodRequest.DataAsJson);
            for (int i = 0; i < number; i++)
            {
                memory.Add(i.ToOrdinalWords());
            }

            memoryPtr = Marshal.AllocHGlobal(number);
            return Task.FromResult(new MethodResponse(200));
        }
        private Task<MethodResponse> Command_free_Handler(MethodRequest methodRequest, object userContext)
        {
            commandCounter++;
            memory = new List<string>();
            GC.Collect(2, GCCollectionMode.Forced, false);
            if (memoryPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(memoryPtr);
                memoryPtr = IntPtr.Zero;
            }
            return Task.FromResult(new MethodResponse(200));
        }


        private Task<MethodResponse> Command_isPrime_Handler(MethodRequest req, object userContext)
        {
            commandCounter++;
            IEnumerable<string> Multiples(int number)
            {
                return from n1 in Enumerable.Range(2, number / 2)
                       from n2 in Enumerable.Range(2, n1)
                       where n1 * n2 == number
                       select $"{n1} x {n2} => {number}";
            }
            int number = JsonSerializer.Deserialize<int>(req.DataAsJson);
            bool result = !Multiples(number).Any();
            byte[] respPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result));
            return Task.FromResult(new MethodResponse(respPayload, 200));
        }

        private Timer? screenRefresher = null;
        private void RefreshScreen(object state)
        {
            string RenderData()
            {
                void AppendLineWithPadRight(StringBuilder sb, string s) => sb.AppendLine(s?.PadRight(Console.BufferWidth > 1 ? Console.BufferWidth - 1 : 300));

                string enabled_value = Enabled.ToString();
                string interval_value = Interval.ToString();
                StringBuilder sb = new();
                AppendLineWithPadRight(sb, " ");
                //AppendLineWithPadRight(sb, $"{connectionSettings?.HostName}:{connectionSettings?.TcpPort}");
                //AppendLineWithPadRight(sb, $"{connectionSettings.ClientId} (Auth:{connectionSettings.Auth}/ TLS:{connectionSettings.UseTls})");
                AppendLineWithPadRight(sb, connectionString);
                AppendLineWithPadRight(sb, " ");
                AppendLineWithPadRight(sb, string.Format("{0:8} | {1:15} | {2}", "Property", "Value".PadRight(15), "Version"));
                AppendLineWithPadRight(sb, string.Format("{0:8} | {1:15} | {2}", "--------", "-----".PadLeft(15, '-'), "------"));
                AppendLineWithPadRight(sb, string.Format("{0:8} | {1:15} | {2}", "enabled".PadRight(8), enabled_value?.PadLeft(15), 0));
                AppendLineWithPadRight(sb, string.Format("{0:8} | {1:15} | {2}", "interval".PadRight(8), interval_value?.PadLeft(15), 0));
                //AppendLineWithPadRight(sb, string.Format("{0:8} | {1:15} | {2}", "started".PadRight(8), client.Property_started.T().PadLeft(15), client?.Property_started?.Version));
                AppendLineWithPadRight(sb, " ");
                AppendLineWithPadRight(sb, $"Reconnects: {reconnectCounter}");
                AppendLineWithPadRight(sb, $"Telemetry: {telemetryCounter}");
                AppendLineWithPadRight(sb, $"Twin receive: {twinRecCounter}");
                //AppendLineWithPadRight(sb, $"Twin send: {RidCounter.Current}");
                AppendLineWithPadRight(sb, $"Command messages: {commandCounter}");
                AppendLineWithPadRight(sb, " ");
                AppendLineWithPadRight(sb, $"WorkingSet: {telemetryWorkingSet} MB");
                AppendLineWithPadRight(sb, $"ManagedMemory: {managedMemory} MB");
                AppendLineWithPadRight(sb, " ");
                AppendLineWithPadRight(sb, $"Time Running: {TimeSpan.FromMilliseconds(clock.ElapsedMilliseconds).Humanize(3)}");
                //AppendLineWithPadRight(sb, $"ConnectionStatus: {client.Connection.IsConnected} [{lastDiscconectReason}]");
                AppendLineWithPadRight(sb, $"NuGet: {infoVersion}");
                AppendLineWithPadRight(sb, " ");
                return sb.ToString();
            }

            Console.SetCursorPosition(0, 0);
            Console.WriteLine(RenderData());
            screenRefresher = new Timer(RefreshScreen, this, 1000, 0);
        }

    }
}