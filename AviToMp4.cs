// AVI -> MP4 converter using the video engine built into Windows 10/11.
// Build with build.bat - nothing needs to be installed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Foundation;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

class MainForm : Form
{
    ListView list = new ListView();
    FlowLayoutPanel buttons = new FlowLayoutPanel();
    ProgressBar progress = new ProgressBar();
    Label status = new Label();
    HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool busy;

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
        Text = "AVI to MP4";
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
            if (busy && MessageBox.Show(this, "A conversion is running. Quit anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                e.Cancel = true;
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
        using (var dlg = new OpenFileDialog { Filter = "AVI videos (*.avi)|*.avi", Multiselect = true })
            if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(dlg.FileNames);
    }

    void AddFolder(object sender, EventArgs e)
    {
        using (var dlg = new FolderBrowserDialog { Description = "All .avi files in this folder and its subfolders will be added." })
            if (dlg.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dlg.SelectedPath });
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
        string outputFolder;
        using (var dlg = new FolderBrowserDialog { Description = "Choose where to save the MP4 files." })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            outputFolder = dlg.SelectedPath;
        }

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

    // Runs on a background thread. Writes to "name.mp4.part" and renames when finished,
    // so an interrupted conversion never leaves a broken .mp4 behind.
    static string ConvertFile(string avi, string mp4)
    {
        if (File.Exists(mp4)) return "Skipped (MP4 exists)";
        string part = mp4 + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mp4));
            StorageFile source = Wait(StorageFile.GetFileFromPathAsync(avi));
            StorageFolder folder = Wait(StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(mp4)));
            StorageFile target = Wait(folder.CreateFileAsync(Path.GetFileName(part), CreationCollisionOption.ReplaceExisting));

            PrepareTranscodeResult transcode = Wait(new MediaTranscoder().PrepareFileTranscodeAsync(source, target, MakeProfile(source)));
            if (!transcode.CanTranscode)
            {
                File.Delete(part);
                return "Unsupported format";
            }
            WaitFor(transcode.TranscodeAsync());
            File.Move(part, mp4);
            return "Done";
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            if (ex.HResult == unchecked((int)0xC00D36C4)) return "Unsupported format";
            return "Error: " + ex.Message;
        }
    }

    // H.264/AAC MP4 with the same resolution and frame rate as the original.
    // (Windows' own "Auto" profile doesn't work reliably, so we copy the values ourselves.)
    static MediaEncodingProfile MakeProfile(StorageFile source)
    {
        MediaEncodingProfile original = Wait(MediaEncodingProfile.CreateFromFileAsync(source));
        MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        if (original.Video != null)
        {
            profile.Video.Width = original.Video.Width;
            profile.Video.Height = original.Video.Height;
            double fps = 30;
            if (original.Video.FrameRate.Numerator > 0 && original.Video.FrameRate.Denominator > 0)
            {
                profile.Video.FrameRate.Numerator = original.Video.FrameRate.Numerator;
                profile.Video.FrameRate.Denominator = original.Video.FrameRate.Denominator;
                fps = (double)original.Video.FrameRate.Numerator / original.Video.FrameRate.Denominator;
            }
            // ~0.1 bits per pixel per frame: good quality without bloated files
            profile.Video.Bitrate = (uint)Math.Max(500000, original.Video.Width * original.Video.Height * fps * 0.1);
        }
        if (original.Audio == null) profile.Audio = null;
        return profile;
    }

    // Blocks until a Windows async operation finishes (fine here: we're on a background thread).
    static void WaitFor(IAsyncInfo operation)
    {
        while (operation.Status == AsyncStatus.Started) Thread.Sleep(50);
        if (operation.Status != AsyncStatus.Completed) throw operation.ErrorCode;
    }

    static T Wait<T>(IAsyncOperation<T> operation)
    {
        WaitFor(operation);
        return operation.GetResults();
    }
}
