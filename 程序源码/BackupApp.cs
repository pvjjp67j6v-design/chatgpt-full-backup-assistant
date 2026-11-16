using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program {
    [STAThread] static void Main(string[] args) {
        if(args.Contains("--self-test")) { SelfTest.Run(); return; }
        bool first;
        using(var mutex=new Mutex(true,"Local\\ChatGPTExporterBackup18746",out first)) {
            if(!first) {MessageBox.Show("备份助手已经打开，请查看现有窗口。");return;}
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
static class Data {
    public static JavaScriptSerializer Serializer() {return new JavaScriptSerializer {MaxJsonLength=int.MaxValue,RecursionLimit=512};}
    public static string Json(object x) {return Serializer().Serialize(x);}
    public static Dictionary<string,object> Parse(string x) {return Serializer().Deserialize<Dictionary<string,object>>(x);}
    public static string Str(Dictionary<string,object> d,string key,string def="") {return d.ContainsKey(key)&&d[key]!=null?Convert.ToString(d[key]):def;}
    public static object[] Array(object value) {return ((IEnumerable)value).Cast<object>().ToArray();}
    public static DateTime Now {get {return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time"));}}
    public static void Atomic(string path,string text) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp=path+".tmp"; File.WriteAllText(tmp,text,new UTF8Encoding(false));
        if(File.Exists(path)) File.Replace(tmp,path,path+".bak"); else File.Move(tmp,path);
    }
}
class BackupRun {
    public string Token=Guid.NewGuid().ToString("N"), Attempt=Guid.NewGuid().ToString("N"), DayDir, CacheDir, RunId;
    public object[] Manifest=new object[0];
    public Dictionary<string,int> Done=new Dictionary<string,int>();
    public Dictionary<string,string> Names=new Dictionary<string,string>();
    public bool Active=true, Claimed=false;
    public DateTime LastSeen=DateTime.UtcNow;
    public int Retry=0;
    public const int Batch=25;
    public static readonly Dictionary<string,string> Folders=new Dictionary<string,string> {
        {"json","chatgpt-export-json"},{"files","chatgpt-export-markdown图片单独打包"},{"embed","chatgpt-export-markdown图片嵌套进MD"}
    };
    public BackupRun(string root) {
        RunId=Data.Now.ToString("HHmmss")+"-"+Token.Substring(0,6);
        DayDir=Path.Combine(Path.GetFullPath(root),Data.Now.ToString("yyyy-MM-dd"));
        CacheDir=Path.Combine(DayDir,".任务缓存",RunId);
        foreach(var folder in Folders.Values) Directory.CreateDirectory(Path.Combine(DayDir,folder));
        Directory.CreateDirectory(CacheDir);
        Log("开始备份全部对话；任务 "+RunId);
        SaveState();
    }
    public int Count(string type) {return Done.Where(x=>x.Key.StartsWith(type+":")).Sum(x=>x.Value);}
    public void Log(string message) {
        File.AppendAllText(Path.Combine(DayDir,"backup.log"),"["+Data.Now.ToString("yyyy-MM-dd HH:mm:ss")+"] ["+RunId+"] "+message+Environment.NewLine,new UTF8Encoding(false));
    }
    public string Summary() {return String.Join("；",Folders.Select(x=>x.Value+"："+Count(x.Key)+"/"+(Manifest.Length==0?"未知":Manifest.Length.ToString())+" 条"));}
    public void SaveState() {Data.Atomic(Path.Combine(CacheDir,"任务状态.json"),Data.Json(new {run=RunId,manifest=Manifest,done=Done,files=Names,active=Active}));}
    public string Key(Dictionary<string,object> body) {
        string type=Data.Str(body,"type"); int part=Convert.ToInt32(body["part"]);
        if(!Folders.ContainsKey(type) || part<1 || part>(Manifest.Length+Batch-1)/Batch) throw new Exception("无效的文件类别或批次");
        return type+":"+part;
    }
    public string TempPath(string key) {return Path.Combine(CacheDir,key.Replace(':','-')+".part");}
    public string[] ExpectedIds(int part) {
        return Manifest.Skip((part-1)*Batch).Take(Batch).Select(x=>Data.Str((Dictionary<string,object>)x,"id")).ToArray();
    }
    public void CheckIds(string[] actual,int part) {
        if(!actual.OrderBy(x=>x).SequenceEqual(ExpectedIds(part).OrderBy(x=>x))) throw new Exception("文件中的会话 ID 与本批清单不一致");
    }
    public int Validate(string path,string type,int part) {
        if(type=="json") {
            var items=Data.Array(Data.Serializer().DeserializeObject(File.ReadAllText(path,Encoding.UTF8)));
            var ids=items.Select(x=>Data.Str((Dictionary<string,object>)x,"id")).ToArray(); CheckIds(ids,part);return ids.Length;
        }
        using(var zip=ZipFile.OpenRead(path)) {
            var manifest=zip.GetEntry("_backup-manifest.json"); if(manifest==null) throw new Exception("压缩包缺少会话核对清单");
            Dictionary<string,object> info;
            using(var reader=new StreamReader(manifest.Open(),Encoding.UTF8)) info=Data.Parse(reader.ReadToEnd());
            if(Data.Str(info,"type")!=type) throw new Exception("Markdown 图片模式不匹配");
            var ids=Data.Array(info["ids"]).Select(Convert.ToString).ToArray(); CheckIds(ids,part);
            if(zip.Entries.Count(e=>e.FullName.EndsWith(".md",StringComparison.OrdinalIgnoreCase))!=ids.Length) throw new Exception("Markdown 实际文件数与会话数不一致");
            foreach(var entry in zip.Entries) {
                // Reading all entries validates decompression and detects truncated uploads.
                using(var stream=entry.Open()) {
                    if(entry.FullName.EndsWith(".md",StringComparison.OrdinalIgnoreCase)) {
                        using(var reader=new StreamReader(stream,Encoding.UTF8)) {
                            string md=reader.ReadToEnd();
                            if(md.Contains("sediment://")||md.Contains("file-service://")) throw new Exception("Markdown 仍有未下载的图片");
                            foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(md,@"!\[image\]\((assets/[^)]+)\)")) {
                                if(type=="embed" || zip.GetEntry(m.Groups[1].Value)==null) throw new Exception("图片引用缺失或模式错误");
                            }
                        }
                    } else stream.CopyTo(Stream.Null);
                }
            }
            return ids.Length;
        }
    }
    public void Commit(Dictionary<string,object> body) {
        string key=Key(body),type=Data.Str(body,"type"); int part=Convert.ToInt32(body["part"]);
        string temp=TempPath(key); int count=Validate(temp,type,part);
        string file="chatgpt-export-"+(type=="json"?"json":"markdown")+"-"+RunId+"-part-"+part.ToString("D3")+(type=="json"?".json":".zip");
        string dest=Path.Combine(DayDir,Folders[type],file), copying=dest+".copying";
        File.Copy(temp,copying,true);
        if(Validate(copying,type,part)!=count) throw new Exception("复制后的文件校验失败");
        if(File.Exists(dest)) throw new Exception("目标文件已存在，拒绝覆盖");
        File.Move(copying,dest); Done[key]=count;Names[key]=file;SaveState();
        Log("保存成功："+Folders[type]+"，第 "+part+" 批，实际 "+count+" 条会话；"+file);
        File.Delete(temp);
    }
}

class MainForm:Form {
    TextBox root=new TextBox(), details=new TextBox();
    NumericUpDown interval=new NumericUpDown(), retries=new NumericUpDown();
    Label last=new Label(), status=new Label(), preview=new Label();
    Button start=new Button(),stop=new Button(),browse=new Button(),install=new Button();
    string home=AppDomain.CurrentDomain.BaseDirectory, configFile;
    HttpListener listener; BackupRun run; readonly object gate=new object();
    System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    DateTime? retryAt; bool closing=false; string lastInfo="尚无备份记录";
    public MainForm() {
        Text="ChatGPT 全量备份助手"; Font=new Font("Microsoft YaHei UI",10F); ClientSize=new Size(940,735);
        MinimumSize=new Size(820,720); StartPosition=FormStartPosition.CenterScreen; BackColor=Color.FromArgb(246,248,250);
        var heading=new Label {Text="ChatGPT 全量备份助手",Font=new Font(Font.FontFamily,21,FontStyle.Bold),AutoSize=true,Location=new Point(26,20)};Controls.Add(heading);
        AddLabel("一次备份全部对话 · JSON / Markdown 独立图片 / Markdown 嵌入图片",26,65,880,25);
        AddLabel("备份根目录",26,109,140,24);
        root.SetBounds(26,139,760,30);root.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(root);
        browse.Text="选择目录";browse.SetBounds(800,136,112,35);browse.Anchor=AnchorStyles.Top|AnchorStyles.Right;Controls.Add(browse);
        browse.Click+=(s,e)=>{using(var d=new FolderBrowserDialog()){d.SelectedPath=root.Text;if(d.ShowDialog()==DialogResult.OK)root.Text=d.SelectedPath;}};
        preview.SetBounds(26,179,875,78);preview.ForeColor=Color.FromArgb(70,90,100);preview.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(preview);
        root.TextChanged+=(s,e)=>UpdatePreview();
        AddLabel("重试间隔（秒）",26,272,155,25);interval.SetBounds(180,268,105,32);interval.Maximum=86400;interval.Value=60;Controls.Add(interval);
        AddLabel("重试次数",330,272,100,25);retries.SetBounds(430,268,90,32);retries.Maximum=100;retries.Value=3;Controls.Add(retries);
        AddLabel("任一项设为 0，即关闭自动重试。倒计时与失败按相同规则处理。",26,311,865,26);
        last.SetBounds(26,353,878,55);last.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;Controls.Add(last);
        start.Text="一键导出并备份";start.SetBounds(26,419,195,44);start.BackColor=Color.FromArgb(29,112,94);start.ForeColor=Color.White;start.FlatStyle=FlatStyle.Flat;Controls.Add(start);start.Click+=(s,e)=>StartRun();
        stop.Text="停止";stop.SetBounds(235,419,90,44);stop.Enabled=false;Controls.Add(stop);stop.Click+=(s,e)=>StopRun("用户停止");
        install.Text="安装 / 更新浏览器连接组件";install.SetBounds(345,419,275,44);Controls.Add(install);install.Click+=(s,e)=>OpenEdge("http://127.0.0.1:18746/install.user.js");
        var open=new Button {Text="打开备份目录",Location=new Point(640,419),Size=new Size(160,44)};Controls.Add(open);open.Click+=(s,e)=>{try{string p=run==null?root.Text:run.DayDir;Directory.CreateDirectory(p);Process.Start("explorer.exe","\""+p+"\"");}catch(Exception ex){MessageBox.Show(ex.Message);}};
        status.SetBounds(26,480,878,30);status.Text="就绪。首次使用请安装浏览器连接组件（仅需一次）。";Controls.Add(status);
        details.Multiline=true;details.ReadOnly=true;details.ScrollBars=ScrollBars.Vertical;details.SetBounds(26,520,886,185);details.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;details.BackColor=Color.White;Controls.Add(details);
        configFile=Path.Combine(home,"备份设置.json");root.Text=home.TrimEnd('\\');
        try{if(File.Exists(configFile)){var c=Data.Parse(File.ReadAllText(configFile));root.Text=Data.Str(c,"root",root.Text);interval.Value=Math.Min(interval.Maximum,Math.Max(0,Convert.ToDecimal(c["interval"])));retries.Value=Math.Min(retries.Maximum,Math.Max(0,Convert.ToDecimal(c["retries"])));lastInfo=Data.Str(c,"last",lastInfo);}}catch(Exception ex){Append("设置读取失败，使用默认值："+ex.Message);}
        last.Text="上次导入 / 备份日期及结果："+lastInfo;UpdatePreview();
        try {listener=new HttpListener();listener.Prefixes.Add("http://127.0.0.1:18746/");listener.Start();Task.Run((Action)Serve);}catch(Exception ex){start.Enabled=false;install.Enabled=false;status.Text="本地连接启动失败："+ex.Message;}
        timer.Interval=1000;timer.Tick+=(s,e)=>Tick();timer.Start();
        FormClosing+=(s,e)=>{closing=true;StopRun("程序关闭，任务停止");SaveConfig();timer.Stop();if(listener!=null)listener.Close();};
    }
    void AddLabel(string text,int x,int y,int w,int h){Controls.Add(new Label{Text=text,Location=new Point(x,y),Size=new Size(w,h)});}
    void UpdatePreview(){preview.Text="保存结构："+Data.Now.ToString("yyyy-MM-dd")+"\\chatgpt-export-json\\\r\n                  "+Data.Now.ToString("yyyy-MM-dd")+"\\chatgpt-export-markdown图片单独打包\\\r\n                  "+Data.Now.ToString("yyyy-MM-dd")+"\\chatgpt-export-markdown图片嵌套进MD\\";}
    void Append(string s){details.AppendText(Data.Now.ToString("HH:mm:ss")+"  "+s+Environment.NewLine);}
    void UI(Action a){if(!closing&&!IsDisposed)try{BeginInvoke(a);}catch(InvalidOperationException){}}
    void SaveConfig(){try{Data.Atomic(configFile,Data.Json(new{root=root.Text,interval=(int)interval.Value,retries=(int)retries.Value,last=lastInfo}));}catch(Exception ex){if(!closing)Append("设置保存失败："+ex.Message);}}
    void ControlsBusy(bool busy){start.Enabled=!busy;stop.Enabled=busy;root.Enabled=!busy;browse.Enabled=!busy;interval.Enabled=!busy;retries.Enabled=!busy;install.Enabled=!busy;}
    void StartRun(){
        lock(gate) {
            try {
                if(String.IsNullOrWhiteSpace(root.Text)||!Path.IsPathRooted(root.Text))throw new Exception("请选择完整的备份目录");
                run=new BackupRun(root.Text);retryAt=null;ControlsBusy(true);
                lastInfo=Data.Now.ToString("yyyy-MM-dd HH:mm:ss")+" · 进行中（若程序意外退出，表示未完成）";last.Text="上次导入 / 备份日期及结果："+lastInfo;SaveConfig();
                Append("已建立当天目录。开始备份全部对话。");LaunchAttempt();
            }catch(Exception ex){if(run!=null)run.Active=false;ControlsBusy(false);status.Text="启动失败";Append(ex.Message);}
        }
    }
    void LaunchAttempt(){
        run.Attempt=Guid.NewGuid().ToString("N");run.Claimed=false;run.LastSeen=DateTime.UtcNow;
        status.Text="正在 Edge 新标签页加载 ChatGPT，等待连接…";Append(status.Text);
        try{OpenEdge("https://chatgpt.com/#backupToken="+run.Token+"&attempt="+run.Attempt);}
        catch(Exception ex){HandleFailure(ex.Message);}
    }
    void OpenEdge(string url){
        string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft\\Edge\\Application\\msedge.exe");
        if(!File.Exists(path))path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Microsoft\\Edge\\Application\\msedge.exe");
        if(!File.Exists(path))throw new Exception("未找到 Microsoft Edge");
        Process.Start(new ProcessStartInfo(path,"--new-tab \""+url+"\""){UseShellExecute=true});
    }
    void Tick(){lock(gate){
        if(run==null||!run.Active)return;
        if(retryAt.HasValue){int seconds=(int)Math.Ceiling((retryAt.Value-DateTime.UtcNow).TotalSeconds);
            if(seconds<=0){retryAt=null;run.Retry++;Append("开始第 "+run.Retry+" 次重试");LaunchAttempt();}
            else status.Text="等待重试："+seconds+" 秒；即将进行第 "+(run.Retry+1)+" / "+(int)retries.Value+" 次重试";
        }else if((DateTime.UtcNow-run.LastSeen).TotalSeconds>(run.Claimed?180:100))HandleFailure(run.Claimed?"浏览器连接中断或页面长时间无响应":"浏览器连接组件尚未连接。请安装组件，并确认 Edge 已登录 ChatGPT，且允许用户脚本运行。");
    }}
    void HandleFailure(string message){lock(gate){
        if(run==null||!run.Active||retryAt.HasValue)return;
        run.Attempt="invalid-"+Guid.NewGuid().ToString("N");
        run.Log("本次尝试失败："+message+"；所在步骤："+status.Text+"；"+run.Summary());Append("失败："+message);
        if(interval.Value>0&&retries.Value>0&&run.Retry<(int)retries.Value){
            retryAt=DateTime.UtcNow.AddSeconds((double)interval.Value);run.Log("按设置等待 "+interval.Value+" 秒后重试；不采用页面倒计时");
            status.Text="等待重试…";
        }else Finish("失败",message);
    }}
    void Finish(string result,string reason){
        run.Active=false;retryAt=null;run.SaveState();string summary=run.Summary();run.Log("任务"+result+"："+reason+"；"+summary);
        foreach(var type in BackupRun.Folders.Keys)run.Log(BackupRun.Folders[type]+"："+(run.Manifest.Length>0&&run.Count(type)==run.Manifest.Length?"成功":"未完成 / 失败")+"；实际已保存会话数 "+run.Count(type)+"；应保存 "+(run.Manifest.Length==0?"未知":run.Manifest.Length.ToString()));
        lastInfo=Data.Now.ToString("yyyy-MM-dd HH:mm:ss")+" · "+result+" · JSON "+run.Count("json")+" / 独立图片 MD "+run.Count("files")+" / 嵌入图片 MD "+run.Count("embed")+" 条";
        if(result!="成功")lastInfo+=" · "+reason;
        last.Text="上次导入 / 备份日期及结果："+lastInfo;status.Text="备份"+result+"："+reason;Append(summary);ControlsBusy(false);SaveConfig();
    }
    void StopRun(string reason){lock(gate){if(run!=null&&run.Active){run.Attempt=Guid.NewGuid().ToString("N");Finish("未完成",reason);}}}
    void Serve(){while(listener.IsListening){try{var c=listener.GetContext();Task.Run(()=>Request(c));}catch{break;}}}
    void Reply(HttpListenerContext c,int code,object body){byte[] bytes=Encoding.UTF8.GetBytes(Data.Json(body));c.Response.StatusCode=code;c.Response.ContentType="application/json; charset=utf-8";c.Response.ContentLength64=bytes.Length;c.Response.OutputStream.Write(bytes,0,bytes.Length);c.Response.Close();}
    void Request(HttpListenerContext c){try{
        if(!IPAddress.IsLoopback(c.Request.RemoteEndPoint.Address))throw new Exception("仅接受本机连接");
        string path=c.Request.Url.AbsolutePath;
        if(path=="/install.user.js"){
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("bridge.user.js")){
                c.Response.ContentType="application/javascript; charset=utf-8";stream.CopyTo(c.Response.OutputStream);c.Response.Close();return;
            }
        }
        lock(gate){
            if(run==null||!run.Active||retryAt.HasValue||c.Request.Headers["X-Backup-Token"]!=run.Token||c.Request.Headers["X-Backup-Attempt"]!=run.Attempt){Reply(c,403,new{error="任务已停止或当前标签页已失效"});return;}
            run.LastSeen=DateTime.UtcNow;
            Dictionary<string,object> body=new Dictionary<string,object>();
            if(c.Request.HttpMethod=="POST")using(var r=new StreamReader(c.Request.InputStream,Encoding.UTF8))body=Data.Parse(r.ReadToEnd());
            switch(path){
                case "/claim":if(run.Claimed)throw new Exception("本次任务已由另一个标签页接管");run.Claimed=true;UI(()=>{status.Text="浏览器已连接，正在读取全部对话";Append(status.Text);});break;
                case "/heartbeat":break;
                case "/progress":string text=Data.Str(body,"text");UI(()=>{status.Text=text;Append(text);});break;
                case "/state":Reply(c,200,new{manifest=run.Manifest,done=run.Done.Keys.ToArray()});return;
                case "/manifest":
                    if(run.Manifest.Length!=0)throw new Exception("本次清单已固定");
                    var items=Data.Array(body["items"]);var ids=items.Select(x=>Data.Str((Dictionary<string,object>)x,"id")).ToArray();
                    if(ids.Length==0||ids.Length>20000||ids.Distinct().Count()!=ids.Length||ids.Any(x=>!ValidGuid(x)))throw new Exception("会话清单无效");
                    run.Manifest=items;run.SaveState();run.Log("已固定全部对话清单："+items.Length+" 条");break;
                case "/cache":
                    string id=c.Request.HttpMethod=="GET"?c.Request.QueryString["id"]:Data.Str(body,"id");Guid idValue;
                    if(!Guid.TryParse(id,out idValue))throw new Exception("无效会话 ID");string file=Path.Combine(run.CacheDir,idValue.ToString()+".json");
                    if(c.Request.HttpMethod=="GET"){Reply(c,200,new{raw=File.Exists(file)?Data.Serializer().DeserializeObject(File.ReadAllText(file)):null});return;}
                    var raw=(Dictionary<string,object>)body["raw"];if(Data.Str(raw,"id")!=id)throw new Exception("缓存 ID 不一致");Data.Atomic(file,Data.Json(raw));break;
                case "/begin":string key=run.Key(body);if(run.Done.ContainsKey(key))throw new Exception("此批已保存成功");File.WriteAllBytes(run.TempPath(key),new byte[0]);break;
                case "/chunk":
                    string ck=run.Key(body);long offset=Convert.ToInt64(body["offset"]);byte[] bytes=Convert.FromBase64String(Data.Str(body,"data"));
                    using(var fs=new FileStream(run.TempPath(ck),FileMode.Open,FileAccess.Write)){if(fs.Length!=offset)throw new Exception("上传片段顺序错误");fs.Position=offset;fs.Write(bytes,0,bytes.Length);fs.Flush(true);}break;
                case "/commit":run.Commit(body);break;
                case "/failed":string msg=Data.Str(body,"message","未知错误");UI(()=>HandleFailure(msg));break;
                case "/complete":
                    if(run.Manifest.Length==0||BackupRun.Folders.Keys.Any(t=>run.Count(t)!=run.Manifest.Length))throw new Exception("三类文件的实际会话数未全部匹配，不能标记成功");
                    UI(()=>{lock(gate){if(run.Active)Finish("成功","三类文件均已保存并核对会话数量");}});break;
                default:throw new Exception("未知请求");
            }
            Reply(c,200,new{ok=true});
        }
    }catch(Exception ex){try{Reply(c,400,new{error=ex.Message});}catch{}}}
    static bool ValidGuid(string value){Guid parsed;return Guid.TryParse(value,out parsed);}
}

static class SelfTest {
    public static void Run(){
        var results=new List<string>();string temp=Path.Combine(Path.GetTempPath(),"ChatGPTBackupTests-"+Guid.NewGuid().ToString("N"));
        try {
            var r=new BackupRun(temp);var id=Guid.NewGuid().ToString();r.Manifest=new object[]{new Dictionary<string,object>{{"id",id},{"title","测试"}}};
            var json=new Dictionary<string,object>{{"type","json"},{"part",1}};
            File.WriteAllText(r.TempPath("json:1"),Data.Json(new[]{new{id=id,mapping=new{}}}));r.Commit(json);
            if(r.Count("json")!=1)throw new Exception("JSON count");results.Add("JSON upload, copy, ID and count verification: PASS");
            foreach(string type in new[]{"files","embed"}) {
                string file=r.TempPath(type+":1");using(var z=ZipFile.Open(file,ZipArchiveMode.Create)){
                    using(var w=new StreamWriter(z.CreateEntry("ChatGPT-test.md").Open()))w.Write("---\ntitle: test\n---\n# Test\n");
                    using(var w=new StreamWriter(z.CreateEntry("_backup-manifest.json").Open()))w.Write(Data.Json(new{ids=new[]{id},type=type}));
                }r.Commit(new Dictionary<string,object>{{"type",type},{"part",1}});
            }
            if(r.Count("embed")!=1||r.Count("files")!=1)throw new Exception("MD counts");results.Add("Both Markdown ZIP types and actual counts: PASS");
            File.WriteAllText(r.TempPath("json:1"),"[]");bool rejected=false;try{r.Validate(r.TempPath("json:1"),"json",1);}catch{rejected=true;}if(!rejected)throw new Exception("Missing conversation accepted");results.Add("Incomplete export rejected: PASS");
            var r2=new BackupRun(temp);if(r2.RunId==r.RunId)throw new Exception("Collision");results.Add("Same-day run isolation and existing-file preservation: PASS");
            r.Active=false;r.SaveState();results.Add("Persistent logs, date layout and state: PASS");
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"自检结果.txt"),results.Concat(new[]{"Fixtures: "+temp,"Time: "+Data.Now.ToString("s")}),Encoding.UTF8);
        }catch(Exception ex){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"自检结果.txt"),String.Join(Environment.NewLine,results)+Environment.NewLine+"FAILED: "+ex,Encoding.UTF8);Environment.ExitCode=1;}
    }
}
