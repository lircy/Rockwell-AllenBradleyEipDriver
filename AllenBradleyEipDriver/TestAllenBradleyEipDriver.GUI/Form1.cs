using AB_EIP_CONTROL_LOGIX_SERIES;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TestAllenBradleyEipDriver.GUI
{
    /// <summary>
    /// AllenBradleyEipDriver (罗克韦尔 ControlLogix/CompactLogix/SoftLogix
    /// EtherNet/IP) GUI 测试 demo — 窗体样式严格对照 AllenBradleyEipDriverDemo_Optimize
    /// （头部横幅/连接/读写/工程加载/树浏览/日志区统一布局），事件处理以当前核心库
    /// 原生 API 实现；FormNativeDiag 诊断窗不引入（无契约依赖）。
    /// </summary>
    public partial class Form1 : Form
    {
        private const string ProjectFilter = "Studio 5000 工程归档 (*.acd)|*.acd";

        private readonly AllenBradleyEipDriver controller = new AllenBradleyEipDriver();

        public Form1()
        {
            InitializeComponent();
            controller.Reconnect.Enabled = true;
            // ILogger 注入：核心库生命周期日志输出到日志区（未注入时内部为 NullLogger）。
            controller.Logger = new TextBoxLogger(tbLog);
            controller.OnConnectionLost += () => InvokeIfNeeded(() => Log("[事件] ConnectionLost (连接断开，自动重连中...)"));
            controller.OnReconnecting += (attempt) => InvokeIfNeeded(() => Log("[事件] 第 " + attempt + " 次重连尝试..."));
            controller.OnReconnected += () => InvokeIfNeeded(() => Log("[事件] 重连成功"));
        }

        // ---------- 公共辅助 ----------

        private void InvokeIfNeeded(Action action)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { /* 窗口关闭过程中的回调 */ }
        }

        private void Log(string message)
        {
            tbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + Environment.NewLine);
        }

        private void setStatus(string status)
        {
            lbStatus.Text = status;
        }

        /// <summary>未连接则按界面参数自动连接；返回是否可用。</summary>
        private bool EnsureConnected()
        {
            if (controller.IsConnected) return true;
            try
            {
                int port;
                byte slot;
                if (!int.TryParse(tbPort.Text.Trim(), out port) || port <= 0) port = 44818;
                if (!byte.TryParse(tbUser.Text.Trim(), out slot)) slot = 1;
                uint st = controller.Connect(tbHost.Text.Trim(), slot, port);
                if (st == 0)
                {
                    DeviceInfo info = controller.ControllerInfo;
                    Log("已连接 " + tbHost.Text.Trim() + " 槽号 " + slot
                        + (info != null ? "，" + info.ShortDescription : ""));
                }
                else
                {
                    Log("连接失败 0x" + st.ToString("X"));
                }
                return controller.IsConnected;
            }
            catch (Exception ex)
            {
                Log("连接异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>取 ItemAddress 的完整符号路径；失败返回 null。</summary>
        private static string SymbolOf(ItemAddress item)
        {
            string symbol;
            return item != null && item.GetSymbol(out symbol) == 0 ? symbol : null;
        }

        /// <summary>树节点显示文本：非 ReadWrite 的节点加访问权限后缀。</summary>
        private static string NodeText(ItemAddress item)
        {
            switch (item.ExternalAccess)
            {
                case ABExternalAccess.ReadOnly: return item.Name + "（只读）";
                case ABExternalAccess.None: return item.Name + "（不可访问）";
                default: return item.Name;
            }
        }

        /// <summary>把 ROOT 下一层符号填进树视图（懒加载：有子节点的挂占位）。</summary>
        private void FillRootNodes(IntPtr rootHandle)
        {
            treeView1.Nodes.Clear();
            if (rootHandle == IntPtr.Zero) return;
            List<ItemAddress> items = controller.Browse(rootHandle);
            if (items == null) return;
            foreach (ItemAddress item in items)
            {
                TreeNode tn = treeView1.Nodes.Add(NodeText(item));
                tn.Tag = item;
                int childCount;
                if (item.GetChildCount(out childCount) == 0 && childCount > 0)
                    tn.Nodes.Add("Loading...");
            }
        }

        /// <summary>选中叶与 symbol 一致时的写权限拦截（只读/不可访问）：
        /// 返回拒绝原因文案，可写返回 null。</summary>
        private string SelectedLeafBlockReason(string symbol)
        {
            TreeNode ui = treeView1 == null ? null : treeView1.SelectedNode;
            ItemAddress item = ui == null ? null : ui.Tag as ItemAddress;
            if (item == null) return null;
            string path = SymbolOf(item);
            if (path == null || !string.Equals(path, symbol, StringComparison.Ordinal)) return null;
            switch (item.ExternalAccess)
            {
                case ABExternalAccess.ReadOnly: return "Read Only（只读）";
                case ABExternalAccess.None: return "None（不可访问）";
                default: return null;
            }
        }

        /// <summary>文本 → PValue（按类型枚举名解析；复合类型不支持文本写）。</summary>
        private static PValue BuildValue(string dataType, string text)
        {
            switch ((dataType ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "BOOL":
                    {
                        string t = (text ?? string.Empty).Trim();
                        if (t == "1") return new ValueBool(true);
                        if (t == "0") return new ValueBool(false);
                        return new ValueBool(bool.Parse(t));
                    }
                case "SINT": return new ValueSInt(sbyte.Parse(text));
                case "INT": return new ValueInt(short.Parse(text));
                case "DINT": return new ValueDInt(int.Parse(text));
                case "LINT": return new ValueLInt(long.Parse(text));
                case "USINT": return new ValueUSInt(byte.Parse(text));
                case "UINT": return new ValueUInt(ushort.Parse(text));
                case "UDINT": return new ValueUDInt(uint.Parse(text));
                case "ULINT": return new ValueULInt(ulong.Parse(text));
                case "REAL": return new ValueReal(float.Parse(text));
                case "LREAL": return new ValueLReal(double.Parse(text));
                case "STRING": return new ValueString(text);
                default: throw new NotSupportedException("该类型不支持文本写入: " + dataType);
            }
        }

        // ---------- 连接 ----------

        private void btnConnect_Click(object sender, EventArgs e)
        {
            setStatus("connecting...");
            if (EnsureConnected()) setStatus("connected");
            else setStatus("error");
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            setStatus("disconnecting...");
            controller.Disconnect();
            treeView1.Nodes.Clear();
            tbSymbol.Text = string.Empty;
            tbDatatype.Text = string.Empty;
            tbValue.Text = string.Empty;
            setStatus("disconnected");
        }

        /// <summary>遍历当前符号树全部叶，返回完整符号路径清单。</summary>
        private List<string> GetAllSymbols()
        {
            List<string> symbols = new List<string>();
            foreach (ItemAddress item in controller.LoadAllSymbolicInfo())
            {
                string symbol;
                if (item.GetSymbol(out symbol) == 0)
                    symbols.Add(symbol);
            }
            return symbols;
        }

        /// <summary>过滤出可符号读的路径：排除 __ 系统标签与 ZZZZZZZZZZ BOOL 宿主；
        /// 含冒号的模块标签（Local:3:C...）不参与抽样，但程序域 Program: 标签是普通
        /// 控制器标签（与契约版口径一致），放行参与抽样。</summary>
        private static bool IsReadable(string symbol)
        {
            if (symbol.StartsWith("__") || symbol.Contains("ZZZZZZZZZZ")) return false;
            if (symbol.Contains(":") && !symbol.StartsWith("Program:")) return false;
            return true;
        }

        private void btnSmoke_Click(object sender, EventArgs e)
        {
            string path = tbProject.Text.Trim();
            bool hasProject = path.Length > 0 && File.Exists(path);
            if (!EnsureConnected()) return;
            btnSmoke.Enabled = false;
            setStatus("smoke running...");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool overall = true;
                try
                {
                    List<string> offlineSymbols = null;
                    if (hasProject)
                    {
                        controller.LoadSymbolsFromFile(path);
                        offlineSymbols = GetAllSymbols();
                        InvokeIfNeeded(() => Log("冒烟: 离线符号 " + offlineSymbols.Count + " (对拍基准)"));
                    }
                    controller.LoadSymbolsFromPLC();
                    List<string> onlineSymbols = GetAllSymbols();
                    InvokeIfNeeded(() => Log("冒烟: 在线符号 " + onlineSymbols.Count));
                    if (offlineSymbols != null)
                    {
                        var offlineSet = new HashSet<string>(offlineSymbols);
                        var onlineSet = new HashSet<string>(onlineSymbols);
                        var missingOnline = offlineSet.Where(s => !onlineSet.Contains(s)).ToList();
                        var missingOffline = onlineSet.Where(s => !offlineSet.Contains(s)).ToList();
                        int diff = missingOnline.Count + missingOffline.Count;
                        overall &= diff == 0;
                        InvokeIfNeeded(() =>
                        {
                            Log("冒烟: 离线/在线对拍 " + (diff == 0
                                ? "一致 (" + offlineSet.Count + " 个符号)"
                                : "差异 " + diff + " (离线缺 " + missingOffline.Count + ", 在线缺 " + missingOnline.Count + ")"));
                            foreach (string s in missingOnline.Take(10)) Log("  在线缺少: " + s);
                            foreach (string s in missingOffline.Take(10)) Log("  离线缺少: " + s);
                        });
                    }
                    List<string> readable = onlineSymbols.Where(IsReadable).ToList();
                    List<string> sample = readable.Take(30).ToList();
                    int ok = 0, fail = 0;
                    foreach (string sym in sample)
                    {
                        try
                        {
                            List<PValue> v = new List<PValue>();
                            if (controller.Read(new List<string> { sym }, out v) == 0) ok++;
                            else { fail++; InvokeIfNeeded(() => Log("  读失败: " + sym)); }
                        }
                        catch (Exception ex) { fail++; InvokeIfNeeded(() => Log("  异常: " + sym + " -> " + ex.Message)); }
                    }
                    overall &= fail == 0;
                    InvokeIfNeeded(() => Log("冒烟: 抽样读 " + ok + "/" + sample.Count));
                    List<string> batch = readable.Take(50).ToList();
                    if (batch.Count > 0)
                    {
                        List<PValue> bv = new List<PValue>();
                        ushort bst = controller.Read(batch, out bv);
                        bool bok = bst == 0 && bv.Count == batch.Count;
                        overall &= bok;
                        InvokeIfNeeded(() => Log("冒烟: 批量读 " + batch.Count + " -> " + (bok ? "成功" : "失败 0x" + bst.ToString("X4"))));
                    }
                }
                catch (Exception ex)
                {
                    overall = false;
                    InvokeIfNeeded(() => Log("冒烟异常: " + ex.Message));
                }
                finally
                {
                    InvokeIfNeeded(() =>
                    {
                        setStatus(overall ? "smoke passed" : "smoke failed");
                        btnSmoke.Enabled = true;
                    });
                }
            });
        }

        // ---------- 符号树 (在线 / 离线 .acd, 懒加载) ----------

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog { Filter = ProjectFilter })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    tbProject.Text = dlg.FileName;
            }
        }

        private void btnLoadProject_Click(object sender, EventArgs e)
        {
            string path = tbProject.Text.Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                Log("请先选择存在的 .acd 工程归档");
                return;
            }
            try
            {
                setStatus("loading...");
                IntPtr rootHandle = controller.LoadSymbolsFromFile(path);
                ACDControllerInfo info = controller.OfflineControllerInfo;
                if (info != null)
                {
                    string sn = !string.IsNullOrEmpty(info.SerialNumber) ? "，SN " + info.SerialNumber : string.Empty;
                    string sw = !string.IsNullOrEmpty(info.SoftwareVersion) ? "，" + info.SoftwareVersion : string.Empty;
                    Log("离线工程控制器信息: " + info + sn + sw);
                }
                FillRootNodes(rootHandle);
                setStatus("load finished");
                Log("离线符号树已加载: " + path);
            }
            catch (Exception ex)
            {
                setStatus("error");
                Log("加载符号树失败: " + ex.Message);
            }
        }

        private void btnOnlineTree_Click(object sender, EventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                setStatus("loading online tree...");
                IntPtr rootHandle = controller.LoadSymbolsFromPLC();
                FillRootNodes(rootHandle);
                setStatus("online tree finished");
                Log("在线符号树已加载");
            }
            catch (Exception ex)
            {
                setStatus("error");
                Log("在线取树失败: " + ex.Message);
            }
        }

        private void treeView1_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Text != "Loading...") return;
            ItemAddress parent = e.Node.Tag as ItemAddress;
            e.Node.Nodes.Clear();
            if (parent == null) return;
            try
            {
                List<ItemAddress> children = controller.Browse(parent.Handle);
                if (children == null) return;
                foreach (ItemAddress child in children)
                {
                    TreeNode tn = e.Node.Nodes.Add(NodeText(child));
                    tn.Tag = child;
                    int childCount;
                    if (child.GetChildCount(out childCount) == 0 && childCount > 0)
                        tn.Nodes.Add("Loading...");
                }
            }
            catch (Exception ex)
            {
                Log("展开节点失败: " + ex.Message);
            }
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            ItemAddress item = e.Node == null ? null : e.Node.Tag as ItemAddress;
            if (item == null) return;
            string symbol = SymbolOf(item);
            if (symbol != null)
            {
                tbSymbol.Text = symbol;
                tbDatatype.Text = item.Datatype.ToString();
            }
        }

        // ---------- 读写 ----------

        private void btnRead_Click(object sender, EventArgs e)
        {
            if (!EnsureConnected()) return;
            string symbol = tbSymbol.Text.Trim();
            if (symbol.Length == 0) return;
            try
            {
                List<PValue> values = new List<PValue>();
                ushort st = controller.Read(new List<string> { symbol }, out values);
                if (st == 0 && values.Count > 0)
                {
                    tbValue.Text = values[0].ToStringWithType();
                    Log("读 " + symbol + " = " + values[0].ToStringWithType());
                }
                else
                {
                    Log("读 " + symbol + " 失败 0x" + st.ToString("X"));
                }
            }
            catch (Exception ex)
            {
                Log("读异常: " + ex.Message);
            }
        }

        private void btnWrite_Click(object sender, EventArgs e)
        {
            if (!EnsureConnected()) return;
            string symbol = tbSymbol.Text.Trim();
            if (symbol.Length == 0) return;
            string block = SelectedLeafBlockReason(symbol);
            if (block != null) { Log("写 " + symbol + " 被拒绝: " + block); return; }
            try
            {
                PValue value = BuildValue(tbDatatype.Text, tbValue.Text.Trim());
                ushort st = controller.Write(symbol, value);
                Log("写 " + symbol + " ← " + tbValue.Text + " : " + (st == 0 ? "成功" : "失败 0x" + st.ToString("X")));
            }
            catch (Exception ex)
            {
                Log("写异常: " + ex.Message);
            }
        }

        private void btnWriteText_Click(object sender, EventArgs e)
        {
            if (!EnsureConnected()) return;
            string symbol = tbSymbol.Text.Trim();
            if (symbol.Length == 0) return;
            string block = SelectedLeafBlockReason(symbol);
            if (block != null) { Log("文本写 " + symbol + " 被拒绝: " + block); return; }
            try
            {
                // 先读当前值确定类型，再按文本解析写入（模拟契约 WriteByText 语义）
                List<PValue> current = new List<PValue>();
                ushort rst = controller.Read(new List<string> { symbol }, out current);
                if (rst != 0 || current.Count == 0)
                {
                    Log("文本写失败: 无法读取当前值确定类型 (0x" + rst.ToString("X") + ")");
                    return;
                }
                string typeName = current[0].GetType().Name;   // e.g. ValueDInt
                string dataType = typeName.StartsWith("Value", StringComparison.Ordinal) ? typeName.Substring(5) : typeName;
                PValue value = BuildValue(dataType, tbValue.Text.Trim());
                ushort st = controller.Write(symbol, value);
                Log("文本写 " + symbol + " ← " + tbValue.Text + " (" + dataType + ") : " + (st == 0 ? "成功" : "失败 0x" + st.ToString("X")));
            }
            catch (Exception ex)
            {
                Log("文本写异常: " + ex.Message);
            }
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            try { controller.Disconnect(); } catch { }
        }
    }

    /// <summary>
    /// 演示 ILogger 注入的最小实现：把核心库日志追加到日志文本框。
    /// 日志可能来自后台线程（重连线程等），内部统一 BeginInvoke 切回 UI 线程。
    /// </summary>
    sealed class TextBoxLogger : ILogger
    {
        private readonly TextBox _textBox;

        public TextBoxLogger(TextBox textBox)
        {
            _textBox = textBox;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter)
        {
            Append("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + logLevel + "] "
                + formatter(state, exception) + Environment.NewLine);
        }

        private void Append(string text)
        {
            TextBox box = _textBox;
            if (box == null || box.IsDisposed) return;
            try
            {
                if (box.InvokeRequired) box.BeginInvoke(new Action(() => Append(text)));
                else box.AppendText(text);
            }
            catch { /* 窗口关闭过程中的回调 */ }
        }
    }
}
