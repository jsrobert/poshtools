﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using PowerShellTools.Common;
using System.Runtime.InteropServices;
using log4net;

namespace PowerShellTools.ServiceManagement
{
    /// <summary>
    /// Helper class for creating a process used to host the WCF service.
    /// </summary>
    internal static class PowershellHostProcessHelper
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int W, int H, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); 

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint TOPMOST_FLAGS = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        private const int SW_HIDE = 0;

        private static readonly ILog Log = LogManager.GetLogger(typeof(PowershellHostProcessHelper));

        public static Guid EndPointGuid { get; private set; }

        public static PowershellHostProcess CreatePowershellHostProcess()
        {
            PowerShellToolsPackage.DebuggerReadyEvent.Reset();

            Process powerShellHostProcess = new Process();
            string hostProcessReadyEventName = Constants.ReadyEventPrefix + Guid.NewGuid();
            EndPointGuid = Guid.NewGuid();

            string exeName = Constants.PowershellHostExeName;
            string currentPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(currentPath, exeName);
            string hostArgs = String.Format(CultureInfo.InvariantCulture,
                                            "{0}{1} {2}{3} {4}{5}",
                                            Constants.UniqueEndpointArg, EndPointGuid, // For generating a unique endpoint address 
                                            Constants.VsProcessIdArg, Process.GetCurrentProcess().Id,
                                            Constants.ReadyEventUniqueNameArg, hostProcessReadyEventName);

            powerShellHostProcess.StartInfo.Arguments = hostArgs;
            powerShellHostProcess.StartInfo.FileName = path;

            powerShellHostProcess.StartInfo.CreateNoWindow = false;
            powerShellHostProcess.StartInfo.WindowStyle = ProcessWindowStyle.Normal;

            powerShellHostProcess.StartInfo.UseShellExecute = false;
            powerShellHostProcess.StartInfo.RedirectStandardInput = true;
            powerShellHostProcess.StartInfo.RedirectStandardOutput = true;
            powerShellHostProcess.StartInfo.RedirectStandardError = true;

            powerShellHostProcess.OutputDataReceived += powerShellHostProcess_OutputDataReceived;
            powerShellHostProcess.ErrorDataReceived += powerShellHostProcess_ErrorDataReceived;

            EventWaitHandle readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, hostProcessReadyEventName);

            powerShellHostProcess.Start();
            powerShellHostProcess.EnableRaisingEvents = true;

            powerShellHostProcess.BeginOutputReadLine();
            powerShellHostProcess.BeginErrorReadLine();

            // For now we dont set timeout and wait infinitely
            // Further UI work might enable some better UX like retry logic for case where remote process being unresponsive
            // By then we will bring timeout back here.
            bool success = readyEvent.WaitOne();
            readyEvent.Close();

            MakeTopMost(powerShellHostProcess.MainWindowHandle);

            #if !DEBUG
                ShowWindow(powershellHostProcess.MainWindowHandle, SW_HIDE);
            #endif

            if (!success)
            {
                int processId = powerShellHostProcess.Id;
                try
                {
                    powerShellHostProcess.Kill();
                }
                catch (Exception)
                {
                }

                if (powerShellHostProcess != null)
                {
                    powerShellHostProcess.Dispose();
                    powerShellHostProcess = null;
                }
                throw new PowershellHostProcessException(String.Format(CultureInfo.CurrentCulture,
                                                                        Resources.ErrorFailToCreateProcess,
                                                                        processId.ToString()));
            }

            //StreamWriter inputStreamWriter = powerShellHostProcess.StandardInput;

            return new PowershellHostProcess(powerShellHostProcess, EndPointGuid);
        }

        private static void powerShellHostProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            powerShellHostProcessOutput(e.Data);
        }

        static void powerShellHostProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            powerShellHostProcessOutput(e.Data);
        }

        private static void powerShellHostProcessOutput(string outputData)
        {
            if (outputData.StartsWith(string.Format("[{0}]:", PowershellHostProcessHelper.EndPointGuid), StringComparison.OrdinalIgnoreCase))
            {
                // debug data
                Log.Debug(outputData);
            }
            else
            {
                // app data
                if (PowerShellToolsPackage.Debugger != null &&
                    PowerShellToolsPackage.Debugger.HostUi != null)
                {
                    PowerShellToolsPackage.Debugger.HostUi.VsOutputString(outputData);
                }
            }
        }

        private static void MakeTopMost(IntPtr hWnd)
        {
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
        }
    }

    /// <summary>
    /// The structure containing the process we want and a guid used for the WCF client to establish connection to the service.
    /// </summary>
    public class PowershellHostProcess
    {
        public PowershellHostProcess(Process process, Guid guid)
        {
            Process = process;
            EndpointGuid = guid;
        }

        public Process Process { get; private set; }

        public Guid EndpointGuid { get; private set; }
    }
}
