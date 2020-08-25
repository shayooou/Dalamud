using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Dalamud.Interface;
using EasyHook;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Dalamud
{
    public sealed class EntryPoint : IEntryPoint
    {
        public EntryPoint(RemoteHooking.IContext ctx, DalamudStartInfo info)
        {
            // Required by EasyHook
        }

        public void Run(RemoteHooking.IContext ctx, DalamudStartInfo info)
        {
            // Setup logger
            var (logger, levelSwitch) = NewLogger(info.WorkingDirectory);
            Log.Logger = logger;

            try
            {
                Log.Information("Initializing a session..");
                sendMessageToFFyu("开始注入插件");
                // This is due to GitHub not supporting TLS 1.0, so we enable all TLS versions globally
                System.Net.ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls | SecurityProtocolType.Ssl3;

                // Log any unhandled exception.
                AppDomain.CurrentDomain.ProcessExit += OnExit;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                using var dalamud = new Dalamud(info, levelSwitch);
                Log.Information("Starting a session..");

                // Run session
                dalamud.Start();
                sendMessageToFFyu("注入成功运行");
                dalamud.WaitForUnload();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Unhandled exception on main thread.");
            }
            finally
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                AppDomain.CurrentDomain.ProcessExit -= OnExit;
                sendMessageToFFyu("结束注入dll");
                Util.messageThread.RunOnThread(delegate {
                    Util.messageThread.Kill();
                });
                Log.Information("Session has ended.");
                Log.CloseAndFlush();
            }
        }

        private void sendMessageToFFyu(string msgString)
        {
            try
            {
                Process ffyuProcess = Process.GetProcessesByName("FFyu").FirstOrDefault<Process>();
                if ((ffyuProcess != null && ffyuProcess.Responding))
                {
                    Util.sendToFFyu(ffyuProcess.MainWindowHandle, 0, msgString);
                }
            }
            catch { }
        }

        private (Logger logger, LoggingLevelSwitch levelSwitch) NewLogger(string baseDirectory)
        {
            var logPath = Path.Combine(baseDirectory, "dalamud.txt");

            var levelSwitch = new LoggingLevelSwitch();

#if DEBUG
            levelSwitch.MinimumLevel = LogEventLevel.Verbose;
#else
            levelSwitch.MinimumLevel = LogEventLevel.Verbose;
#endif

            var newLogger = new LoggerConfiguration()
                   .WriteTo.Async(a => a.File(logPath))
                   .WriteTo.EventSink()
                   .MinimumLevel.ControlledBy(levelSwitch)
                   .CreateLogger();

            return (newLogger, levelSwitch);
        }

        private void OnExit(object sender, EventArgs arg)
        {
            Util.messageThread.Kill();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs arg)
        {
            switch (arg.ExceptionObject)
            {
                case Exception ex:
                    Log.Fatal(ex, "Unhandled exception on AppDomain");
                    break;
                default:
                    Log.Fatal("Unhandled SEH object on AppDomain: {Object}", arg.ExceptionObject);
                    break;
            }
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            if (!e.Observed)
                Log.Error(e.Exception, "Unobserved exception in Task.");
        }
    }
}
