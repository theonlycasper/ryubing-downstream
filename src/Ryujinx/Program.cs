using Avalonia;
using Avalonia.Threading;
using DiscordRPC;
using Gommon;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using Projektanker.Icons.Avalonia.MaterialDesign;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Ava.Utilities;
using Ryujinx.Ava.Utilities.Configuration;
using Ryujinx.Ava.Utilities.SystemInfo;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.GraphicsDriver;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using Ryujinx.Headless;
using Ryujinx.SDL2.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Ryujinx.Ava
{
    internal partial class Program
    {
        public static double WindowScaleFactor { get; set; }
        public static double DesktopScaleFactor { get; set; } = 1.0;
        public static string Version { get; private set; }
        public static string ConfigurationPath { get; private set; }
        public static string GlobalConfigurationPath { get; private set; }
        public static bool PreviewerDetached { get; private set; }
        public static bool UseHardwareAcceleration { get; private set; }
        public static string BackendThreadingArg { get; private set; }

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int MessageBoxA(nint hWnd, [MarshalAs(UnmanagedType.LPStr)] string text, [MarshalAs(UnmanagedType.LPStr)] string caption, uint type);

        private const uint MbIconwarning = 0x30;

        public static int Main(string[] args)
        {
            Version = ReleaseInformation.Version;
            
            if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                _ = MessageBoxA(nint.Zero, "You are running an outdated version of Windows.\n\nRyujinx supports Windows 10 version 20H1 and newer.\n", $"Ryujinx {Version}", MbIconwarning);
                return 0;
            }

            PreviewerDetached = true;
            
            if (args.Length > 0 && args[0] is "--no-gui" or "nogui")
            {
                HeadlessRyujinx.Entrypoint(args[1..]);
                return 0;
            }

            Initialize(args);
            
            LoggerAdapter.Register();

            IconProvider.Current
                .Register<FontAwesomeIconProvider>()
                .Register<MaterialDesignIconProvider>();

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<RyujinxApp>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions
                {
                    EnableMultiTouch = true,
                    EnableIme = true,
                    EnableInputFocusProxy = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "gamescope",
                    RenderingMode = UseHardwareAcceleration
                        ? [X11RenderingMode.Glx, X11RenderingMode.Software]
                        : [X11RenderingMode.Software]
                })
                .With(new Win32PlatformOptions
                {
                    WinUICompositionBackdropCornerRadius = 8.0f,
                    RenderingMode = UseHardwareAcceleration
                        ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
                        : [Win32RenderingMode.Software]
                });

        private static void Initialize(string[] args)
        {
            // Ensure Discord presence timestamp begins at the absolute start of when Ryujinx is launched
            DiscordIntegrationModule.EmulatorStartedAt = Timestamps.Now;

            // Parse arguments
            CommandLineState.ParseArguments(args);

            if (OperatingSystem.IsMacOS())
            {
                MVKInitialization.InitializeResolver();
            }

            // Delete backup files after updating.
            Task.Run(Updater.CleanupUpdate);

            Console.Title = $"{RyujinxApp.FullAppName} Console {Version}";

            // Hook unhandled exception and process exit events.
            AppDomain.CurrentDomain.UnhandledException += (sender, e)
                => ProcessUnhandledException(sender, e.ExceptionObject as Exception, e.IsTerminating);
            TaskScheduler.UnobservedTaskException += (sender, e)
                => ProcessUnhandledException(sender, e.Exception, false); 
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Exit();


            
            // Setup base data directory.
            AppDataManager.Initialize(CommandLineState.BaseDirPathArg);

            // Initialize the configuration.
            ConfigurationState.Initialize();

            // Initialize the logger system.
            LoggerModule.Initialize();

            // Initialize Discord integration.
            DiscordIntegrationModule.Initialize();

            // Initialize SDL2 driver
            SDL2Driver.MainThreadDispatcher = action => Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Input);

            ReloadConfig();

            WindowScaleFactor = ForceDpiAware.GetWindowScaleFactor();

            // Logging system information.
            PrintSystemInfo();

            // Enable OGL multithreading on the driver, and some other flags.
            DriverUtilities.InitDriverConfig(ConfigurationState.Instance.Graphics.BackendThreading == BackendThreading.Off);

            // Check if keys exists.
            if (!File.Exists(Path.Combine(AppDataManager.KeysDirPath, "prod.keys")))
            {
                if (!(AppDataManager.Mode == AppDataManager.LaunchMode.UserProfile && File.Exists(Path.Combine(AppDataManager.KeysDirPathUser, "prod.keys"))))
                {
                    MainWindow.ShowKeyErrorOnLoad = true;
                }
            }

            if (CommandLineState.LaunchPathArg != null)
            {
                MainWindow.DeferLoadApplication(CommandLineState.LaunchPathArg, CommandLineState.LaunchApplicationId, CommandLineState.StartFullscreenArg);
            }
        }

        public static string GetDirGameUserConfig(string gameId, bool rememberGlobalDir = false, bool changeFolderForGame = false)
        {
            if (string.IsNullOrEmpty(gameId))
            {
                return "";
            }

            string gameDir = Path.Combine(AppDataManager.GamesDirPath, gameId, ReleaseInformation.ConfigName);

            // Should load with the game if there is a custom setting for the game
            if (rememberGlobalDir)
            {
                GlobalConfigurationPath = ConfigurationPath;
            }

            if (changeFolderForGame)
            {
                ConfigurationPath = gameDir;
            }

            return gameDir;
        }

        public static void ReloadConfig()
        {
            //It is necessary that when a user setting appears, the global setting remains available
            GlobalConfigurationPath = null;

            string localConfigurationPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ReleaseInformation.ConfigName);
            string appDataConfigurationPath = Path.Combine(AppDataManager.BaseDirPath, ReleaseInformation.ConfigName);


            // Now load the configuration as the other subsystems are now registered
            if (File.Exists(localConfigurationPath))
            {
                ConfigurationPath = localConfigurationPath;
            }
            else if (File.Exists(appDataConfigurationPath))
            {
                ConfigurationPath = appDataConfigurationPath;
            }

            if (ConfigurationPath == null)
            {
                // No configuration, we load the default values and save it to disk
                ConfigurationPath = appDataConfigurationPath;
                Logger.Notice.Print(LogClass.Application, $"No configuration file found. Saving default configuration to: {ConfigurationPath}");

                ConfigurationState.Instance.LoadDefault();
                ConfigurationState.Instance.ToFileFormat().SaveConfig(ConfigurationPath);
            }
            else
            {
                Logger.Notice.Print(LogClass.Application, $"Loading configuration from: {ConfigurationPath}");

                if (ConfigurationFileFormat.TryLoad(ConfigurationPath, out ConfigurationFileFormat configurationFileFormat))
                {
                    ConfigurationState.Instance.Load(configurationFileFormat, ConfigurationPath);
                }
                else
                {
                    Logger.Warning?.PrintMsg(LogClass.Application, $"Failed to load config! Loading the default config instead.\nFailed config location: {ConfigurationPath}");

                    ConfigurationState.Instance.LoadDefault();
                }
            }

            UseHardwareAcceleration = ConfigurationState.Instance.EnableHardwareAcceleration;

            // Check if graphics backend was overridden
            if (CommandLineState.OverrideGraphicsBackend is not null)
                ConfigurationState.Instance.Graphics.GraphicsBackend.Value = CommandLineState.OverrideGraphicsBackend.ToLower() switch
                {
                    "opengl" => GraphicsBackend.OpenGl,
                    "vulkan" => GraphicsBackend.Vulkan,
                    _ => ConfigurationState.Instance.Graphics.GraphicsBackend
                };

            // Check if backend threading was overridden
            if (CommandLineState.OverrideBackendThreading is not null)
                ConfigurationState.Instance.Graphics.BackendThreading.Value = CommandLineState.OverrideBackendThreading.ToLower() switch
                {
                    "auto" => BackendThreading.Auto,
                    "off" => BackendThreading.Off,
                    "on" => BackendThreading.On,
                    _ => ConfigurationState.Instance.Graphics.BackendThreading
                };

            if (CommandLineState.OverrideBackendThreadingAfterReboot is not null)
            {
                BackendThreadingArg = CommandLineState.OverrideBackendThreadingAfterReboot;
            }

            // Check if docked mode was overriden.
            if (CommandLineState.OverrideDockedMode.HasValue)
                ConfigurationState.Instance.System.EnableDockedMode.Value = CommandLineState.OverrideDockedMode.Value;


            // Check if HideCursor was overridden.
            if (CommandLineState.OverrideHideCursor is not null)
                ConfigurationState.Instance.HideCursor.Value = CommandLineState.OverrideHideCursor.ToLower() switch
                {
                    "never" => HideCursorMode.Never,
                    "onidle" => HideCursorMode.OnIdle,
                    "always" => HideCursorMode.Always,
                    _ => ConfigurationState.Instance.HideCursor,
                };

            // Check if memoryManagerMode was overridden. 
            if (CommandLineState.OverrideMemoryManagerMode is not null)
                if (Enum.TryParse(CommandLineState.OverrideMemoryManagerMode, true, out MemoryManagerMode result))
                {
                    ConfigurationState.Instance.System.MemoryManagerMode.Value = result;
                }

            // Check if PPTC was overridden. 
            if (CommandLineState.OverridePPTC is not null)
                if (Enum.TryParse(CommandLineState.OverridePPTC, true, out bool result))
                {
                    ConfigurationState.Instance.System.EnablePtc.Value = result;
                }

            // Check if region was overridden. 
            if (CommandLineState.OverrideSystemRegion is not null)
                if (Enum.TryParse(CommandLineState.OverrideSystemRegion, true, out Ryujinx.HLE.HOS.SystemState.RegionCode result))
                {
                    ConfigurationState.Instance.System.Region.Value = (Utilities.Configuration.System.Region)result;
                }

            //Check if language was overridden. 
            if (CommandLineState.OverrideSystemLanguage is not null)
                if (Enum.TryParse(CommandLineState.OverrideSystemLanguage, true, out Ryujinx.HLE.HOS.SystemState.SystemLanguage result))
                {
                    ConfigurationState.Instance.System.Language.Value = (Utilities.Configuration.System.Language)result;
                }

            // Check if hardware-acceleration was overridden.
            if (CommandLineState.OverrideHardwareAcceleration != null)
                UseHardwareAcceleration = CommandLineState.OverrideHardwareAcceleration.Value;
        }

        internal static void PrintSystemInfo()
        {
            Logger.Notice.Print(LogClass.Application, $"{RyujinxApp.FullAppName} Version: {Version}");
            Logger.Notice.Print(LogClass.Application, $".NET Runtime: {RuntimeInformation.FrameworkDescription}");
            SystemInfo.Gather().Print();

            Logger.Notice.Print(LogClass.Application, $"Logs Enabled: {
                Logger.GetEnabledLevels()
                    .FormatCollection(
                        x => x.ToString(), 
                        separator: ", ", 
                        emptyCollectionFallback: "<None>")
            }");

            Logger.Notice.Print(LogClass.Application,
                AppDataManager.Mode == AppDataManager.LaunchMode.Custom
                    ? $"Launch Mode: Custom Path {AppDataManager.BaseDirPath}"
                    : $"Launch Mode: {AppDataManager.Mode}");
        }

        internal static void ProcessUnhandledException(object sender, Exception initialException, bool isTerminating)
        {
            Logger.Log log = Logger.Error ?? Logger.Notice;

            List<Exception> exceptions = [];

            if (initialException is AggregateException ae)
            {
                exceptions.AddRange(ae.InnerExceptions);
            }
            else
            {
                exceptions.Add(initialException);
            }

            foreach (Exception e in exceptions)
            {
                string message = $"Unhandled exception caught: {e}";
                // ReSharper disable once ConstantConditionalAccessQualifier
                if (sender?.GetType()?.AsPrettyString() is { } senderName)
                    log.Print(LogClass.Application, message, senderName);
                else
                    log.PrintMsg(LogClass.Application, message);
            }
            
            
            if (isTerminating)
                Exit();
        }

        internal static void Exit()
        {
            DiscordIntegrationModule.Exit();

            Logger.Shutdown();
        }
    }
}
