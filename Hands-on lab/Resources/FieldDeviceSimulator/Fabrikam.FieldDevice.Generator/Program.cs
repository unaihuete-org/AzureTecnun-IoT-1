﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Fabrikam.FieldDevice.Generator
{
    internal class Program
    {
        private static IConfigurationRoot _configuration;
        private static List<PumpDevice> devices = new List<PumpDevice>();
        private static readonly object LockObject = new object();
        // AutoResetEvent to signal when to exit the application.
        private static readonly AutoResetEvent WaitHandle = new AutoResetEvent(false);
        private static CancellationTokenSource _cancellationSource = new CancellationTokenSource();
        private static Dictionary<string, Task> _runningDeviceTasks;
        
        static void Main(string[] args)
        {
            // Setup configuration to either read from the appsettings.json file (if present) or environment variables.
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            _configuration = builder.Build();

            
            var cancellationToken = _cancellationSource.Token;

            WriteLineInColor("Pump Telemetry Generator", ConsoleColor.White);
            Console.WriteLine("=============");
            WriteLineInColor("** Enter 1 to generate and send pump device telemetry to IoT Central.", ConsoleColor.Green);
            WriteLineInColor("** Enter 2 to generate anomaly model training data in CSV files.", ConsoleColor.Green);
            Console.WriteLine("=============");
            Console.WriteLine(string.Empty);
            WriteLineInColor("Press Ctrl+C or Ctrl+Break to cancel while generator is running.", ConsoleColor.Cyan);
            Console.WriteLine(string.Empty);

            // Handle Control+C or Control+Break.
            Console.CancelKeyPress += (o, e) =>
            {
                WriteLineInColor("Stopped generator. No more events are being sent.", ConsoleColor.Yellow);
                CancelAll();

                // Allow the main thread to continue and exit...
                WaitHandle.Set();
            };

            var userInput = "";

            while (true)
            {
                Console.Write("Enter the number of the operation you would like to perform > ");

                var input = Console.ReadLine();
                if (input.Equals("1", StringComparison.InvariantCultureIgnoreCase) ||
                    input.Equals("2", StringComparison.InvariantCultureIgnoreCase))
                {
                    userInput = input.Trim();
                    break;
                }

                Console.WriteLine("Invalid input entered. Please enter 1 or 2");
            }

            switch (userInput)
            {
                case "1":
                    try
                    {
                        // Start sending telemetry to simulated devices:
                        SetupDeviceRunTasks().GetAwaiter().GetResult();
                        var tasks = _runningDeviceTasks.Select(t => t.Value).ToList();
                        while (tasks.Count > 0)
                        {
                            try
                            {
                                Task.WhenAll(tasks).Wait(cancellationToken);
                            }
                            catch (TaskCanceledException)
                            {
                                //expected
                            }
                            catch(Exception ex){
                                Console.WriteLine(ex.Message);
                            }
                            
                            tasks = _runningDeviceTasks.Where(t=> !t.Value.IsCompleted).Select(t => t.Value).ToList();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("The device telemetry operation was canceled.");
                        // No need to throw, as this was expected.
                    }
                    break;
                case "2":
                    GenerateTrainingData();
                    break;
            }

            CancelAll();
            Console.WriteLine();
            WriteLineInColor("Done sending generated pump data", ConsoleColor.Cyan);
            Console.WriteLine();
            Console.WriteLine();

            // Keep the console open.
            Console.ReadLine();
            WaitHandle.WaitOne();
        }


        /// <summary>
        /// Generates anomaly model training data to new CSV files.
        /// </summary>
        private static void GenerateTrainingData()
        {
            var sampleSize = 10000;

            Console.WriteLine("Generating data for ML model training. This may take a while...");

            // Generate with no failures:
            Console.WriteLine("\r\nGenerating data with no failures...");
            GenerateData.GenerateModelTrainingData(sampleSize, false, 0, false, true);
            // Generate with immediate failures:
            Console.WriteLine("\r\nGenerating data with immediate failures...");
            GenerateData.GenerateModelTrainingData(sampleSize, true, 0, false, true);
            // Generate with gradual failures:
            Console.WriteLine("\r\nGenerating data with gradual failures...");
            GenerateData.GenerateModelTrainingData(sampleSize, true, 2500, false, true);

            Console.WriteLine("\r\n---------------------------\r\nGeneration complete.");
        }

        /// <summary>
        /// Creates the set of tasks that will send data to the IoT hub.
        /// </summary>
        /// <returns></returns>
        private static async Task SetupDeviceRunTasks()
        {
            var deviceTasks = new Dictionary<string, Task>();
            var config = ParseConfiguration();
            const int sampleSize = 10000;
            const int failOverXIterations = 625;

            Console.WriteLine("Setting up simulated pump devices and generating random sample data. This may take a while...");

            // Add a pump that gradually fails.
            devices.Add(new PumpDevice(1, config.Device1Key, config.IdScope, config.DpsEndpoint, "DEVICE001", "192.168.1.1", new Location(35.815090, -101.043192), 
                GenerateData.GeneratePumpTelemetry(sampleSize, true, failOverXIterations)));
            // Add a pump that never fails.
            devices.Add(new PumpDevice(2, config.Device2Key, config.IdScope, config.DpsEndpoint, "DEVICE002", "192.168.1.2", new Location(35.815862, -101.042167),
                GenerateData.GeneratePumpTelemetry(sampleSize + failOverXIterations, false, 0)));
            // Add a pump that immediately fails after a period of time.
            devices.Add(new PumpDevice(3, config.Device3Key, config.IdScope, config.DpsEndpoint, "DEVICE003", "192.168.1.3", new Location(35.815743, -101.048099),  
                GenerateData.GeneratePumpTelemetry(sampleSize + failOverXIterations, true, 0)));

            foreach (var device in devices)
            {
                await device.RegisterAndConnectDeviceAsync(); //fire and forget
                deviceTasks.Add(device.DeviceId, device.RunDeviceAsync());
                device.PumpPowerStateChanged += Device_PumpPowerStateChanged;
            }

            _runningDeviceTasks = deviceTasks;           
        }

        private static void Device_PumpPowerStateChanged(object sender, PumpPowerStateChangedArgs e)
        {
            //reset pump if state has changed to being powered on
            if (e.PumpPowerState == PumpPowerState.ON)
            {
                //get current device running task
                var device = devices.FirstOrDefault(d => d.DeviceId == e.DeviceId);

                if (device != null)
                {
                    _runningDeviceTasks.Remove(e.DeviceId);
                    _runningDeviceTasks.Add(device.DeviceId, device.RunDeviceAsync());
                }
            }
        }

        private static void CancelAll()
        {
            foreach(var device in devices)
            {
                device.CancelCurrentRun();
            }
            _cancellationSource.Cancel();
        }

        /// <summary>
        /// Extracts properties from either the appsettings.json file or system environment variables.
        /// </summary>
        /// <returns>
        /// IdScope: ID Scope of the IoT Central application devices
        /// DpsEndpoint: DPS Endpoint
        /// Device1Key: Connection key for the first pump device in IoT Central.
        /// Device2Key: Connection key for the second pump device in IoT Central.
        /// Device3Key: Connection key for the third pump device in IoT Central.
        /// </returns>
        private static (string IdScope,
                        string DpsEndpoint,
                        string Device1Key,
                        string Device2Key,
                        string Device3Key) ParseConfiguration()
        {
            try
            {
                // The Configuration object will extract values either from the machine's environment variables, or the appsettings.json file.
                var idScope = _configuration["ID_SCOPE"];
                var dpsEndpoint = _configuration["DPS_ENDPOINT"];
                var device1Key = _configuration["DEVICE_1_KEY"];
                var device2Key = _configuration["DEVICE_2_KEY"];
                var device3Key = _configuration["DEVICE_3_KEY"];

                if (string.IsNullOrWhiteSpace(idScope))
                {
                    throw new ArgumentException("ID_SCOPE must be provided");
                }
                
                if (string.IsNullOrWhiteSpace(dpsEndpoint))
                {
                    throw new ArgumentException("DPS_ENDPOINT must be provided");
                }

                if (string.IsNullOrWhiteSpace(device1Key))
                {
                    throw new ArgumentException("DEVICE_1_KEY must be provided");
                }

                if (string.IsNullOrWhiteSpace(device2Key))
                {
                    throw new ArgumentException("DEVICE_2_KEY must be provided");
                }

                if (string.IsNullOrWhiteSpace(device3Key))
                {
                    throw new ArgumentException("DEVICE_3_Key must be provided");
                }

                return (idScope, dpsEndpoint, device1Key, device2Key, device3Key);
            }
            catch (Exception e)
            {
                WriteLineInColor(e.Message, ConsoleColor.Red);
                Console.ReadLine();
                throw;
            }
        }

        public static void WriteLineInColor(string msg, ConsoleColor color)
        {
            lock (LockObject)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(msg);
                Console.ResetColor();
            }
        }
    }
}
