// «Установить Map.exe» — the small installer that ships in the release zip next to Map.App.exe.
// Copies the unpacked folder to %LOCALAPPDATA%\Programs\Map (no admin rights), makes the desktop
// and Start menu shortcuts, registers Map in «Приложения» for removal and starts Map.
// "/uninstall" (the command Windows runs from «Приложения») removes the program, its shortcuts and
// the registration; the user's maps (Документы\Map) and library (%LOCALAPPDATA%\Map) stay.
// Built by tools/release/publish-release.ps1 with the .NET Framework csc that every Windows 10/11 has,
// so it needs no packages and runs on any machine; C# 5 only.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Установка Map")]
[assembly: AssemblyProduct("Map — конструктор карт")]

internal static class MapSetup
{
    internal const string AppName = "Map — конструктор карт";
    internal const string AppExe = "Map.App.exe";
    internal const string SetupExe = "Установить Map.exe";
    // MAP_SETUP_TEST_ROOT (checks only): every folder goes under it and the registry key gets another name,
    // so a check never touches the real desktop, Start menu or «Приложения».
    private static readonly string TestRoot = Environment.GetEnvironmentVariable("MAP_SETUP_TEST_ROOT");

    internal static string UninstallKey
    {
        get { return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + (TestRoot == null ? "Map" : "Map.SetupCheck"); }
    }

    internal static string InstallDir
    {
        get { return Path.Combine(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData, "Local"), "Programs"), "Map"); }
    }

    internal static string DesktopLink
    {
        get { return Path.Combine(Folder(Environment.SpecialFolder.DesktopDirectory, "Desktop"), AppName + ".lnk"); }
    }

    internal static string StartMenuLink
    {
        get { return Path.Combine(Folder(Environment.SpecialFolder.Programs, "Start"), AppName + ".lnk"); }
    }

    private static string Folder(Environment.SpecialFolder folder, string testName)
    {
        if (TestRoot == null)
        {
            return Environment.GetFolderPath(folder);
        }

        var dir = Path.Combine(TestRoot, testName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var uninstall = HasArg(args, "uninstall");
        Silent = HasArg(args, "silent");
        try
        {
            if (uninstall)
            {
                return Uninstall();
            }

            var source = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (Silent)
            {
                // "/silent": defaults (Start menu + desktop shortcut), no window, Map is not started.
                if (!File.Exists(Path.Combine(source, AppExe)))
                {
                    throw new FileNotFoundException(AppExe + " is not next to the installer.");
                }

                if (MapIsRunning())
                {
                    throw new IOException("Map is running.");
                }

                var form = new SetupForm(source);
                form.InstallNow(true);
                return 0;
            }

            if (!File.Exists(Path.Combine(source, AppExe)))
            {
                MessageBox.Show(
                    "Рядом с установщиком нет файлов программы.\n\n" +
                    "Сначала распакуйте архив целиком: правой кнопкой по архиву → «Извлечь всё…». " +
                    "Потом откройте распакованную папку и запустите «" + SetupExe + "».",
                    "Установка Map", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            Application.Run(new SetupForm(source));
            return 0;
        }
        catch (Exception ex)
        {
            if (Silent)
            {
                Console.Error.WriteLine(ex.Message);
            }
            else
            {
                MessageBox.Show("Не получилось: " + ex.Message, "Установка Map", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
    }

    internal static bool Silent;

    private static bool HasArg(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (string.Equals(a.TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool MapIsRunning()
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe));
        foreach (var p in running)
        {
            p.Dispose();
        }

        return running.Length > 0;
    }

    internal static string VersionOf(string exe)
    {
        var info = FileVersionInfo.GetVersionInfo(exe);
        var v = info.ProductVersion ?? info.FileVersion ?? "";
        var plus = v.IndexOf('+');
        return plus > 0 ? v.Substring(0, plus) : v;
    }

    internal static void MakeShortcut(string link, string target, string workDir)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        var shell = Activator.CreateInstance(shellType);
        var lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
        var lnkType = lnk.GetType();
        lnkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
        lnkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
        lnkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
        lnkType.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { AppName });
        lnkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }

    internal static void Register(string dir, string version, long bytes)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            var exe = Path.Combine(dir, AppExe);
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "dmfromtheash");
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", "\"" + Path.Combine(dir, SetupExe) + "\" /uninstall");
            key.SetValue("URLInfoAbout", "https://github.com/dmfromtheash/map");
            key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    private static int Uninstall()
    {
        if (!Silent && MessageBox.Show(
                "Удалить Map с этого компьютера?\n\nВаши карты в «Документы\\Map» и библиотека ассетов останутся.",
                "Удаление Map", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return 1;
        }

        while (MapIsRunning())
        {
            if (Silent)
            {
                throw new IOException("Map is running.");
            }

            if (MessageBox.Show("Map сейчас открыта. Закройте её и нажмите «Повторить».", "Удаление Map",
                    MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
            {
                return 1;
            }
        }

        DeleteQuietly(DesktopLink);
        DeleteQuietly(StartMenuLink);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        // This exe lives in the folder being removed, so a hidden cmd removes it after we exit.
        var dir = InstallDir;
        if (Directory.Exists(dir))
        {
            var psi = new ProcessStartInfo("cmd.exe",
                "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            using (Process.Start(psi))
            {
            }
        }

        if (!Silent)
        {
            MessageBox.Show("Map удалена.", "Удаление Map", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return 0;
    }

    private static void DeleteQuietly(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class SetupForm : Form
{
    private readonly string _source;
    private readonly string _version;
    private readonly CheckBox _desktop;
    private readonly CheckBox _launch;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly Button _install;
    private readonly Button _cancel;

    public SetupForm(string source)
    {
        _source = source;
        _version = MapSetup.VersionOf(Path.Combine(source, MapSetup.AppExe));

        Text = "Установка Map";
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Path.Combine(source, MapSetup.AppExe));
        }
        catch (ArgumentException)
        {
        }

        var layout = new TableLayoutPanel();
        layout.ColumnCount = 1;
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.Padding = new Padding(20, 16, 20, 16);
        layout.Dock = DockStyle.Fill;

        var title = new Label();
        title.Text = MapSetup.AppName;
        title.Font = new Font("Segoe UI Semibold", 15f);
        title.AutoSize = true;
        title.Margin = new Padding(0, 0, 0, 2);
        layout.Controls.Add(title);

        var version = new Label();
        version.Text = "Версия " + _version;
        version.ForeColor = SystemColors.GrayText;
        version.AutoSize = true;
        version.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(version);

        var where = new Label();
        where.Text = "Программа будет установлена в папку:\n" + MapSetup.InstallDir +
            "\n\nПрава администратора не нужны. Карты хранятся в «Документы\\Map».";
        where.AutoSize = true;
        where.MaximumSize = new Size(460, 0);
        where.Margin = new Padding(0, 0, 0, 12);
        layout.Controls.Add(where);

        _desktop = new CheckBox();
        _desktop.Text = "Ярлык на рабочем столе";
        _desktop.Checked = true;
        _desktop.AutoSize = true;
        layout.Controls.Add(_desktop);

        _launch = new CheckBox();
        _launch.Text = "Открыть Map после установки";
        _launch.Checked = true;
        _launch.AutoSize = true;
        _launch.Margin = new Padding(3, 3, 3, 12);
        layout.Controls.Add(_launch);

        _progress = new ProgressBar();
        _progress.Width = 460;
        _progress.Height = 18;
        _progress.Visible = false;
        layout.Controls.Add(_progress);

        _status = new Label();
        _status.AutoSize = true;
        _status.MaximumSize = new Size(460, 0);
        _status.Margin = new Padding(0, 4, 0, 8);
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.AutoSize = true;
        buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        buttons.Dock = DockStyle.Fill;
        buttons.Margin = new Padding(0);

        _cancel = new Button();
        _cancel.Text = "Отмена";
        _cancel.AutoSize = true;
        _cancel.MinimumSize = new Size(100, 32);
        _cancel.Click += delegate { Close(); };
        buttons.Controls.Add(_cancel);

        _install = new Button();
        _install.Text = "Установить";
        _install.AutoSize = true;
        _install.MinimumSize = new Size(120, 32);
        _install.Click += delegate { StartInstall(); };
        buttons.Controls.Add(_install);

        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = _install;
        CancelButton = _cancel;
    }

    private void StartInstall()
    {
        if (MapSetup.MapIsRunning())
        {
            _status.Text = "Map сейчас открыта. Закройте её и нажмите «Установить» ещё раз.";
            _status.ForeColor = Color.Firebrick;
            return;
        }

        _install.Enabled = false;
        _cancel.Enabled = false;
        _desktop.Enabled = false;
        _launch.Enabled = false;
        _progress.Visible = true;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Копирую файлы…";
        var desktop = _desktop.Checked;
        var thread = new Thread(delegate ()
        {
            string exe = null, target = null, error = null;
            try
            {
                InstallNow(desktop);
                target = MapSetup.InstallDir;
                exe = Path.Combine(target, MapSetup.AppExe);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            BeginInvoke((MethodInvoker)delegate { Finish(exe, target, error); });
        });
        thread.IsBackground = true;
        thread.Start();
    }

    /// <summary>Copies the program, makes the shortcuts and registers Map; throws on failure.</summary>
    internal void InstallNow(bool desktop)
    {
        // (one block: the copy, the shortcuts and the registration)
        {
            var target = MapSetup.InstallDir;
            var sameFolder = string.Equals(Path.GetFullPath(_source).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
            var files = Directory.GetFiles(_source, "*", SearchOption.AllDirectories);
            long bytes = 0;
            foreach (var f in files)
            {
                bytes += new FileInfo(f).Length;
            }

            if (!sameFolder)
            {
                // An update replaces the whole program folder so files of the old version do not linger.
                if (Directory.Exists(target))
                {
                    if (!File.Exists(Path.Combine(target, MapSetup.AppExe)) &&
                        Directory.GetFileSystemEntries(target).Length > 0)
                    {
                        throw new IOException("Папка " + target + " занята чем-то другим, а не Map. Уберите её и повторите.");
                    }

                    Directory.Delete(target, true);
                }

                long copied = 0;
                foreach (var f in files)
                {
                    var rel = f.Substring(_source.TrimEnd('\\').Length + 1);
                    var dest = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f, dest, true);
                    copied += new FileInfo(f).Length;
                    var percent = (int)(copied * 100 / Math.Max(1, bytes));
                    if (IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate { _progress.Value = percent; });
                    }
                }
            }

            var exe = Path.Combine(target, MapSetup.AppExe);
            MapSetup.MakeShortcut(MapSetup.StartMenuLink, exe, target);
            if (desktop)
            {
                MapSetup.MakeShortcut(MapSetup.DesktopLink, exe, target);
            }

            MapSetup.Register(target, _version, bytes);
        }
    }

    private void Finish(string exe, string target, string error)
    {
        _progress.Value = error == null ? 100 : _progress.Value;
        if (error != null)
        {
            _status.Text = "Не получилось: " + error;
            _status.ForeColor = Color.Firebrick;
            _install.Enabled = true;
            _cancel.Enabled = true;
            _desktop.Enabled = true;
            _launch.Enabled = true;
            return;
        }

        _status.Text = "Готово. Map — в меню «Пуск»" + (_desktop.Checked ? " и на рабочем столе." : ".") +
            " Распакованную папку теперь можно удалить.";
        if (_launch.Checked)
        {
            var psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = target;
            psi.UseShellExecute = true;
            using (Process.Start(psi))
            {
            }

            Close();
            return;
        }

        _cancel.Text = "Закрыть";
        _cancel.Enabled = true;
    }
}
