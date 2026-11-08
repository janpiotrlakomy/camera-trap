// AVI -> MP4 converter. ffmpeg.exe is embedded in this exe (see build.bat), so it's a single file to download.
// camera-trap is licensed under the GNU GPL version 3 (see LICENSE).
// It bundles FFmpeg 9.0.2 (https://ffmpeg.org), also GPL version 3, see ffmpeg-LICENSE.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

class MainForm : Form
{
    ListView list = new ListView();
    FlowLayoutPanel buttons = new FlowLayoutPanel();
    ProgressBar progress = new ProgressBar();
    Label status = new Label();
    HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool busy;

    // All file/folder pickers start in the current user's folder (C:\Users\<name>).
    static readonly string UserFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Files/folders dropped onto the .exe arrive as args.
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        var form = new MainForm();
        form.AddPaths(args);
        Application.Run(form);
    }

    MainForm()
    {
        Text = "camera-trap";
        Font = new Font("Segoe UI", 9f);
        Size = new Size(760, 500);
        StartPosition = FormStartPosition.CenterScreen;

        list.View = View.Details;
        list.FullRowSelect = true;
        list.Dock = DockStyle.Fill;
        list.AllowDrop = true;
        list.Columns.Add("File", 560);
        list.Columns.Add("Status", 160);
        list.DragEnter += (s, e) => e.Effect = DragDropEffects.Copy;
        list.DragDrop += (s, e) => AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));

        buttons.Dock = DockStyle.Top;
        buttons.AutoSize = true;
        AddButton("Add files...", AddFiles);
        AddButton("Add folder...", AddFolder);
        AddButton("Clear", (s, e) => { list.Items.Clear(); added.Clear(); });
        AddButton("Convert", ConvertAll);

        progress.Dock = DockStyle.Bottom;
        status.Dock = DockStyle.Bottom;
        status.Height = 26;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Text = "Add .avi files or a folder (subfolders included), or drag them here. Then click Convert.";

        // Fill control first, so the docked edges are laid out around it.
        Controls.Add(list);
        Controls.Add(buttons);
        Controls.Add(progress);
        Controls.Add(status);

        FormClosing += (s, e) =>
        {
            if (!busy) return;
            if (MessageBox.Show(this, "A conversion is running. Quit anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                e.Cancel = true;
            else
                try { ffmpeg.Kill(); } catch { }  // otherwise it keeps running in the background
        };
    }

    void AddButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        buttons.Controls.Add(b);
    }

    // ---------- adding files ----------

    void AddFiles(object sender, EventArgs e)
    {
        using (var dlg = new OpenFileDialog { Filter = "AVI videos (*.avi)|*.avi", Multiselect = true, InitialDirectory = UserFolder })
            if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
    }

    void AddFolder(object sender, EventArgs e)
    {
        string folder = PickFolder("Add folder (subfolders are included)");
        if (folder != null) AddPaths(new[] { folder });
    }

    void AddPaths(IEnumerable<string> paths)
    {
        if (busy) return;
        foreach (string p in paths)
        {
            string path = Path.GetFullPath(p);
            if (Directory.Exists(path))
            {
                var files = new List<string>();
                FindAvis(path, files);
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files) AddFile(f, path);
            }
            else if (IsAvi(path)) AddFile(path, Path.GetDirectoryName(path));
        }
        status.Text = list.Items.Count + " file(s) in the list.";
    }

    // baseFolder is the folder the user picked; subfolders below it are recreated in the output folder.
    void AddFile(string file, string baseFolder)
    {
        if (!added.Add(file)) return;
        var item = new ListViewItem(new[] { file, "Waiting" });
        item.Tag = baseFolder;
        list.Items.Add(item);
    }

    // Skips folders we can't read (e.g. "System Volume Information" on an SD card).
    static void FindAvis(string folder, List<string> files)
    {
        try
        {
            foreach (string f in Directory.GetFiles(folder)) if (IsAvi(f)) files.Add(f);
            foreach (string d in Directory.GetDirectories(folder)) FindAvis(d, files);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    static bool IsAvi(string path)
    {
        return string.Equals(Path.GetExtension(path), ".avi", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- converting ----------

    async void ConvertAll(object sender, EventArgs e)
    {
        if (list.Items.Count == 0)
        {
            status.Text = "Add some .avi files first.";
            return;
        }
        string outputFolder = PickFolder("Choose where to save the MP4 files");
        if (outputFolder == null) return;

        busy = true;
        buttons.Enabled = false;
        progress.Maximum = Math.Max(1, list.Items.Count);
        progress.Value = 0;
        int ok = 0, failed = 0;

        foreach (ListViewItem item in list.Items)
        {
            item.SubItems[1].Text = "Converting...";
            item.EnsureVisible();
            string avi = item.Text;
            string mp4 = OutputPath(avi, (string)item.Tag, outputFolder);
            string result = await Task.Run(() => ConvertFile(avi, mp4));
            item.SubItems[1].Text = result;
            if (result == "Done" || result.StartsWith("Skipped")) ok++;
            else { failed++; item.ForeColor = Color.Firebrick; }
            progress.Value++;
        }

        status.Text = "Finished: " + ok + " OK, " + failed + " failed. Saved to " + outputFolder;
        buttons.Enabled = true;
        busy = false;
    }

    // Keeps the subfolders below the folder the user added
    // (trail cameras reuse names like IMAG0001.AVI in different folders).
    static string OutputPath(string avi, string baseFolder, string outputFolder)
    {
        string relative = Path.ChangeExtension(avi, ".mp4").Substring(baseFolder.Length).TrimStart('\\');
        return Path.Combine(outputFolder, relative);
    }

    static Process ffmpeg;  // the running conversion, so it can be stopped when the app is closed

    // Runs on a background thread. Writes to "name.mp4.part" and renames when finished,
    // so an interrupted conversion never leaves a broken .mp4 behind.
    static string ConvertFile(string avi, string mp4)
    {
        if (File.Exists(mp4)) return "Skipped (MP4 exists)";
        string part = mp4 + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mp4));
            // H.264 + AAC: plays everywhere. crf 28 + veryfast: ~1/5 of ffmpeg's default size and 3x faster
            // on grainy night footage, with the animals just as visible.
            var info = new ProcessStartInfo(FfmpegPath(), string.Format(
                "-hide_banner -loglevel error -y -i \"{0}\" -c:v libx264 -preset veryfast -crf 28 -pix_fmt yuv420p " +
                "-c:a aac -b:a 128k -movflags +faststart -f mp4 \"{1}\"", avi, part));
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            using (ffmpeg = Process.Start(info))
            {
                string errors = ffmpeg.StandardError.ReadToEnd();
                ffmpeg.WaitForExit();
                if (ffmpeg.ExitCode != 0)
                {
                    File.Delete(part);
                    string[] lines = errors.Trim().Split('\n');
                    return "Error: " + lines[lines.Length - 1].Trim();
                }
            }
            File.Move(part, mp4);
            return "Done";
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            return "Error: " + ex.Message;
        }
    }

    // Unpacks the embedded ffmpeg.exe to %LOCALAPPDATA%\camera-trap once (the size in the
    // name means a new version of the app with a different ffmpeg unpacks its own copy).
    static string FfmpegPath()
    {
        using (Stream embedded = typeof(MainForm).Assembly.GetManifestResourceStream("ffmpeg.exe"))
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "camera-trap");
            string path = Path.Combine(folder, "ffmpeg-" + embedded.Length + ".exe");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(folder);
                foreach (string old in Directory.GetFiles(folder, "ffmpeg-*"))  // copies from older versions
                    try { File.Delete(old); } catch { }
                using (var file = File.Create(path + ".tmp")) embedded.CopyTo(file);
                File.Move(path + ".tmp", path);
            }
            return path;
        }
    }

    // ---------- folder picker ----------

    // The same Explorer-style window as the file picker, in folder mode, always starting in the user's folder.
    // (.NET Framework's FolderBrowserDialog is the old tree view, so we use the Windows dialog directly.)
    string PickFolder(string title)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
        dialog.SetTitle(title);
        IShellItem start;
        SHCreateItemFromParsingName(UserFolder, IntPtr.Zero, typeof(IShellItem).GUID, out start);
        dialog.SetFolder(start);
        if (dialog.Show(Handle) != 0) return null;  // cancelled

        IShellItem result;
        dialog.GetResult(out result);
        string path;
        result.GetDisplayName(SIGDN_FILESYSPATH, out path);
        return path;
    }

    const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, SIGDN_FILESYSPATH = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialog { }

    // Methods must stay in this exact order (it mirrors the Windows interface); unused ones are placeholders.
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
