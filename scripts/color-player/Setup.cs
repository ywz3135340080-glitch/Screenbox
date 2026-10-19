using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
[assembly: AssemblyTitle("Screenbox 安装程序")]
[assembly: AssemblyProduct("Screenbox HDR")]
[assembly: AssemblyVersion("0.2.0.1")]
internal static class SetupProgram
{
    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length == 2 && args[0] == "--render") {
            using(var f = new SetupForm(false)) {
                f.Show(); Application.DoEvents();
                using(var b = new Bitmap(f.Width,f.Height)) {
                    f.DrawToBitmap(b,new Rectangle(0,0,f.Width,f.Height));
                    b.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);
                }
                f.Close();
            }
            return 0;
        }
        if(args.Length == 1 && args[0] == "--self-test") {
            string root = SetupForm.NewStage();
            try {
                SetupForm.Extract(root);
                using(var z = ZipFile.OpenRead(Path.Combine(root,"ScreenboxHDR.msix"))) {
                    if(z.GetEntry("Screenbox.NativeColor.dll") == null) return 2;
                    var x = new XmlDocument();
                    using(var s = z.GetEntry("AppxManifest.xml").Open()) x.Load(s);
                    if(x.DocumentElement == null) return 3;
                }
                return 0;
            } catch(Exception e) { File.WriteAllText("setup-selftest.txt",e.ToString()); return 1; }
            finally { SetupForm.Cleanup(root); }
        }
        Application.Run(new SetupForm(args.Length == 1 && args[0] == "--install"));
        return 0;
    }
}
internal sealed class SetupForm : Form
{
    private Label status, description;
    private Button install, close;
    private ProgressBar progress;
    private LinkLabel copy;
    private bool busy, complete;
    private volatile string appId = "";
    private string error = "";
    private readonly StringBuilder log = new StringBuilder();
    internal SetupForm(bool start) {
        Text="Screenbox 安装"; ClientSize=new Size(550,330);
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
        Font=new Font("Microsoft YaHei UI",10F); BackColor=Color.FromArgb(247,248,251);
        ForeColor=Color.FromArgb(32,35,43); FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterScreen; MaximizeBox=false; MinimizeBox=false; ShowIcon=false;
        Controls.Add(new Label {Text="Screenbox HDR",Font=new Font("Segoe UI",22F,FontStyle.Bold),AutoSize=true,Location=new Point(30,30)});
        Controls.Add(new Label {Text="熟悉的 Screenbox 界面，改进杜比视频的色彩播放",AutoSize=true,Location=new Point(33,82),ForeColor=Color.DimGray});
        description=new Label {Text="点击“安装”，程序会自动检查所需组件。\r\n安装完成后，可从开始菜单打开播放器。",Location=new Point(33,128),Size=new Size(484,50)};
        Controls.Add(description);
        progress=new ProgressBar {Location=new Point(33,192),Size=new Size(484,7)}; Controls.Add(progress);
        status=new Label {Text="准备就绪",Location=new Point(30,211),Size=new Size(486,40),ForeColor=Color.DimGray}; Controls.Add(status);
        Controls.Add(new Panel {BackColor=Color.FromArgb(224,227,234),Location=new Point(0,263),Size=new Size(550,1)});
        var license=new LinkLabel {Text="开源信息",AutoSize=true,Location=new Point(30,288),LinkColor=Color.DimGray};
        license.LinkClicked+=delegate {MessageBox.Show(this,"基于 Tung Huynh 的 Screenbox。\r\nGNU GPL 第 3 版或更新版本。\r\n\r\n"+ReadResource("SetupSource.txt"),"开源信息");};
        Controls.Add(license);
        copy=new LinkLabel {Text="复制错误信息",AutoSize=true,Location=new Point(125,288),Visible=false};
        copy.LinkClicked+=delegate {try {Clipboard.SetText(log.ToString()); status.Text="错误信息已复制，可以发来检查。";} catch {}};
        Controls.Add(copy);
        install=new Button {Text="安装",Location=new Point(322,279),Size=new Size(92,34),FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(73,90,211),ForeColor=Color.White};
        install.FlatAppearance.BorderSize=0; install.Click+=delegate {if(complete) OpenPlayer(); else StartInstall();}; Controls.Add(install);
        close=new Button {Text="取消",Location=new Point(426,279),Size=new Size(92,34),FlatStyle=FlatStyle.Flat,BackColor=Color.White};
        close.Click+=delegate {Close();}; Controls.Add(close);
        FormClosing+=delegate(object sender,FormClosingEventArgs e) {if(busy) e.Cancel=true;};
        Shown+=delegate {if(start) BeginInvoke((Action)StartInstall);};
    }
    private void StartInstall() {
        if(busy) return;
        bool admin;
        using(var i=WindowsIdentity.GetCurrent()) admin=new WindowsPrincipal(i).IsInRole(WindowsBuiltInRole.Administrator);
        if(!admin) {
            try {Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--install") {UseShellExecute=true,Verb="runas"}); Close();}
            catch(Win32Exception) {status.Text="已取消安装，可以随时重试。";}
            return;
        }
        busy=true; error=""; appId=""; log.Clear(); copy.Visible=false; install.Enabled=false; close.Enabled=false;
        status.Text="正在准备安装文件…"; progress.Value=10;
        new Thread(InstallWorker) {IsBackground=true,Name="Screenbox installation"}.Start();
    }
    private void InstallWorker() {
        string root=NewStage(); bool success=false;
        try {
            Extract(root); UpdateStatus("正在检查已有运行组件…",30);
            string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
            var info=new ProcessStartInfo(shell,"-NoLogo -NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(ReadResource("InstallEngine.ps1")))) {
                UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            info.EnvironmentVariables["SCREENBOX_KIT_DIR"]=root;
            using(var p=new Process {StartInfo=info}) {
                p.OutputDataReceived+=delegate(object sender,DataReceivedEventArgs e) {if(e.Data!=null) Output(e.Data);};
                p.ErrorDataReceived+=delegate(object sender,DataReceivedEventArgs e) {if(e.Data!=null) Append(e.Data);};
                p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); p.WaitForExit();
                success=p.ExitCode==0 && appId.Length>0;
            }
        } catch(Exception e) {error=e.Message; Append(e.ToString());}
        finally {
            Cleanup(root);
            try {string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenboxHdrSetup"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder,"install.log"),log.ToString(),Encoding.UTF8);} catch {}
            bool result=success;
            if(!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate {Finish(result);});
        }
    }
    private void Append(string s) {lock(log) log.AppendLine(s);}
    private void Output(string s) {
        Append(s);
        if(s.StartsWith("[AUMID]",StringComparison.Ordinal)) {appId=s.Substring(7).Trim(); return;}
        if(s.StartsWith("[ERROR]",StringComparison.Ordinal)) {error=s.Substring(7); return;}
        if(s.StartsWith("[PROGRESS]",StringComparison.Ordinal)) {int n; if(int.TryParse(s.Substring(10),out n)) UpdateStatus(n>=60?"正在安装播放器，请稍候…":"正在检查已有运行组件…",n);}
    }
    private void UpdateStatus(string text,int n) {
        if(!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate {status.Text=text; progress.Value=Math.Max(0,Math.Min(100,n));});
    }
    private void Finish(bool ok) {
        busy=false; close.Enabled=true; close.Text="关闭"; install.Enabled=true;
        if(ok) {complete=true; progress.Value=100; status.Text="安装完成"; description.Text="Screenbox 已安装完成。\r\n点击“打开播放器”开始使用。"; install.Text="打开播放器"; install.Location=new Point(290,279); install.Width=124;}
        else {progress.Value=0; install.Text="重试"; copy.Visible=true; description.Text="这次安装未能完成，已保留错误信息。\r\n点击“复制错误信息”可交给我继续处理。"; status.Text=error.IndexOf("0x80073D02",StringComparison.OrdinalIgnoreCase)>=0?"系统提示共用组件仍被占用。":"安装未完成。";}
    }
    private void OpenPlayer() {
        try {Process.Start(new ProcessStartInfo("explorer.exe",@"shell:AppsFolder\"+appId) {UseShellExecute=true}); Close();}
        catch {status.Text="请从开始菜单打开 Screenbox HDR。";}
    }
    internal static string NewStage() {return Path.Combine(Path.GetTempPath(),"ScreenboxGuiSetup-"+Guid.NewGuid().ToString("N"));}
    internal static void Extract(string stage) {
        Directory.CreateDirectory(stage);
        string root=Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenboxPayload.zip")) {
            if(s==null) throw new InvalidDataException("Embedded application package is missing.");
            using(var z=new ZipArchive(s,ZipArchiveMode.Read)) foreach(var e in z.Entries) {
                if(e.FullName.EndsWith("/",StringComparison.Ordinal)||e.FullName.EndsWith("Install.cmd",StringComparison.OrdinalIgnoreCase)) continue;
                string target=Path.GetFullPath(Path.Combine(root,e.FullName.Replace('/',Path.DirectorySeparatorChar)));
                if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected package path.");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using(var input=e.Open()) using(var output=File.Create(target)) input.CopyTo(output);
            }
        }
        if(!File.Exists(Path.Combine(stage,"ScreenboxHDR.msix"))) throw new InvalidDataException("Player package is missing.");
    }
    internal static void Cleanup(string stage) {
        string full=Path.GetFullPath(stage), temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("ScreenboxGuiSetup-",StringComparison.Ordinal)) return;
        try {if(Directory.Exists(full)) Directory.Delete(full,true);} catch {}
    }
    private static string ReadResource(string name) {
        using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) {
            if(s==null) throw new InvalidDataException("Installer resource is missing.");
            using(var r=new StreamReader(s,Encoding.UTF8,true)) return r.ReadToEnd();
        }
    }
}
