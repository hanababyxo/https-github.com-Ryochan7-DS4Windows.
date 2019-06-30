﻿using System;
using System.Windows.Forms;
using System.Threading;
using System.Runtime.InteropServices;
using Process = System.Diagnostics.Process;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Win32.TaskScheduler;

namespace DS4Windows
{
    static class Program
    {
        [DllImport("user32.dll", EntryPoint = "FindWindow")]
        private static extern IntPtr FindWindow(string sClass, string sWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);

        public const int WM_COPYDATA = 0x004A;

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        // Add "global\" in front of the EventName, then only one instance is allowed on the
        // whole system, including other users. But the application can not be brought
        // into view, of course. 
        private static string SingleAppComEventName = "{a52b5b20-d9ee-4f32-8518-307fa14aa0c6}";
        private static EventWaitHandle threadComEvent = null;
        private static bool exitComThread = false;
        public static ControlService rootHub;
        private static Thread testThread;
        private static Thread controlThread;
        private static Form ds4form;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            //Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
            for (int i = 0, argsLen = args.Length; i < argsLen; i++)
            {
                string s = args[i];
                if (s == "driverinstall" || s == "-driverinstall")
                {
                    Application.EnableVisualStyles();
                    Application.Run(new Forms.WelcomeDialog(true));
                    return;
                }
                else if (s == "re-enabledevice" || s == "-re-enabledevice")
                {
                    try
                    {
                        i++;
                        string deviceInstanceId = args[i];
                        DS4Devices.reEnableDevice(deviceInstanceId);
                        Environment.ExitCode = 0;
                        return;
                    }
                    catch (Exception)
                    {
                        Environment.ExitCode = Marshal.GetLastWin32Error();
                        return;
                    }
                }
                else if (s == "runtask" || s == "-runtask")
                {
                    TaskService ts = new TaskService();
                    Task tasker = ts.FindTask("RunDS4Windows");
                    if (tasker != null)
                    {
                        tasker.Run("");
                    }

                    Environment.ExitCode = 0;
                    return;
                }
                else if (s == "command" || s == "-command")
                {
                    i++;

                    IntPtr hWndDS4WindowsForm = IntPtr.Zero;

                    Global.FindConfigLocation();
                    if (args[i].Length > 0 && !string.IsNullOrEmpty(Global.appdatapath) && System.IO.File.Exists(Global.appdatapath + "\\IPCClassName.dat"))
                    {
                        hWndDS4WindowsForm = FindWindow(System.IO.File.ReadAllText(Global.appdatapath + "\\IPCClassName.dat"), "DS4Windows");
                        if (hWndDS4WindowsForm != IntPtr.Zero)
                        {
                            COPYDATASTRUCT cds;
                            cds.lpData = IntPtr.Zero;

                            try
                            {
                                cds.dwData = IntPtr.Zero;
                                cds.cbData = args[i].Length;
                                cds.lpData = Marshal.StringToHGlobalAnsi(args[i]);
                                SendMessage(hWndDS4WindowsForm, WM_COPYDATA, IntPtr.Zero, ref cds);
                            }
                            finally
                            {
                                if(cds.lpData != IntPtr.Zero)
                                    Marshal.FreeHGlobal(cds.lpData);
                            }
                        }
                        else
                        {
                            Environment.ExitCode = 2;
                        }
                    }
                    else
                    {
                        Environment.ExitCode = 1;
                    }

                    return;
                }
            }

            try
            {
                Process.GetCurrentProcess().PriorityClass =
                    System.Diagnostics.ProcessPriorityClass.High;
            }
            catch { } // Ignore problems raising the priority.

            // Force Normal IO Priority
            IntPtr ioPrio = new IntPtr(2);
            Util.NtSetInformationProcess(Process.GetCurrentProcess().Handle,
                Util.PROCESS_INFORMATION_CLASS.ProcessIoPriority, ref ioPrio, 4);

            // Force Normal Page Priority
            IntPtr pagePrio = new IntPtr(5);
            Util.NtSetInformationProcess(Process.GetCurrentProcess().Handle,
                Util.PROCESS_INFORMATION_CLASS.ProcessPagePriority, ref pagePrio, 4);

            try
            {
                // another instance is already running if OpenExsting succeeds.
                threadComEvent = EventWaitHandle.OpenExisting(SingleAppComEventName,
                    System.Security.AccessControl.EventWaitHandleRights.Synchronize |
                    System.Security.AccessControl.EventWaitHandleRights.Modify);
                threadComEvent.Set();  // signal the other instance.
                threadComEvent.Close();
                return;    // return immediatly.
            }
            catch { /* don't care about errors */ }

            // Create the Event handle
            threadComEvent = new EventWaitHandle(false, EventResetMode.ManualReset, SingleAppComEventName);
            //System.Threading.Tasks.Task.Run(() => CreateTempWorkerThread());
            //CreateInterAppComThread();
            CreateTempWorkerThread();
            //System.Threading.Tasks.Task.Run(() => { Thread.CurrentThread.Priority = ThreadPriority.Lowest; CreateInterAppComThread(); Thread.CurrentThread.Priority = ThreadPriority.Lowest; }).Wait();

            //if (mutex.WaitOne(TimeSpan.Zero, true))
            //{
            createControlService();
                //rootHub = new ControlService();
                Application.EnableVisualStyles();
                ds4form = new Forms.DS4Form(args);
                Application.Run();
            //mutex.ReleaseMutex();
            //}

            exitComThread = true;
            threadComEvent.Set();  // signal the other instance.
            while (testThread.IsAlive)
                Thread.SpinWait(500);
            threadComEvent.Close();
        }

        private static void createControlService()
        {
            controlThread = new Thread(() => { rootHub = new ControlService(); });
            controlThread.Priority = ThreadPriority.Normal;
            controlThread.IsBackground = true;
            controlThread.Start();
            while (controlThread.IsAlive)
                Thread.SpinWait(500);
        }

        private static void CreateTempWorkerThread()
        {
            testThread = new Thread(SingleAppComThread_DoWork);
            testThread.Priority = ThreadPriority.Lowest;
            testThread.IsBackground = true;
            testThread.Start();
        }

        private static void SingleAppComThread_DoWork()
        {
            while (!exitComThread)
            {
                // check for a signal.
                if (threadComEvent.WaitOne())
                {
                    threadComEvent.Reset();
                    // The user tried to start another instance. We can't allow that, 
                    // so bring the other instance back into view and enable that one. 
                    // That form is created in another thread, so we need some thread sync magic.
                    if (!exitComThread)
                    {
                        ds4form?.Invoke(new SetFormVisableDelegate(ThreadFormVisable), ds4form);
                    }
                }
            }
        }

        /// <summary>
        /// When this method is called using a Invoke then this runs in the thread
        /// that created the form, which is nice. 
        /// </summary>
        /// <param name="frm"></param>
        private delegate void SetFormVisableDelegate(Form frm);
        private static void ThreadFormVisable(Form frm)
        {
            if (frm is Forms.DS4Form)
            {
                // display the form and bring to foreground.
                Forms.DS4Form temp = (Forms.DS4Form)frm;
                temp.Show();
                temp.WindowState = FormWindowState.Normal;
            }
        }
    }
}