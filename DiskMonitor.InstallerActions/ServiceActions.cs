using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using WixToolset.Dtf.WindowsInstaller;

namespace DiskMonitor.InstallerActions
{
    public static class ServiceActions
    {
        private const string ServiceName    = "DiskMonitor";
        private const string ServiceDisplay = "DiskMonitor";
        private const string ServiceDesc    = "Windows 磁盘 I/O 监控服务";

        private static string DefaultServiceExePath()
        {
            string pgData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(pgData, "DiskMonitor", "service", "DiskMonitor.Service.exe");
        }

        // ── Install actions ──────────────────────────────────────────────────

        // Deferred, elevated. Runs before WiX installs files.
        // Stops and deletes any existing DiskMonitor service (portable or prior MSI).
        [CustomAction]
        public static ActionResult MigratePortableService(Session session)
        {
            session.Log("MigratePortableService: begin");
            try
            {
                if (!ServiceExists(ServiceName))
                {
                    session.Log("MigratePortableService: service not found, nothing to migrate");
                    return ActionResult.Success;
                }
                session.Log("MigratePortableService: stopping existing service");
                StopService(ServiceName);
                Thread.Sleep(2000);
                session.Log("MigratePortableService: deleting service registration");
                DeleteServiceRegistration(ServiceName);
                Thread.Sleep(500);
                session.Log("MigratePortableService: done");
            }
            catch (Exception ex) { session.Log("MigratePortableService: " + ex.Message); }
            return ActionResult.Success;
        }

        // Deferred, elevated. Runs after files are installed.
        // Creates the service registration pointing to the ProgramData path.
        [CustomAction]
        public static ActionResult RegisterService(Session session)
        {
            session.Log("RegisterService: begin");
            try
            {
                string exePath = DefaultServiceExePath();
                if (!File.Exists(exePath))
                {
                    session.Log("RegisterService: service EXE not found at " + exePath);
                    return ActionResult.Failure;
                }

                // Ensure not already registered (shouldn't happen but be safe)
                if (ServiceExists(ServiceName))
                {
                    session.Log("RegisterService: already registered, skipping");
                    return ActionResult.Success;
                }

                RunSc("create " + ServiceName + " binPath= \"" + exePath + "\" start= auto displayname= \"" + ServiceDisplay + "\"");
                RunSc("description " + ServiceName + " \"" + ServiceDesc + "\"");
                session.Log("RegisterService: created service at " + exePath);
            }
            catch (Exception ex)
            {
                session.Log("RegisterService: " + ex.Message);
                return ActionResult.Failure;
            }
            return ActionResult.Success;
        }

        // Deferred, elevated. Starts the service after registration.
        [CustomAction]
        public static ActionResult StartService(Session session)
        {
            session.Log("StartService: begin");
            try
            {
                RunSc("start " + ServiceName);
                session.Log("StartService: started");
            }
            catch (Exception ex) { session.Log("StartService: " + ex.Message); }
            return ActionResult.Success;
        }

        // ── Uninstall actions ────────────────────────────────────────────────

        // Immediate. Shows MessageBox asking user whether to remove service.
        // Sets REMOVE_SERVICE property to "1" (yes) or "0" (no).
        [CustomAction]
        public static ActionResult AskRemoveService(Session session)
        {
            session.Log("AskRemoveService: begin");
            try
            {
                if (!ServiceExists(ServiceName))
                {
                    session.Log("AskRemoveService: service not registered, skipping prompt");
                    session["REMOVE_SERVICE"] = "0";
                    return ActionResult.Success;
                }

                const uint MB_YESNO        = 0x00000004;
                const uint MB_ICONQUESTION = 0x00000020;
                const uint MB_DEFBUTTON2   = 0x00000100;
                const int  IDYES           = 6;

                int result = MessageBox(
                    IntPtr.Zero,
                    "检测到 DiskMonitor 后台服务仍在运行。\n\n" +
                    "是否同时停止并卸载服务？\n\n" +
                    "• 选「是」：停止服务，删除服务注册及服务文件\n" +
                    "• 选「否」：服务继续运行，历史数据和服务文件保留不动",
                    "DiskMonitor 卸载",
                    MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2);

                session["REMOVE_SERVICE"] = result == IDYES ? "1" : "0";
                session.Log("AskRemoveService: user chose " + (result == IDYES ? "Yes" : "No"));
            }
            catch (Exception ex)
            {
                session.Log("AskRemoveService: " + ex.Message);
                session["REMOVE_SERVICE"] = "0";
            }
            return ActionResult.Success;
        }

        // Immediate. Passes REMOVE_SERVICE into the deferred action's CustomActionData.
        [CustomAction]
        public static ActionResult SetRemoveServiceData(Session session)
        {
            string flag = session["REMOVE_SERVICE"] ?? "0";
            session.Log("SetRemoveServiceData: REMOVE_SERVICE=" + flag);
            session["RemoveServiceConditional"] = "REMOVE_SERVICE=" + flag;
            return ActionResult.Success;
        }

        // Deferred, elevated. Stops + removes service if user said yes.
        [CustomAction]
        public static ActionResult RemoveServiceConditional(Session session)
        {
            session.Log("RemoveServiceConditional: begin");
            try
            {
                string flag = session.CustomActionData.ContainsKey("REMOVE_SERVICE")
                    ? session.CustomActionData["REMOVE_SERVICE"] : "0";

                if (flag != "1")
                {
                    session.Log("RemoveServiceConditional: user chose keep service, skipping");
                    return ActionResult.Success;
                }

                session.Log("RemoveServiceConditional: stopping service");
                StopService(ServiceName);
                Thread.Sleep(3000);

                session.Log("RemoveServiceConditional: deleting service registration");
                DeleteServiceRegistration(ServiceName);
                Thread.Sleep(500);

                string pgData  = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string svcDir  = Path.Combine(pgData, "DiskMonitor", "service");
                if (Directory.Exists(svcDir))
                {
                    session.Log("RemoveServiceConditional: removing " + svcDir);
                    try { Directory.Delete(svcDir, true); }
                    catch (Exception ex) { session.Log("RemoveServiceConditional: dir delete: " + ex.Message); }
                }

                // Clean up Windows Event Log source
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName               = "reg.exe",
                        Arguments              = @"delete ""HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\DiskMonitor"" /f",
                        CreateNoWindow         = true,
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                    };
                    Process.Start(psi)?.WaitForExit(5000);
                }
                catch (Exception ex) { session.Log("RemoveServiceConditional: reg delete: " + ex.Message); }

                session.Log("RemoveServiceConditional: done");
            }
            catch (Exception ex) { session.Log("RemoveServiceConditional: " + ex.Message); }
            return ActionResult.Success;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void RunSc(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "sc.exe",
                Arguments              = args,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            var p = Process.Start(psi) ?? throw new InvalidOperationException("sc.exe failed to start");
            p.WaitForExit(10000);
        }

        private static bool ServiceExists(string name)
        {
            IntPtr hScm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (hScm == IntPtr.Zero) return false;
            try
            {
                IntPtr hSvc = OpenService(hScm, name, SERVICE_QUERY_STATUS);
                if (hSvc == IntPtr.Zero) return false;
                CloseServiceHandle(hSvc);
                return true;
            }
            finally { CloseServiceHandle(hScm); }
        }

        private static void StopService(string name)
        {
            IntPtr hScm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (hScm == IntPtr.Zero) return;
            try
            {
                IntPtr hSvc = OpenService(hScm, name, SERVICE_STOP | SERVICE_QUERY_STATUS);
                if (hSvc == IntPtr.Zero) return;
                try
                {
                    var ss = new SERVICE_STATUS();
                    ControlService(hSvc, SERVICE_CONTROL_STOP, ref ss);
                    for (int i = 0; i < 20; i++)
                    {
                        QueryServiceStatus(hSvc, ref ss);
                        if (ss.dwCurrentState == SERVICE_STOPPED) break;
                        Thread.Sleep(500);
                    }
                }
                finally { CloseServiceHandle(hSvc); }
            }
            finally { CloseServiceHandle(hScm); }
        }

        private static void DeleteServiceRegistration(string name)
        {
            IntPtr hScm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (hScm == IntPtr.Zero) return;
            try
            {
                IntPtr hSvc = OpenService(hScm, name, SERVICE_DELETE);
                if (hSvc == IntPtr.Zero) return;
                try { WinDeleteService(hSvc); }
                finally { CloseServiceHandle(hSvc); }
            }
            finally { CloseServiceHandle(hScm); }
        }

        // ── P/Invoke ─────────────────────────────────────────────────────────

        private const uint SC_MANAGER_CONNECT   = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_STOP         = 0x0020;
        private const uint SERVICE_DELETE       = 0x10000;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const uint SERVICE_STOPPED      = 0x00000001;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hScm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hObj);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr hSvc, uint control, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr hSvc, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "DeleteService")]
        private static extern bool WinDeleteService(IntPtr hSvc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }
    }
}
