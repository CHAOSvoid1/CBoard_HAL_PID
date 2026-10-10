using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CBoardMotorWeb
{
    internal static class Program
    {
        private static Mutex singleton;

        [STAThread]
        private static void Main()
        {
            try
            {
                bool created;
                singleton = new Mutex(true, @"Local\CBoardMotorTestBench", out created);
                if (!created)
                {
                    string marker = Path.Combine(Path.GetTempPath(), "CBoardMotorTestBench.port");
                    int existingPort;
                    if (File.Exists(marker) && Int32.TryParse(File.ReadAllText(marker).Trim(), out existingPort))
                        Process.Start("http://127.0.0.1:" + existingPort + "/");
                    return;
                }
                Application.EnableVisualStyles();
                Application.Run(new ServerContext());
                singleton.ReleaseMutex();
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web_startup_error.log"), ex.GetType().FullName + "\r\n" + ex.Message + "\r\n" + ex.StackTrace); } catch { }
                MessageBox.Show("Web Dashboard 启动失败：" + ex.Message, "CBoard Lab");
            }
        }
    }

    internal sealed class Sample
    {
        public long Id;
        public double Time;
        public double[] V;
    }

    internal sealed class LogEntry
    {
        public long Id;
        public double Time;
        public string Text;
    }

    internal sealed class LocalRequest
    {
        public Uri Url;
        public string HttpMethod;
        public string Body;
    }

    internal sealed class LocalContext
    {
        public LocalRequest Request;
        public NetworkStream Stream;
        public int StatusCode = 200;
    }

    internal sealed class ServerContext : ApplicationContext
    {
        private readonly string root;
        private readonly string configPath;
        private readonly string htmlPath;
        private readonly string keilProject;
        private readonly string portFile;
        private TcpListener listener;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly object sync = new object();
        private readonly List<Sample> samples = new List<Sample>();
        private readonly List<LogEntry> logs = new List<LogEntry>();
        private readonly List<byte> binary = new List<byte>();
        private readonly StringBuilder text = new StringBuilder();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private SerialPort serial;
        private bool justFloat;
        private long nextSample;
        private long nextLog;
        private long rxBytes;
        private double lastRxSeconds = -1.0;
        private int port;

        public ServerContext()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            root = Directory.Exists(Path.Combine(baseDir, "User_Files")) ? baseDir : Directory.GetParent(baseDir).FullName;
            configPath = Path.Combine(root, "User_Files", "app_config.h");
            htmlPath = Path.Combine(root, "Tools", "dashboard.html");
            if (!File.Exists(htmlPath)) htmlPath = Path.Combine(baseDir, "dashboard.html");
            keilProject = Path.Combine(root, "MDK-ARM", "CBoard.uvprojx");
            portFile = Path.Combine(Path.GetTempPath(), "CBoardMotorTestBench.port");
            port = FreePort();
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            File.WriteAllText(portFile, port.ToString(CultureInfo.InvariantCulture));
            listener.BeginAcceptTcpClient(Handle, null);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开控制台", null, delegate { OpenBrowser(); });
            menu.Items.Add("退出", null, delegate { Shutdown(); });
            tray.Icon = System.Drawing.SystemIcons.Application;
            tray.Text = "C板单电机测试台";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenBrowser(); };
            if (Environment.GetEnvironmentVariable("CBOARD_NO_BROWSER") != "1") OpenBrowser();
        }

        private static int FreePort()
        {
            TcpListener tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start(); int p = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop(); return p;
        }

        private void OpenBrowser()
        {
            Process.Start("http://127.0.0.1:" + port + "/");
        }

        private void Handle(IAsyncResult result)
        {
            TcpClient client;
            try { client = listener.EndAcceptTcpClient(result); }
            catch { return; }
            try { listener.BeginAcceptTcpClient(Handle, null); } catch { }
            ThreadPool.QueueUserWorkItem(delegate { ReadRequest(client); });
        }

        private void ReadRequest(TcpClient client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                string first = reader.ReadLine();
                if (String.IsNullOrEmpty(first)) { client.Close(); return; }
                string[] parts = first.Split(' ');
                if (parts.Length < 2) { client.Close(); return; }
                int contentLength = 0;
                string line;
                while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        Int32.TryParse(line.Substring(colon + 1).Trim(), out contentLength);
                }
                char[] bodyChars = new char[contentLength];
                int read = 0;
                while (read < contentLength) { int n = reader.Read(bodyChars, read, contentLength - read); if (n <= 0) break; read += n; }
                LocalContext context = new LocalContext {
                    Stream = stream,
                    Request = new LocalRequest { HttpMethod = parts[0], Url = new Uri("http://127.0.0.1:" + port + parts[1]), Body = new string(bodyChars, 0, read) }
                };
                Route(context);
            }
            catch { try { client.Close(); } catch { } }
        }

        private void Route(LocalContext c)
        {
            try
            {
                string path = c.Request.Url.AbsolutePath.ToLowerInvariant();
                if (path == "/") ServeFile(c, htmlPath, "text/html; charset=utf-8");
                else if (path == "/api/config" && c.Request.HttpMethod == "GET") Json(c, ReadConfig());
                else if (path == "/api/config") { SaveConfig(Form(c)); Json(c, "{\"ok\":true}"); }
                else if (path == "/api/ports") Json(c, PortsJson());
                else if (path == "/api/status") Json(c, StatusJson());
                else if (path == "/api/connect") { Connect(Form(c)); Json(c, StatusJson()); }
                else if (path == "/api/command") { Send(Get(Form(c), "cmd")); Json(c, "{\"ok\":true}"); }
                else if (path == "/api/format") { SetFormat(Get(Form(c), "format")); Json(c, StatusJson()); }
                else if (path == "/api/samples") Json(c, SamplesJson(ParseLong(ParseQuery(c.Request.Url.Query, "after"))));
                else if (path == "/api/logs") Json(c, LogsJson(ParseLong(ParseQuery(c.Request.Url.Query, "after"))));
                else if (path == "/api/keil") { Keil(Get(Form(c), "mode")); Json(c, "{\"ok\":true}"); }
                else if (path == "/api/exit") { Json(c, "{\"ok\":true}"); BeginInvokeShutdown(); }
                else { c.StatusCode = 404; Text(c, "Not found", "text/plain"); }
            }
            catch (Exception ex) { c.StatusCode = 500; Json(c, "{\"ok\":false,\"error\":" + Q(ex.Message) + "}"); }
        }

        private static string ParseQuery(string query, string key)
        {
            Dictionary<string, string> values = ParseForm(query.TrimStart('?'));
            return Get(values, key);
        }

        private static Dictionary<string, string> Form(LocalContext c) { return ParseForm(c.Request.Body); }

        private static Dictionary<string, string> ParseForm(string body)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            foreach (string pair in (body ?? "").Split('&'))
            {
                if (pair.Length == 0) continue;
                string[] parts = pair.Split(new[] { '=' }, 2);
                result[WebUtility.UrlDecode(parts[0])] = parts.Length > 1 ? WebUtility.UrlDecode(parts[1].Replace("+", " ")) : "";
            }
            return result;
        }

        private static string Get(Dictionary<string, string> form, string key)
        {
            string value; return form.TryGetValue(key, out value) ? value : "";
        }

        private static string PortKind(string description)
        {
            string s = (description ?? "").ToLowerInvariant();
            if (s.Contains("ch340") || s.Contains("ch341")) return "CH340 USB 串口";
            if (s.Contains("cp210")) return "CP210x USB 串口";
            if (s.Contains("ftdi") || s.Contains("ft232")) return "FTDI USB 串口";
            if (s.Contains("bluetooth") || s.Contains("蓝牙")) return "蓝牙串口";
            if (s.Contains("stlink") || s.Contains("st-link")) return "ST-Link 虚拟串口";
            if (s.Contains("usb")) return "USB 串口";
            return "串口设备";
        }

        private static string PortsJson()
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string name = Convert.ToString(device["Name"], CultureInfo.InvariantCulture);
                        Match match = Regex.Match(name ?? "", @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                        if (match.Success) names[match.Groups[1].Value] = name;
                    }
                }
            }
            catch { }

            List<string> items = new List<string>();
            foreach (string portName in SerialPort.GetPortNames().OrderBy(x => ParsePortNumber(x)).ThenBy(x => x))
            {
                string description;
                if (!names.TryGetValue(portName, out description)) description = portName;
                items.Add("{\"port\":" + Q(portName) + ",\"description\":" + Q(description) +
                          ",\"kind\":" + Q(PortKind(description)) + "}");
            }
            return "[" + String.Join(",", items.ToArray()) + "]";
        }

        private static int ParsePortNumber(string value)
        {
            Match match = Regex.Match(value ?? "", @"\d+");
            int number;
            return match.Success && Int32.TryParse(match.Value, out number) ? number : Int32.MaxValue;
        }

        private static string Q(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
        }

        private static long ParseLong(string value)
        {
            long n; return Int64.TryParse(value, out n) ? n : 0;
        }

        private static void ServeFile(LocalContext c, string path, string type)
        {
            if (!File.Exists(path)) { c.StatusCode = 404; Text(c, "dashboard.html missing", "text/plain"); return; }
            WriteResponse(c, File.ReadAllBytes(path), type);
        }

        private static void Json(LocalContext c, string value) { Text(c, value, "application/json; charset=utf-8"); }
        private static void Text(LocalContext c, string value, string type)
        {
            WriteResponse(c, Encoding.UTF8.GetBytes(value), type);
        }

        private static void WriteResponse(LocalContext c, byte[] data, string type)
        {
            string reason = c.StatusCode == 200 ? "OK" : (c.StatusCode == 404 ? "Not Found" : "Internal Server Error");
            string header = "HTTP/1.1 " + c.StatusCode + " " + reason + "\r\nContent-Type: " + type +
                "\r\nContent-Length: " + data.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            c.Stream.Write(head, 0, head.Length); c.Stream.Write(data, 0, data.Length); c.Stream.Flush(); c.Stream.Close();
        }

        private string Define(string all, string name)
        {
            Match m = Regex.Match(all, "(?m)^\\s*#define\\s+" + Regex.Escape(name) + "\\s+([^\\s/]+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        private string ReadConfig()
        {
            string t = File.ReadAllText(configPath, Encoding.UTF8);
            return "{\"motor\":" + Q(Define(t, "APP_MOTOR_TYPE").Contains("2006") ? "2006" : "3508") +
                ",\"id\":" + Regex.Replace(Define(t, "APP_MOTOR_ID"), "[^0-9]", "") +
                ",\"can\":" + Q(Define(t, "APP_USE_CAN1") == "0U" ? "2" : "1") +
                ",\"uart\":" + Q(Define(t, "APP_USE_USART6") == "0U" ? "1" : "6") +
                ",\"format\":" + Q(Define(t, "APP_DEFAULT_VOFA_FORMAT").Contains("JUSTFLOAT") ? "justfloat" : "firewater") +
                ",\"controller\":" + Q(Define(t, "APP_DEFAULT_CONTROLLER").Contains("SMC") ? "smc" : "pid") + "}";
        }

        private static string SetDefine(string text, string name, string value)
        {
            string p = "(?m)^\\s*#define\\s+" + Regex.Escape(name) + "\\s+.*$";
            if (!Regex.IsMatch(text, p)) throw new InvalidOperationException("配置项不存在: " + name);
            return Regex.Replace(text, p, "#define " + name.PadRight(31) + value);
        }

        private void SaveConfig(Dictionary<string, string> f)
        {
            string t = File.ReadAllText(configPath, Encoding.UTF8);
            t = SetDefine(t, "APP_MOTOR_TYPE", Get(f, "motor") == "2006" ? "MOTOR_TYPE_M2006" : "MOTOR_TYPE_M3508");
            t = SetDefine(t, "APP_MOTOR_ID", Get(f, "id") + "U");
            t = SetDefine(t, "APP_USE_CAN1", Get(f, "can") == "2" ? "0U" : "1U");
            t = SetDefine(t, "APP_USE_USART6", Get(f, "uart") == "1" ? "0U" : "1U");
            t = SetDefine(t, "APP_DEFAULT_VOFA_FORMAT", Get(f, "format") == "justfloat" ? "APP_VOFA_FORMAT_JUSTFLOAT" : "APP_VOFA_FORMAT_FIREWATER");
            t = SetDefine(t, "APP_DEFAULT_CONTROLLER", Get(f, "controller") == "smc" ? "APP_CONTROLLER_SMC" : "APP_CONTROLLER_PID");
            File.WriteAllText(configPath, t, new UTF8Encoding(false));
        }

        private string StatusJson()
        {
            lock (sync)
            {
                double age = lastRxSeconds < 0.0 ? -1.0 : clock.Elapsed.TotalSeconds - lastRxSeconds;
                return "{\"connected\":" + ((serial != null && serial.IsOpen) ? "true" : "false") +
                    ",\"port\":" + Q(serial == null ? "" : serial.PortName) +
                    ",\"format\":" + Q(justFloat ? "justfloat" : "firewater") +
                    ",\"rxBytes\":" + rxBytes +
                    ",\"lastRxAge\":" + age.ToString("F2", CultureInfo.InvariantCulture) + "}";
            }
        }

        private void Connect(Dictionary<string, string> f)
        {
            lock (sync)
            {
                if (serial != null) { try { serial.Close(); } catch { } serial.Dispose(); serial = null; }
                if (Get(f, "action") == "disconnect") return;
                serial = new SerialPort(Get(f, "port"), 115200, Parity.None, 8, StopBits.One);
                serial.DataReceived += SerialData; serial.Open();
                rxBytes = 0; lastRxSeconds = -1.0;
                AddLog("SYS> 已打开 " + serial.PortName + "，115200-8-N-1");
            }
        }

        private void Send(string command)
        {
            lock (sync) { if (serial == null || !serial.IsOpen) throw new InvalidOperationException("串口未连接"); serial.Write(command + "\r\n"); AddLog("TX> " + command); }
        }

        private void SetFormat(string format)
        {
            justFloat = format == "justfloat"; binary.Clear(); text.Clear();
            Send(justFloat ? "FORMAT:JUSTFLOAT" : "FORMAT:FIREWATER");
        }

        private void SerialData(object sender, SerialDataReceivedEventArgs e)
        {
            lock (sync)
            {
                if (serial == null || !serial.IsOpen) return;
                byte[] data = new byte[serial.BytesToRead]; serial.Read(data, 0, data.Length);
                rxBytes += data.Length; lastRxSeconds = clock.Elapsed.TotalSeconds;
                if (justFloat) { binary.AddRange(data); DecodeBinary(); }
                else DecodeText(data);
            }
        }

        private void DecodeText(byte[] data)
        {
            text.Append(Encoding.ASCII.GetString(data));
            while (true)
            {
                string all = text.ToString(); int nl = all.IndexOf('\n'); if (nl < 0) return;
                string line = all.Substring(0, nl).Trim(); text.Remove(0, nl + 1);
                if (line.Length > 0) AddLog("RX< " + line);
                int colon = line.IndexOf(':'); string payload = colon >= 0 ? line.Substring(colon + 1) : line;
                string[] fields = payload.Split(','); if (fields.Length < 8) continue;
                double[] values = new double[8]; bool ok = true;
                for (int i = 0; i < 8; i++) ok &= Double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
                if (ok) AddSample(values);
            }
        }

        private void DecodeBinary()
        {
            while (binary.Count >= 36)
            {
                int tail = -1;
                for (int i = 32; i <= binary.Count - 4; i++) if (binary[i] == 0 && binary[i + 1] == 0 && binary[i + 2] == 0x80 && binary[i + 3] == 0x7F) { tail = i; break; }
                if (tail < 32) { if (binary.Count > 512) binary.RemoveRange(0, binary.Count - 64); return; }
                int start = tail - 32; double[] values = new double[8];
                for (int i = 0; i < 8; i++) values[i] = BitConverter.ToSingle(binary.Skip(start + i * 4).Take(4).ToArray(), 0);
                binary.RemoveRange(0, tail + 4); AddSample(values);
            }
        }

        private void AddSample(double[] values)
        {
            samples.Add(new Sample { Id = ++nextSample, Time = clock.Elapsed.TotalSeconds, V = values });
            if (samples.Count > 5000) samples.RemoveRange(0, samples.Count - 5000);
        }

        private void AddLog(string value)
        {
            logs.Add(new LogEntry { Id = ++nextLog, Time = clock.Elapsed.TotalSeconds, Text = value });
            if (logs.Count > 3000) logs.RemoveRange(0, logs.Count - 3000);
        }

        private string LogsJson(long after)
        {
            lock (sync)
            {
                IEnumerable<LogEntry> list = logs.Where(x => x.Id > after);
                return "[" + String.Join(",", list.Select(x => "{\"id\":" + x.Id + ",\"t\":" +
                    x.Time.ToString("F3", CultureInfo.InvariantCulture) + ",\"text\":" + Q(x.Text) + "}").ToArray()) + "]";
            }
        }

        private string SamplesJson(long after)
        {
            lock (sync)
            {
                IEnumerable<Sample> list = samples.Where(s => s.Id > after);
                return "[" + String.Join(",", list.Select(s => "{\"id\":" + s.Id + ",\"t\":" + s.Time.ToString("F3", CultureInfo.InvariantCulture) +
                    ",\"v\":[" + String.Join(",", s.V.Select(v => v.ToString("R", CultureInfo.InvariantCulture)).ToArray()) + "]}").ToArray()) + "]";
            }
        }

        private string FindKeil()
        {
            List<string> paths = new List<string>(); string env = Environment.GetEnvironmentVariable("KEIL_UVISION_PATH"); if (!String.IsNullOrWhiteSpace(env)) paths.Add(env);
            string saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CBoardMotorTool", "keil_path.txt"); if (File.Exists(saved)) paths.Add(File.ReadAllText(saved).Trim());
            foreach (string keyName in new[] { @"SOFTWARE\WOW6432Node\Keil\Products\MDK", @"SOFTWARE\Keil\Products\MDK" }) using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyName))
            { if (key != null && key.GetValue("Path") != null) { string p = key.GetValue("Path").ToString(); paths.Add(Path.Combine(p, "UV4", "UV4.exe")); DirectoryInfo parent = Directory.GetParent(p.TrimEnd('\\')); if (parent != null) paths.Add(Path.Combine(parent.FullName, "UV4", "UV4.exe")); } }
            foreach (DriveInfo d in DriveInfo.GetDrives().Where(x => x.IsReady)) foreach (string rel in new[] { @"Keil_v5\UV4\UV4.exe", @"Keil5\UV4\UV4.exe", @"Keil\UV4\UV4.exe" }) paths.Add(Path.Combine(d.RootDirectory.FullName, rel));
            string found = paths.FirstOrDefault(File.Exists);
            if (found == null) using (OpenFileDialog dlg = new OpenFileDialog { Filter = "Keil uVision|UV4.exe", Title = "请选择 UV4.exe（只需一次）" }) if (dlg.ShowDialog() == DialogResult.OK) found = dlg.FileName;
            if (found != null) { Directory.CreateDirectory(Path.GetDirectoryName(saved)); File.WriteAllText(saved, found); }
            return found;
        }

        private void Keil(string mode)
        {
            string uv4 = FindKeil(); if (uv4 == null) throw new InvalidOperationException("未找到 Keil");
            if (mode == "open") { Process.Start(uv4, "\"" + keilProject + "\""); return; }
            string log = Path.Combine(root, "MDK-ARM", "web_build.log"); Process p = Process.Start(new ProcessStartInfo(uv4, "-b \"" + keilProject + "\" -j0 -o \"" + log + "\"") { WindowStyle = ProcessWindowStyle.Hidden }); p.WaitForExit();
            if (!File.Exists(log) || !File.ReadAllText(log).Contains("0 Error(s)")) throw new InvalidOperationException("编译未通过，请查看 " + log);
        }

        private void BeginInvokeShutdown()
        {
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(150); if (tray != null) tray.ContextMenuStrip.BeginInvoke((MethodInvoker)Shutdown); });
        }

        private void Shutdown()
        {
            lock (sync) { if (serial != null) { try { serial.Close(); } catch { } serial.Dispose(); serial = null; } }
            try { if (File.Exists(portFile)) File.Delete(portFile); } catch { }
            tray.Visible = false; tray.Dispose(); try { listener.Stop(); } catch { } ExitThread();
        }
    }
}
