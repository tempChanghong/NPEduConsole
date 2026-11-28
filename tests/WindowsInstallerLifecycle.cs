// Build with the Windows .NET Framework x64 compiler. This test starts real installers.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal static class WindowsInstallerLifecycle
{
    private const string ExeName = "NPEduConsole.App.exe";
    private const string ConsoleTitlePrefix = "NPEduConsole ";
    private const string Registration = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NovarkPower.NPEduConsole.CurrentUser_is1";
    private static readonly List<string> Checks = new List<string>();
    private static readonly List<Process> ConsoleWindows = new List<Process>();
    private static string evidence;
    private static string installed;
    private static string settings;
    private static string shortcut;

    private delegate bool WindowVisitor(IntPtr window, IntPtr state);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(IntPtr desktop, WindowVisitor callback, IntPtr state);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class Release
    {
        public string Version, Setup, Zip;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--isolated-guest")
        {
            Console.Error.WriteLine("Usage: WindowsInstallerLifecycle.exe --isolated-guest <first-release> <next-release> <new-evidence-dir>");
            return 2;
        }
        try { return VerifyLifecycle(args[1], args[2], args[3]); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static int VerifyLifecycle(string firstRelease, string nextRelease, string reportPath)
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool administrator = identity.Groups.Any(group => group.Value == "S-1-5-32-544");
        if (!Environment.Is64BitProcess || administrator || identity.IsSystem)
            throw new InvalidOperationException("A dedicated standard Windows account in an x64 guest is required.");
        using (var query = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
        using (var systems = query.Get())
        {
            var guest = systems.Cast<ManagementBaseObject>().Any(system =>
                System.Text.RegularExpressions.Regex.IsMatch(system["Manufacturer"] + " " + system["Model"],
                    "VMware|Virtual Machine|VirtualBox|Parallels|QEMU|KVM", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (!guest) throw new InvalidOperationException("A Windows virtual machine is required.");
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        installed = Path.GetFullPath(Path.Combine(local, "Programs", "NPEduConsole"));
        settings = Path.Combine(local, "NPEduConsole");
        shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NPEduConsole.lnk");
        string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NPEduConsole.lnk");
        string main = Path.Combine(installed, ExeName);
        evidence = Path.GetFullPath(reportPath);
        if (Directory.Exists(installed) || Directory.Exists(settings) || File.Exists(shortcut) ||
            File.Exists(desktopShortcut) || HasRegistration(RegistryHive.CurrentUser) ||
            HasRegistration(RegistryHive.LocalMachine) || Directory.Exists(evidence))
            throw new InvalidOperationException("Use a clean account and a new evidence directory.");

        Directory.CreateDirectory(evidence);
        var result = new Dictionary<string, object> {
            { "status", "FAILED" }, { "user", identity.Name }, { "sid", identity.User.Value },
            { "standardUser", !administrator }, { "sessionId", Process.GetCurrentProcess().SessionId },
            { "interactive", Environment.UserInteractive }, { "localAppData", local }, { "checks", Checks }
        };
        try
        {
            Check(true, "no prior install, profile, shortcut or registration");
            Release first = ReadRelease(firstRelease);
            Release next = ReadRelease(nextRelease);
            RunSetup(first.Setup, "first-install", true);
            string uninstaller = CheckInstalled(first, desktopShortcut);
            string server = Path.Combine(settings, "last-server.txt");
            File.WriteAllText(server, "https://school-fixture.invalid/");
            string pending = Path.Combine(Directory.CreateDirectory(Path.Combine(settings, "staff-configuration-requests")).FullName, "fixture.json");
            File.WriteAllText(pending, "{\"test\":\"preserve this file\"}");
            string serverHash = Hash(server), pendingHash = Hash(pending), firstHash = Hash(main);

            Process running = StartConsole(main);
            RunSetup(next.Setup, "upgrade-while-running", false);
            RunSetup(uninstaller, "uninstall-while-running", false);
            Check(LogContains("upgrade-while-running", "Could not lock this user's NPEduConsole installation.") &&
                LogContains("uninstall-while-running", "Could not lock this user's NPEduConsole installation."),
                "the installed Console blocked both maintenance operations through the installation lock");
            Check(!running.HasExited && Hash(main) == firstHash, "active installed Console was neither stopped nor overwritten");
            CloseConsole(running);

            string portable = Path.Combine(local, "NPEduConsoleInstallerTest-portable");
            ZipFile.ExtractToDirectory(first.Zip, portable);
            Process portableWindow = StartConsole(Path.Combine(portable, ExeName));
            RunSetup(next.Setup, "upgrade-in-place", true);
            uninstaller = CheckInstalled(next, desktopShortcut);
            Check(Hash(main) != firstHash && FileVersionInfo.GetVersionInfo(main).FileVersion !=
                FileVersionInfo.GetVersionInfo(Path.Combine(portable, ExeName)).FileVersion, "installed executable advanced to the second build");
            RunSetup(first.Setup, "downgrade", false);
            using (var entry = Registry.CurrentUser.OpenSubKey(Registration))
                Check(LogContains("downgrade", "A newer version is already installed.") &&
                    (string)entry.GetValue("DisplayVersion") == next.Version, "downgrade kept the newer registration");

            RunSetup(uninstaller, "uninstall", true);
            WaitForUninstaller();
            Check(!HasRegistration(RegistryHive.CurrentUser) && !Directory.Exists(installed) && !File.Exists(shortcut) &&
                !portableWindow.HasExited, "uninstall removed the installed product and left the portable app running");
            Check(Hash(server) == serverHash && Hash(pending) == pendingHash, "uninstall retained server and pending request files");

            RunSetup(next.Setup, "reinstall", true);
            CheckInstalled(next, desktopShortcut);
            CloseConsole(StartConsole(main));
            Check(Hash(server) == serverHash && Hash(pending) == pendingHash,
                "upgrade, uninstall and reinstall retained server and pending request files");
            result["status"] = "PASSED";
            return 0;
        }
        catch (Exception error)
        {
            result["error"] = error.ToString();
            throw;
        }
        finally
        {
            foreach (Process process in ConsoleWindows)
            {
                try { CloseConsole(process); }
                catch (Exception error) { Console.Error.WriteLine(error); }
                process.Dispose();
            }
            File.WriteAllText(Path.Combine(evidence, "result.json"), new JavaScriptSerializer().Serialize(result));
        }
    }

    private static bool HasRegistration(RegistryHive hive)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var entry = root.OpenSubKey(Registration))
                if (entry != null) return true;
        return false;
    }

    private static Release ReadRelease(string directory)
    {
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
            File.ReadAllText(Path.Combine(directory, "console-release.json")));
        string version = (string)manifest["version"];
        string fileName = "NPEduConsole-" + version + "-win-x64";
        string setup = Path.Combine(directory, fileName + "-setup.exe");
        string zip = Path.Combine(directory, fileName + ".zip");
        string expectedSetup = File.ReadAllText(setup + ".sha256").Trim().Split(' ')[0];
        Check((string)manifest["fileName"] == Path.GetFileName(zip) &&
            new FileInfo(zip).Length == Convert.ToInt64(manifest["sizeBytes"], CultureInfo.InvariantCulture) &&
            Same(Hash(zip), (string)manifest["sha256"]) && Same(Hash(setup), expectedSetup),
            "SHA-256 for " + version + " EXE and ZIP");
        return new Release { Version = version, Setup = setup, Zip = zip };
    }

    private static string CheckInstalled(Release release, string desktopShortcut)
    {
        using (var entry = Registry.CurrentUser.OpenSubKey(Registration))
        {
            Check(entry != null && (string)entry.GetValue("DisplayVersion") == release.Version &&
                Same(((string)entry.GetValue("Inno Setup: App Path")).TrimEnd('\\', '/'), installed) &&
                !HasRegistration(RegistryHive.LocalMachine), "per-user registration " + release.Version + " at original directory");
            Check(File.Exists(shortcut) && !File.Exists(desktopShortcut), "Start menu entry and optional desktop shortcut");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(shortcut);
            Check(Same((string)link.TargetPath, Path.Combine(installed, ExeName)) && Same((string)link.WorkingDirectory, installed),
                "Start menu shortcut points to installed Console");
            using (ZipArchive archive = ZipFile.OpenRead(release.Zip))
                foreach (ZipArchiveEntry member in archive.Entries.Where(entryFile => entryFile.Name.Length > 0))
                {
                    string file = Path.GetFullPath(Path.Combine(installed, member.FullName));
                    if (!file.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unexpected ZIP path.");
                    using (Stream stream = member.Open())
                        if (Hash(file) != Hash(stream)) throw new InvalidOperationException("ZIP payload differs at " + member.FullName);
                }
            Check(true, "all installed files match the same-build ZIP for " + release.Version);
            string unregister = (string)entry.GetValue("UninstallString");
            var match = System.Text.RegularExpressions.Regex.Match(unregister, "^\"([^\"]+)\"$");
            if (!match.Success) throw new InvalidOperationException("Unexpected uninstall command.");
            string uninstaller = Path.GetFullPath(match.Groups[1].Value);
            if (!Same(Path.GetDirectoryName(uninstaller), installed) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(uninstaller), @"^unins\d+\.exe$"))
                throw new InvalidOperationException("Unexpected uninstaller path " + uninstaller);
            return uninstaller;
        }
    }

    private static void RunSetup(string executable, string scenario, bool succeeds)
    {
        string log = Path.Combine(evidence, scenario + ".log");
        using (Process process = Process.Start(new ProcessStartInfo(executable,
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=english /LOG=\"" + log + "\"") { UseShellExecute = false }))
        {
            if (!process.WaitForExit(90000)) throw new TimeoutException(scenario + ": see " + log);
            Check((process.ExitCode == 0) == succeeds, scenario + " exit code " + process.ExitCode);
        }
    }

    private static bool LogContains(string scenario, string message)
    {
        using (var stream = new FileStream(Path.Combine(evidence, scenario + ".log"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd().IndexOf(message, StringComparison.Ordinal) >= 0;
    }

    private static void WaitForUninstaller()
    {
        // The registered launcher may return while Inno's second phase still holds the lease.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string lease = Path.Combine(settings, "installation.lock");
        do
        {
            if (LogContains("uninstall", "Uninstallation process succeeded.") && LogContains("uninstall", "Log closed."))
            {
                try
                {
                    using (File.Open(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    Check(true, "Inno uninstaller finished its second phase and released the installation lock");
                    return;
                }
                catch (IOException) { }
            }
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("The registered uninstaller did not finish and release its installation lock.");
    }

    private static Process StartConsole(string executable)
    {
        Process process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
        ConsoleWindows.Add(process);
        var deadline = DateTime.UtcNow.AddSeconds(25);
        do
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("Console exited before creating a window: " + executable + "; code " + process.ExitCode);
            IntPtr window = FindConsoleWindow(process.Id);
            if (window != IntPtr.Zero)
            {
                Check(true, "real Console HWND " + window + " opened from " + executable);
                return process;
            }
            Thread.Sleep(150);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Console did not create a main window: " + executable);
    }

    private static void CloseConsole(Process process)
    {
        process.Refresh();
        if (process.HasExited) return;
        IntPtr window = FindConsoleWindow(process.Id);
        if (window == IntPtr.Zero || !PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero) || !process.WaitForExit(12000))
            throw new InvalidOperationException("Could not close test Console normally: " + process.Id);
    }

    private static IntPtr FindConsoleWindow(int processId)
    {
        // The main window can be hidden by an isolated window station even after it has opened.
        IntPtr result = IntPtr.Zero;
        var title = new StringBuilder(256);
        EnumDesktopWindows(GetThreadDesktop(GetCurrentThreadId()), delegate(IntPtr window, IntPtr state)
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == processId && GetWindowText(window, title, title.Capacity) > 0 &&
                title.ToString().StartsWith(ConsoleTitlePrefix, StringComparison.Ordinal))
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Checks.Add(label);
        Console.WriteLine("PASS " + label);
    }
    private static string Hash(string path) { using (Stream source = File.OpenRead(path)) return Hash(source); }
    private static string Hash(Stream source) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(source)).Replace("-", ""); }
    private static bool Same(string left, string right) { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
}
