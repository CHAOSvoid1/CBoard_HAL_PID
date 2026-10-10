using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using Microsoft.Win32;

namespace CBoardMotorTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(15, 18, 24);
        private static readonly Color PanelBg = Color.FromArgb(23, 28, 37);
        private static readonly Color CardBg = Color.FromArgb(30, 36, 47);
        private static readonly Color InputBg = Color.FromArgb(18, 22, 30);
        private static readonly Color Fore = Color.FromArgb(225, 231, 239);
        private static readonly Color Muted = Color.FromArgb(151, 163, 180);
        private static readonly Color Accent = Color.FromArgb(72, 149, 239);
        private static readonly Color Border = Color.FromArgb(55, 65, 81);
        private static readonly Color Success = Color.FromArgb(34, 197, 94);
        private static readonly Color Danger = Color.FromArgb(239, 68, 68);
        private readonly string root;
        private readonly string configPath;
        private readonly string keilProject;
        private readonly ComboBox motor = Combo();
        private readonly NumericUpDown motorId = new NumericUpDown();
        private readonly ComboBox can = Combo();
        private readonly ComboBox boardUart = Combo();
        private readonly ComboBox defaultFormat = Combo();
        private readonly ComboBox com = Combo();
        private readonly ComboBox rxFormat = Combo();
        private readonly TextBox target = new TextBox();
        private readonly TextBox rawCommand = new TextBox();
        private readonly TextBox log = new TextBox();
        private readonly Label state = new Label();
        private readonly Chart speedChart = MakeChart("速度曲线", "rpm");
        private readonly Chart positionChart = MakeChart("位置曲线", "deg / raw / °C");
        private readonly Timer serialTimer = new Timer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<byte> binary = new List<byte>();
        private readonly StringBuilder textRx = new StringBuilder();
        private SerialPort port;
        private Button connectButton;
        private bool plotPaused;
        private int sampleCount;

        public MainForm()
        {
            root = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
            configPath = Path.Combine(root, "User_Files", "app_config.h");
            keilProject = Path.Combine(root, "MDK-ARM", "CBoard.uvprojx");
            Text = "C板单电机测试台 - M2006+C610 / M3508+C620";
            Width = 1220;
            Height = 820;
            MinimumSize = new Size(1100, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9.25F);
            BackColor = Bg;
            ForeColor = Fore;

            TabControl tabs = new TabControl { Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed, ItemSize = new Size(180, 38), Padding = new Point(20, 6) };
            tabs.DrawItem += DrawDarkTab;
            TabPage configTab = new TabPage("  固件配置  ") { BackColor = Bg, ForeColor = Fore, Padding = new Padding(12) };
            TabPage controlTab = new TabPage("  串口控制与实时曲线  ") { BackColor = Bg, ForeColor = Fore, Padding = new Padding(8) };
            tabs.TabPages.Add(configTab);
            tabs.TabPages.Add(controlTab);
            Controls.Add(tabs);
            BuildConfigTab(configTab);
            BuildControlTab(controlTab);
            ApplyDarkTheme(this);
            LoadConfig();
            RefreshPorts();

            serialTimer.Interval = 30;
            serialTimer.Tick += delegate { PollSerial(); };
            serialTimer.Start();
            FormClosing += delegate { ClosePort(); };
        }

        private static ComboBox Combo()
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = InputBg, ForeColor = Fore };
        }

        private static Label LabelAt(string text, int x, int y, int width)
        {
            return new Label { Text = text, Left = x, Top = y, Width = width, Height = 26, TextAlign = ContentAlignment.MiddleLeft };
        }

        private static Button ButtonAt(string text, int x, int y, int width)
        {
            return new Button { Text = text, Left = x, Top = y, Width = width, Height = 32,
                FlatStyle = FlatStyle.Flat, BackColor = CardBg, ForeColor = Fore,
                Cursor = Cursors.Hand, FlatAppearance = { BorderColor = Border, BorderSize = 1,
                MouseOverBackColor = Color.FromArgb(45, 55, 72), MouseDownBackColor = Color.FromArgb(38, 46, 61) } };
        }

        private void DrawDarkTab(object sender, DrawItemEventArgs e)
        {
            TabControl tabs = (TabControl)sender;
            Rectangle bounds = e.Bounds;
            bool selected = e.Index == tabs.SelectedIndex;
            using (SolidBrush back = new SolidBrush(selected ? CardBg : Bg)) e.Graphics.FillRectangle(back, bounds);
            if (selected) using (SolidBrush line = new SolidBrush(Accent)) e.Graphics.FillRectangle(line, bounds.Left, bounds.Bottom - 3, bounds.Width, 3);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Font, bounds,
                selected ? Color.White : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void ApplyDarkTheme(Control parent)
        {
            foreach (Control c in parent.Controls) {
                if (c is TabControl) { c.BackColor = Bg; c.ForeColor = Fore; }
                else if (c is TabPage) { c.BackColor = Bg; c.ForeColor = Fore; }
                else if (c is GroupBox) { c.BackColor = CardBg; c.ForeColor = Fore; }
                else if (c is Panel || c is SplitterPanel) { c.BackColor = PanelBg; c.ForeColor = Fore; }
                else if (c is TextBox) { c.BackColor = InputBg; c.ForeColor = Fore; }
                else if (c is NumericUpDown) { c.BackColor = InputBg; c.ForeColor = Fore; }
                else if (c is Label) { c.ForeColor = c == state ? Success : Fore; c.BackColor = Color.Transparent; }
                else if (c is Button) {
                    Button b = (Button)c; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Border;
                    b.ForeColor = Color.White;
                    if (b.Text.Contains("STOP")) b.BackColor = Danger;
                    else if (b.Text.Contains("RUN")) b.BackColor = Success;
                    else if (b.Text.Contains("保存") || b.Text.Contains("编译") || b.Text.Contains("连接")) b.BackColor = Accent;
                    else b.BackColor = CardBg;
                }
                if (c.HasChildren) ApplyDarkTheme(c);
            }
        }

        private void BuildConfigTab(Control page)
        {
            Label intro = new Label {
                Left = 20, Top = 18, Width = 1120, Height = 55,
                Text = "用途：一块 RoboMaster C 型开发板作为唯一主控，一次闭环测试一台 M2006+C610 或 M3508+C620。\r\n修改此页后需重新编译并烧录；串口目标值和 PID 命令可即时生效。"
            };
            page.Controls.Add(intro);
            page.Controls.Add(LabelAt("电机型号", 25, 90, 80));
            motor.SetBounds(110, 90, 180, 26);
            motor.Items.AddRange(new object[] { "M3508 + C620", "M2006 + C610" });
            page.Controls.Add(motor);
            page.Controls.Add(LabelAt("电调 ID", 325, 90, 70));
            motorId.SetBounds(400, 90, 70, 26); motorId.Minimum = 1; motorId.Maximum = 8;
            page.Controls.Add(motorId);
            page.Controls.Add(LabelAt("CAN", 510, 90, 45));
            can.SetBounds(555, 90, 155, 26); can.Items.AddRange(new object[] { "CAN1（2Pin）", "CAN2（4Pin）" });
            page.Controls.Add(can);
            page.Controls.Add(LabelAt("C板串口", 745, 90, 70));
            boardUart.SetBounds(820, 90, 270, 26);
            boardUart.Items.AddRange(new object[] { "串口1 → USART6 PG14/PG9", "串口2 → USART1 PA9/PB7" });
            page.Controls.Add(boardUart);
            page.Controls.Add(LabelAt("默认协议", 25, 140, 80));
            defaultFormat.SetBounds(110, 140, 180, 26); defaultFormat.Items.AddRange(new object[] { "FireWater", "JustFloat" });
            page.Controls.Add(defaultFormat);

            Button save = ButtonAt("保存配置", 325, 136, 120);
            Button open = ButtonAt("保存并打开 Keil", 460, 136, 150);
            Button build = ButtonAt("一键编译", 625, 136, 120);
            save.Click += delegate { SaveConfig(); };
            open.Click += delegate { if (SaveConfig()) OpenKeil(false); };
            build.Click += delegate { if (SaveConfig()) OpenKeil(true); };
            page.Controls.Add(save); page.Controls.Add(open); page.Controls.Add(build);
            state.SetBounds(25, 195, 1100, 40); state.ForeColor = Success;
            page.Controls.Add(state);

            GroupBox notes = new GroupBox { Text = "首次测试", Left = 20, Top = 255, Width = 1120, Height = 205 };
            notes.Controls.Add(new Label {
                Left = 20, Top = 28, Width = 1070, Height = 150,
                Text = "1. 架空或卸载电机，确认 C 板、电调和 USB-TTL 共地。\r\n" +
                       "2. 默认 ID=5、CAN1、丝印串口1（实际 USART6）。必须与接线和电调拨码一致。\r\n" +
                       "3. M2006 默认限流 ±3000 raw；M3508 默认限流 ±4000 raw。\r\n" +
                       "4. 烧录后先连接串口并点击 STOP，再设速度模式和 100~200 rpm，最后点击 RUN。\r\n" +
                       "5. online 必须为 1、rx_count 必须增长；速度环稳定后才能测试位置环。"
            });
            page.Controls.Add(notes);
        }

        private void BuildControlTab(Control page)
        {
            Panel top = new Panel { Dock = DockStyle.Top, Height = 126 };
            page.Controls.Add(top);
            top.Controls.Add(LabelAt("电脑 COM", 10, 10, 75));
            com.SetBounds(85, 10, 90, 26); top.Controls.Add(com);
            Button refresh = ButtonAt("刷新", 182, 7, 65); refresh.Click += delegate { RefreshPorts(); }; top.Controls.Add(refresh);
            connectButton = ButtonAt("连接", 254, 7, 72); connectButton.Click += delegate { TogglePort(); }; top.Controls.Add(connectButton);
            top.Controls.Add(LabelAt("协议", 342, 10, 45));
            rxFormat.SetBounds(390, 10, 105, 26); rxFormat.Items.AddRange(new object[] { "FireWater", "JustFloat" }); rxFormat.SelectedIndex = 0;
            top.Controls.Add(rxFormat);
            Button setFormat = ButtonAt("切换设备格式", 505, 7, 115);
            setFormat.Click += delegate { Send(rxFormat.SelectedIndex == 1 ? "FORMAT:JUSTFLOAT" : "FORMAT:FIREWATER"); binary.Clear(); };
            top.Controls.Add(setFormat);
            Button pause = ButtonAt("暂停曲线", 630, 7, 90); pause.Click += delegate { plotPaused = !plotPaused; pause.Text = plotPaused ? "继续曲线" : "暂停曲线"; };
            top.Controls.Add(pause);
            Button clear = ButtonAt("清空曲线", 728, 7, 90); clear.Click += delegate { ClearPlots(); }; top.Controls.Add(clear);

            AddCommandButton(top, "STOP 急停", "STOP", 10, 48, Color.MistyRose);
            AddCommandButton(top, "RUN 使能", "RUN", 112, 48, Color.Honeydew);
            AddCommandButton(top, "ZERO 置零", "ZERO", 214, 48, SystemColors.Control);
            AddCommandButton(top, "速度模式", "MODE:SPEED", 316, 48, SystemColors.Control);
            AddCommandButton(top, "位置模式", "MODE:POSITION", 418, 48, SystemColors.Control);
            AddCommandButton(top, "HELP", "HELP", 520, 48, SystemColors.Control);
            target.SetBounds(630, 52, 75, 25); target.Text = "200"; top.Controls.Add(target);
            Button speed = ButtonAt("设速度", 710, 48, 75); speed.Click += delegate { Send("SPEED:" + target.Text.Trim()); }; top.Controls.Add(speed);
            Button pos = ButtonAt("设位置", 790, 48, 75); pos.Click += delegate { Send("POS:" + target.Text.Trim()); }; top.Controls.Add(pos);

            rawCommand.SetBounds(10, 91, 450, 25); rawCommand.Text = "PID:SPEED,1.0,2.0,0.0"; top.Controls.Add(rawCommand);
            Button raw = ButtonAt("发送命令", 468, 87, 90); raw.Click += delegate { Send(rawCommand.Text.Trim()); }; top.Controls.Add(raw);

            SplitContainer split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 365 };
            page.Controls.Add(split); split.BringToFront(); top.BringToFront();
            TableLayoutPanel charts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            charts.Controls.Add(speedChart, 0, 0); charts.Controls.Add(positionChart, 1, 0);
            split.Panel1.Controls.Add(charts);
            log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Dock = DockStyle.Fill;
            log.Font = new Font("Cascadia Mono", 9F); log.BackColor = InputBg; log.ForeColor = Color.FromArgb(188, 230, 192); log.BorderStyle = BorderStyle.FixedSingle;
            split.Panel2.Controls.Add(log);
            InitSeries();
        }

        private void AddCommandButton(Control parent, string title, string command, int x, int y, Color color)
        {
            Button button = ButtonAt(title, x, y, 94); button.BackColor = color;
            button.Click += delegate { Send(command); }; parent.Controls.Add(button);
        }

        private static Chart MakeChart(string title, string yTitle)
        {
            Chart chart = new Chart { Dock = DockStyle.Fill, BackColor = PanelBg, BorderlineColor = Border,
                BorderlineDashStyle = ChartDashStyle.Solid, BorderlineWidth = 1 };
            ChartArea area = new ChartArea("main"); area.AxisX.Title = "时间 / s"; area.AxisY.Title = yTitle;
            area.BackColor = InputBg; area.AxisX.MajorGrid.LineColor = Border; area.AxisY.MajorGrid.LineColor = Border;
            area.AxisX.LabelStyle.ForeColor = Muted; area.AxisY.LabelStyle.ForeColor = Muted;
            area.AxisX.TitleForeColor = Muted; area.AxisY.TitleForeColor = Muted;
            area.AxisX.LineColor = Border; area.AxisY.LineColor = Border;
            area.AxisX.MajorTickMark.LineColor = Border; area.AxisY.MajorTickMark.LineColor = Border;
            chart.ChartAreas.Add(area);
            Title chartTitle = new Title(title) { ForeColor = Fore, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
            chart.Titles.Add(chartTitle);
            Legend legend = new Legend { BackColor = PanelBg, ForeColor = Fore, BorderColor = Border };
            chart.Legends.Add(legend);
            return chart;
        }

        private static void AddSeries(Chart chart, string name, Color color)
        {
            Series s = new Series(name) { ChartType = SeriesChartType.FastLine, BorderWidth = 2, Color = color, XValueType = ChartValueType.Double };
            chart.Series.Add(s);
        }

        private void InitSeries()
        {
            AddSeries(speedChart, "目标速度", Color.RoyalBlue); AddSeries(speedChart, "实际速度", Color.OrangeRed);
            AddSeries(positionChart, "目标位置", Color.ForestGreen); AddSeries(positionChart, "实际位置", Color.MediumVioletRed);
            AddSeries(positionChart, "电流raw", Color.DarkOrange); AddSeries(positionChart, "温度", Color.SlateBlue);
        }

        private void ClearPlots()
        {
            foreach (Series s in speedChart.Series) s.Points.Clear();
            foreach (Series s in positionChart.Series) s.Points.Clear();
            sampleCount = 0; clock.Restart();
        }

        private void Plot(double[] v)
        {
            if (plotPaused || v.Length < 8) return;
            double x = clock.Elapsed.TotalSeconds;
            speedChart.Series[0].Points.AddXY(x, v[0]); speedChart.Series[1].Points.AddXY(x, v[1]);
            positionChart.Series[0].Points.AddXY(x, v[2]); positionChart.Series[1].Points.AddXY(x, v[3]);
            positionChart.Series[2].Points.AddXY(x, v[4]); positionChart.Series[3].Points.AddXY(x, v[5]);
            sampleCount++;
            foreach (Chart chart in new[] { speedChart, positionChart }) {
                foreach (Series s in chart.Series) while (s.Points.Count > 500) s.Points.RemoveAt(0);
                chart.ChartAreas[0].AxisX.Minimum = Math.Max(0, x - 10.0); chart.ChartAreas[0].AxisX.Maximum = Math.Max(10.0, x);
            }
        }

        private void RefreshPorts()
        {
            string old = com.Text; com.Items.Clear(); com.Items.AddRange(SerialPort.GetPortNames().OrderBy(x => x).Cast<object>().ToArray());
            if (com.Items.Contains(old)) com.SelectedItem = old; else if (com.Items.Count > 0) com.SelectedIndex = 0;
        }

        private void TogglePort()
        {
            if (port != null && port.IsOpen) { ClosePort(); return; }
            if (String.IsNullOrWhiteSpace(com.Text)) { MessageBox.Show("没有可用 COM 口。"); return; }
            try {
                port = new SerialPort(com.Text, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 20, WriteTimeout = 200 };
                port.Open(); connectButton.Text = "断开"; AppendLog("已连接 " + com.Text + "，115200-8-N-1");
            } catch (Exception ex) { MessageBox.Show("串口打开失败：" + ex.Message); ClosePort(); }
        }

        private void ClosePort()
        {
            if (port != null) { try { if (port.IsOpen) port.Close(); } catch { } port.Dispose(); port = null; }
            if (connectButton != null) connectButton.Text = "连接";
        }

        private void Send(string command)
        {
            if (port == null || !port.IsOpen) { MessageBox.Show("请先连接串口。"); return; }
            try { port.Write(command + "\r\n"); AppendLog("TX> " + command); }
            catch (Exception ex) { AppendLog("发送失败：" + ex.Message); }
        }

        private void PollSerial()
        {
            if (port == null || !port.IsOpen || port.BytesToRead <= 0) return;
            try {
                byte[] data = new byte[port.BytesToRead]; port.Read(data, 0, data.Length);
                if (rxFormat.SelectedIndex == 1) { binary.AddRange(data); DecodeBinary(); }
                else { DecodeText(data); }
            } catch (Exception ex) { AppendLog("读取失败：" + ex.Message); }
        }

        private void DecodeText(byte[] data)
        {
            textRx.Append(Encoding.ASCII.GetString(data));
            while (true) {
                string all = textRx.ToString(); int newline = all.IndexOf('\n'); if (newline < 0) break;
                string line = all.Substring(0, newline).Trim(); textRx.Remove(0, newline + 1);
                if (line.Length == 0) continue; AppendLog("RX> " + line);
                int colon = line.IndexOf(':'); string payload = colon >= 0 ? line.Substring(colon + 1) : line;
                string[] fields = payload.Split(','); double[] values = new double[fields.Length];
                bool ok = fields.Length >= 8;
                for (int i = 0; ok && i < fields.Length; i++) ok = Double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
                if (ok) Plot(values);
            }
        }

        private void DecodeBinary()
        {
            while (binary.Count >= 36) {
                int tail = -1;
                for (int i = 32; i <= binary.Count - 4; i++)
                    if (binary[i] == 0 && binary[i + 1] == 0 && binary[i + 2] == 0x80 && binary[i + 3] == 0x7F) { tail = i; break; }
                if (tail < 32) { if (binary.Count > 512) binary.RemoveRange(0, binary.Count - 64); return; }
                int start = tail - 32; double[] values = new double[8];
                for (int c = 0; c < 8; c++) values[c] = BitConverter.ToSingle(binary.Skip(start + c * 4).Take(4).ToArray(), 0);
                binary.RemoveRange(0, tail + 4); Plot(values);
                AppendLog(String.Format(CultureInfo.InvariantCulture, "JF> speed {0:F1}/{1:F1}, pos {2:F2}/{3:F2}, current {4:F0}, temp {5:F0}, online {6:F0}", values));
            }
        }

        private void AppendLog(string text)
        {
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + "\r\n");
            log.SelectionStart = log.TextLength; log.ScrollToCaret();
        }

        private string ConfigValue(string text, string name)
        {
            Match m = Regex.Match(text, "(?m)^\\s*#define\\s+" + Regex.Escape(name) + "\\s+([^\\s/]+)"); return m.Success ? m.Groups[1].Value : "";
        }

        private string SetDefine(string text, string name, string value)
        {
            string pattern = "(?m)^\\s*#define\\s+" + Regex.Escape(name) + "\\s+.*$";
            if (!Regex.IsMatch(text, pattern)) throw new InvalidOperationException("配置项不存在：" + name);
            return Regex.Replace(text, pattern, "#define " + name.PadRight(31) + value);
        }

        private void LoadConfig()
        {
            try {
                string t = File.ReadAllText(configPath, Encoding.UTF8);
                motor.SelectedIndex = ConfigValue(t, "APP_MOTOR_TYPE") == "MOTOR_TYPE_M2006" ? 1 : 0;
                motorId.Value = Decimal.Parse(Regex.Replace(ConfigValue(t, "APP_MOTOR_ID"), "[^0-9]", ""));
                can.SelectedIndex = ConfigValue(t, "APP_USE_CAN1") == "0U" ? 1 : 0;
                boardUart.SelectedIndex = ConfigValue(t, "APP_USE_USART6") == "0U" ? 1 : 0;
                defaultFormat.SelectedIndex = ConfigValue(t, "APP_DEFAULT_VOFA_FORMAT") == "APP_VOFA_FORMAT_JUSTFLOAT" ? 1 : 0;
                state.Text = "配置文件：" + configPath;
            } catch (Exception ex) { state.ForeColor = Danger; state.Text = "读取配置失败：" + ex.Message; }
        }

        private bool SaveConfig()
        {
            try {
                string t = File.ReadAllText(configPath, Encoding.UTF8);
                t = SetDefine(t, "APP_MOTOR_TYPE", motor.SelectedIndex == 1 ? "MOTOR_TYPE_M2006" : "MOTOR_TYPE_M3508");
                t = SetDefine(t, "APP_MOTOR_ID", motorId.Value.ToString("0") + "U");
                t = SetDefine(t, "APP_USE_CAN1", can.SelectedIndex == 0 ? "1U" : "0U");
                t = SetDefine(t, "APP_USE_USART6", boardUart.SelectedIndex == 0 ? "1U" : "0U");
                t = SetDefine(t, "APP_DEFAULT_VOFA_FORMAT", defaultFormat.SelectedIndex == 1 ? "APP_VOFA_FORMAT_JUSTFLOAT" : "APP_VOFA_FORMAT_FIREWATER");
                File.WriteAllText(configPath, t, new UTF8Encoding(false)); state.ForeColor = Success; state.Text = "配置已保存，请重新编译并烧录。"; return true;
            } catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message); return false; }
        }

        private string FindKeil()
        {
            List<string> paths = new List<string>();
            string env = Environment.GetEnvironmentVariable("KEIL_UVISION_PATH"); if (!String.IsNullOrWhiteSpace(env)) paths.Add(env);
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CBoardMotorTool", "keil_path.txt");
            if (File.Exists(settings)) paths.Add(File.ReadAllText(settings).Trim());
            string[] keys = { @"SOFTWARE\WOW6432Node\Keil\Products\MDK", @"SOFTWARE\Keil\Products\MDK" };
            foreach (string keyName in keys) using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyName)) {
                if (key == null) continue; object value = key.GetValue("Path"); if (value == null) continue;
                string p = value.ToString(); paths.Add(Path.Combine(p, "UV4", "UV4.exe"));
                DirectoryInfo parent = Directory.GetParent(p.TrimEnd('\\')); if (parent != null) paths.Add(Path.Combine(parent.FullName, "UV4", "UV4.exe"));
            }
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady)) foreach (string rel in new[] { @"Keil_v5\UV4\UV4.exe", @"Keil5\UV4\UV4.exe", @"Keil\UV4\UV4.exe" }) paths.Add(Path.Combine(drive.RootDirectory.FullName, rel));
            string found = paths.FirstOrDefault(File.Exists);
            if (found == null) using (OpenFileDialog dlg = new OpenFileDialog { Filter = "Keil uVision|UV4.exe", Title = "请选择 Keil 安装目录中的 UV4.exe（只需一次）" }) if (dlg.ShowDialog() == DialogResult.OK) found = dlg.FileName;
            if (found != null) { Directory.CreateDirectory(Path.GetDirectoryName(settings)); File.WriteAllText(settings, found); }
            return found;
        }

        private void OpenKeil(bool build)
        {
            string uv4 = FindKeil(); if (uv4 == null) return;
            if (!build) { Process.Start(uv4, "\"" + keilProject + "\""); return; }
            string buildLog = Path.Combine(root, "MDK-ARM", "gui_build.log");
            Process p = Process.Start(new ProcessStartInfo(uv4, "-b \"" + keilProject + "\" -j0 -o \"" + buildLog + "\"") { WindowStyle = ProcessWindowStyle.Hidden });
            p.WaitForExit(); string result = File.Exists(buildLog) ? File.ReadAllText(buildLog) : "";
            MessageBox.Show(result.Contains("0 Error(s)") ? "编译成功：0 Error(s)" : "编译未通过，请查看：\r\n" + buildLog);
        }
    }
}
