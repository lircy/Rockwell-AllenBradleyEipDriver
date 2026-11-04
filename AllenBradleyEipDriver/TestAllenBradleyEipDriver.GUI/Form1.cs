using AB_EIP_CONTROL_LOGIX_SERIES;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TestAllenBradleyEipDriver.GUI
{
    /// <summary>
    /// AllenBradleyEipDriver (罗克韦尔 Allen-Bradley ControlLogix/CompactLogix/SoftLogix EtherNet/IP) GUI 测试 demo —
    /// 按驱动原生 handle 式 API 编写 (该驱动未接入 DriverContracts 统一契约):
    ///   连接/断开:   Connect(ip, slot, port) → ControllerInfo.ShortDescription; Disconnect()
    ///   变量树:      LoadSymbolsFromFile(.acd 离线) / LoadSymbolsFromPLC(在线) → Browse(handle) 惰性展开
    ///                (子节点是否可展开经 GetChildCount 判定, 叶子节点不挂占位, 无展开箭头)
    ///   读写:        Read(List&lt;string&gt;, out List&lt;PValue&gt;) / Write(tag, PValue)
    ///   冒烟:        抽样批量读×2比对 + BOOL 取反写往返恢复 (写候选仅取 ExternalAccess=ReadWrite)
    /// </summary>
    public partial class Form1 : Form
    {
        private readonly AllenBradleyEipDriver driver = new AllenBradleyEipDriver();
        private IntPtr _treeRoot = IntPtr.Zero;

        private static readonly HashSet<AllenBradleyVarType> CompositeTypes = new HashSet<AllenBradleyVarType>
        {
            AllenBradleyVarType.ROOT, AllenBradleyVarType.ARRAY, AllenBradleyVarType.STRUCT,
            AllenBradleyVarType.AbbreviatedSTRUCT, AllenBradleyVarType.VARINSTANCE,
            AllenBradleyVarType.Union, AllenBradleyVarType.Program
        };

        public Form1()
        {
            InitializeComponent();
            // 重连事件来自后台线程, Log/SetStatus 内部已 Invoke 回 UI 线程
            driver.OnConnectionLost += () => Log("连接断开, 自动重连中…");
            driver.OnReconnecting += (attempt) => Log("重连第 " + attempt + " 次…");
            driver.OnReconnected += () => Log("重连成功");
        }

        // ---------------- 日志 / 状态 ----------------

        private void Log(string msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Log), msg); } catch { } return; }
            tbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
        }

        private void SetStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(SetStatus), text); } catch { } return; }
            lbStatus.Text = text;
        }

        private int ParsePort()
        {
            int port;
            if (!int.TryParse(tbPort.Text.Trim(), out port) || port <= 0 || port > 65535)
                throw new ArgumentException("端口无效: " + tbPort.Text);
            return port;
        }

        private byte ParseSlot()
        {
            int slot;
            if (string.IsNullOrEmpty(tbUser.Text.Trim())) return 1;
            if (!int.TryParse(tbUser.Text.Trim(), out slot) || slot < 0 || slot > 255)
                throw new ArgumentException("槽号无效 (0-255): " + tbUser.Text);
            return (byte)slot;
        }

        // ---------------- 连接 ----------------

        private void btnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                string host = tbHost.Text.Trim();
                if (host.Length == 0) { MessageBox.Show("请输入 PLC 地址"); return; }
                int port = ParsePort();
                byte slot = ParseSlot();
                SetStatus("正在连接 " + host + ":" + port + " (槽号 " + slot + ") …");
                uint status = driver.Connect(host, slot, port);
                if (status != 0)
                {
                    SetStatus("连接失败");
                    MessageBox.Show("连接失败, 返回码: " + status);
                    return;
                }
                DeviceInfo info = driver.ControllerInfo;
                string device = info != null ? info.ShortDescription : "(无设备信息)";
                SetStatus("已连接 " + host + ":" + port);
                Log("连接成功: " + host + ":" + port + " (槽号 " + slot + ")  " + device);
            }
            catch (Exception ex)
            {
                SetStatus("连接失败");
                Log("连接异常: " + ex.Message);
                MessageBox.Show("连接异常: " + ex.Message);
            }
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            try
            {
                driver.Disconnect();
                SetStatus("已断开");
                Log("已断开连接");
            }
            catch (Exception ex)
            {
                Log("断开异常: " + ex.Message);
            }
        }

        // ---------------- 变量树 ----------------

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "AB 工程归档 (*.acd)|*.acd";
                dlg.Title = "选择 .acd 工程归档文件";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    tbProject.Text = dlg.FileName;
            }
        }

        private void btnLoadProject_Click(object sender, EventArgs e)
        {
            string file = tbProject.Text.Trim();
            if (file.Length == 0 || !File.Exists(file))
            {
                MessageBox.Show("请先选择存在的 .acd 工程归档文件");
                return;
            }
            SetStatus("正在离线加载工程 …");
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                IntPtr root = driver.LoadSymbolsFromFile(file);
                sw.Stop();
                if (root == IntPtr.Zero) throw new Exception("LoadSymbolsFromFile 返回空句柄");
                LoadTree(root);
                ACDControllerInfo info = driver.OfflineControllerInfo;
                string desc = info != null ? info.ToString() : string.Empty;
                Log("离线加载工程成功: " + Path.GetFileName(file) + "  建树耗时 " + sw.ElapsedMilliseconds + " ms"
                    + (desc.Length > 0 ? "  " + desc : string.Empty));
                SetStatus("离线树已加载");
            }
            catch (Exception ex)
            {
                SetStatus("加载工程失败");
                Log("加载工程失败: " + ex.Message);
                MessageBox.Show("加载工程失败: " + ex.Message);
            }
        }

        private void btnOnlineTree_Click(object sender, EventArgs e)
        {
            if (!driver.IsConnected)
            {
                MessageBox.Show("请先连接 PLC");
                return;
            }
            SetStatus("正在在线获取变量树 …");
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                IntPtr root = driver.LoadSymbolsFromPLC();
                sw.Stop();
                if (root == IntPtr.Zero) throw new Exception("LoadSymbolsFromPLC 返回空句柄");
                LoadTree(root);
                Log("在线取树成功  耗时 " + sw.ElapsedMilliseconds + " ms");
                SetStatus("在线树已加载");
            }
            catch (Exception ex)
            {
                SetStatus("在线取树失败");
                Log("在线取树失败: " + ex.Message);
                MessageBox.Show("在线取树失败: " + ex.Message);
            }
        }

        private void LoadTree(IntPtr rootHandle)
        {
            _treeRoot = rootHandle;
            treeView1.BeginUpdate();
            treeView1.Nodes.Clear();
            TreeNode root = new TreeNode("ROOT");
            root.Tag = rootHandle;
            AddDummy(root);
            treeView1.Nodes.Add(root);
            FillChildren(root);
            root.Expand();
            treeView1.EndUpdate();
        }

        private static void AddDummy(TreeNode node)
        {
            node.Nodes.Add(new TreeNode("(加载中…)"));
        }

        /// <summary>
        /// 调用驱动 Browse 展开一层子节点 (惰性加载)。
        /// 是否可展开经 ItemAddress.GetChildCount 判定 (其内部即 Browse, 返回驱动缓存),
        /// 仅给有子节点的节点挂占位 dummy, 叶子节点不挂 (无展开箭头)。
        /// </summary>
        private void FillChildren(TreeNode node)
        {
            node.Nodes.Clear();
            IntPtr handle = node.Tag is ItemAddress ia ? ia.Handle : (IntPtr)node.Tag;
            List<ItemAddress> items = driver.Browse(handle);
            foreach (ItemAddress item in items)
            {
                TreeNode child = new TreeNode(item.Name + "  [" + item.Datatype + "]");
                child.Tag = item;
                int childCount;
                if (item.GetChildCount(out childCount) == 0 && childCount > 0)
                    AddDummy(child);
                node.Nodes.Add(child);
            }
        }

        private void treeView1_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode node = e.Node;
            if (node.Tag is ItemAddress && node.Nodes.Count == 1 && node.Nodes[0].Tag == null)
            {
                try
                {
                    FillChildren(node);
                    if (node.Nodes.Count == 0) e.Cancel = true;
                }
                catch (Exception ex)
                {
                    Log("展开节点失败: " + ex.Message);
                    e.Cancel = true;
                }
            }
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (!(e.Node.Tag is ItemAddress item)) return;
            string symbol;
            item.GetSymbol(out symbol);
            tbSymbol.Text = symbol;
            tbDatatype.Text = item.Datatype.ToString();
            if (item.ExternalAccess == ABExternalAccess.ReadOnly)
                Log("提示: " + symbol + " 为只读 (ExternalAccess=ReadOnly), 写入将失败");
        }

        // ---------------- 单点读写 ----------------

        private AllenBradleyVarType RequireScalarType()
        {
            if (tbSymbol.Text.Trim().Length == 0) throw new ArgumentException("请先在树中选中一个叶子变量");
            AllenBradleyVarType type;
            if (!Enum.TryParse(tbDatatype.Text.Trim(), out type))
                throw new ArgumentException("未知数据类型: " + tbDatatype.Text);
            if (CompositeTypes.Contains(type))
                throw new ArgumentException("复合类型节点 (" + type + ") 不支持单点读写, 请展开后选择叶子变量");
            return type;
        }

        private void btnRead_Click(object sender, EventArgs e)
        {
            try
            {
                if (!driver.IsConnected) { MessageBox.Show("请先连接 PLC"); return; }
                AllenBradleyVarType type = RequireScalarType();
                string symbol = tbSymbol.Text.Trim();
                List<string> names = new List<string> { symbol };
                List<PValue> values;
                Stopwatch sw = Stopwatch.StartNew();
                ushort status = driver.Read(names, out values);
                sw.Stop();
                if (status != 0) { Log("读失败 " + symbol + "  st=" + status); MessageBox.Show("读失败, 状态码: " + status); return; }
                if (values == null || values.Count == 0) { Log("读 " + symbol + " 无返回值"); return; }
                tbValue.Text = values[0].ToStringWithType();
                Log("读 " + symbol + "  [" + type + "] = " + tbValue.Text + "  (" + sw.ElapsedMilliseconds + " ms)");
            }
            catch (Exception ex)
            {
                Log("读异常: " + ex.Message);
                MessageBox.Show("读异常: " + ex.Message);
            }
        }

        private void btnWrite_Click(object sender, EventArgs e)
        {
            DoWrite(false);
        }

        private void btnWriteText_Click(object sender, EventArgs e)
        {
            DoWrite(true);
        }

        /// <summary>按 AllenBradleyVarType 名解析文本值构造 PValue (该驱动无 PValue.Create 工厂)。</summary>
        private static PValue CreatePValue(AllenBradleyVarType type, string text)
        {
            switch (type)
            {
                case AllenBradleyVarType.BOOL:
                    return new ValueBool(ParseBool(text));
                case AllenBradleyVarType.SINT: return new ValueSInt(Convert.ToSByte(text));
                case AllenBradleyVarType.INT: return new ValueInt(Convert.ToInt16(text));
                case AllenBradleyVarType.DINT: return new ValueDInt(Convert.ToInt32(text));
                case AllenBradleyVarType.LINT: return new ValueLInt(Convert.ToInt64(text));
                case AllenBradleyVarType.USINT: return new ValueUSInt(Convert.ToByte(text));
                case AllenBradleyVarType.UINT: return new ValueUInt(Convert.ToUInt16(text));
                case AllenBradleyVarType.UDINT: return new ValueUDInt(Convert.ToUInt32(text));
                case AllenBradleyVarType.ULINT: return new ValueULInt(Convert.ToUInt64(text));
                case AllenBradleyVarType.REAL: return new ValueReal(Convert.ToSingle(text));
                case AllenBradleyVarType.LREAL: return new ValueLReal(Convert.ToDouble(text));
                case AllenBradleyVarType.STRING: return new ValueString(text);
                case AllenBradleyVarType.BYTE: return new ValueByte(Convert.ToByte(text));
                case AllenBradleyVarType.WORD: return new ValueWord(Convert.ToUInt16(text));
                case AllenBradleyVarType.DWORD: return new ValueDWord(Convert.ToUInt32(text));
                case AllenBradleyVarType.LWORD: return new ValueLWord(Convert.ToUInt64(text));
                default:
                    throw new ArgumentException("暂不支持写入类型: " + type);
            }
        }

        private static bool ParseBool(string text)
        {
            string t = text.Trim();
            return t == "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase)
                || t.Equals("on", StringComparison.OrdinalIgnoreCase) || t.Equals("真", StringComparison.Ordinal);
        }

        private void DoWrite(bool asText)
        {
            try
            {
                if (!driver.IsConnected) { MessageBox.Show("请先连接 PLC"); return; }
                AllenBradleyVarType type = RequireScalarType();
                string symbol = tbSymbol.Text.Trim();
                string text = tbValue.Text;
                PValue pv = asText && type == AllenBradleyVarType.STRING
                    ? new ValueString(text)   // 字符串原样写入, 不做数值转换
                    : CreatePValue(type, text.Trim());
                Stopwatch sw = Stopwatch.StartNew();
                ushort status = driver.Write(symbol, pv);
                sw.Stop();
                if (status != 0) { Log("写失败 " + symbol + "  st=" + status); MessageBox.Show("写失败, 状态码: " + status); return; }
                Log("写 " + symbol + "  [" + type + "] = " + text.Trim() + "  (" + sw.ElapsedMilliseconds + " ms)");
            }
            catch (Exception ex)
            {
                Log("写异常: " + ex.Message);
                MessageBox.Show("写异常: " + ex.Message);
            }
        }

        // ---------------- 批量读 ----------------

        /// <summary>递归收集选中节点下最多 maxCount 个叶子变量的符号路径。</summary>
        private void CollectLeaves(TreeNode node, List<string> leaves, int maxCount)
        {
            if (leaves.Count >= maxCount) return;
            if (node.Tag is ItemAddress item && !CompositeTypes.Contains(item.Datatype))
            {
                string symbol;
                item.GetSymbol(out symbol);
                leaves.Add(symbol);
                return;
            }
            if (node.Tag is ItemAddress && node.Nodes.Count == 1 && node.Nodes[0].Tag == null)
                FillChildren(node); // 未展开的复合节点先展开一层
            foreach (TreeNode child in node.Nodes)
            {
                if (leaves.Count >= maxCount) return;
                CollectLeaves(child, leaves, maxCount);
            }
        }

        private void btnBatchRead_Click(object sender, EventArgs e)
        {
            try
            {
                if (!driver.IsConnected) { MessageBox.Show("请先连接 PLC"); return; }
                if (_treeRoot == IntPtr.Zero) { MessageBox.Show("请先加载工程或在线取树"); return; }
                TreeNode start = treeView1.SelectedNode ?? treeView1.Nodes[0];
                List<string> leaves = new List<string>();
                CollectLeaves(start, leaves, 100);
                if (leaves.Count == 0) { MessageBox.Show("选中节点下没有叶子变量"); return; }

                List<PValue> values;
                Stopwatch sw = Stopwatch.StartNew();
                ushort status = driver.Read(leaves, out values);
                sw.Stop();
                if (status != 0)
                {
                    Log("批量读失败 st=" + status + " (共 " + leaves.Count + " 个)");
                    MessageBox.Show("批量读失败, 状态码: " + status);
                    return;
                }
                Log("批量读 " + leaves.Count + " 个叶子, 耗时 " + sw.ElapsedMilliseconds + " ms:");
                int show = Math.Min(values == null ? 0 : values.Count, 10);
                for (int i = 0; i < show; i++)
                    Log("  " + leaves[i] + " = " + values[i].ToStringWithType());
                if (values != null && values.Count > show)
                    Log("  … 其余 " + (values.Count - show) + " 个略");
                SetStatus("批量读完成: " + leaves.Count + " 个 / " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                Log("批量读异常: " + ex.Message);
                MessageBox.Show("批量读异常: " + ex.Message);
            }
        }

        // ---------------- 读写冒烟 ----------------

        private void btnSmoke_Click(object sender, EventArgs e)
        {
            if (!driver.IsConnected) { MessageBox.Show("请先连接 PLC"); return; }
            if (_treeRoot == IntPtr.Zero) { MessageBox.Show("请先加载工程或在线取树"); return; }
            Task.Run((Action)RunSmoke);
        }

        private void RunSmoke()
        {
            try
            {
                Log("=== 读写冒烟开始 ===");
                List<ItemAddress> leaves = driver.LoadAllSymbolicInfo();
                if (leaves == null || leaves.Count == 0) { Log("冒烟中止: 变量树中没有叶子变量"); return; }
                Log("叶子变量总数: " + leaves.Count);

                // 1) 均匀抽样最多 30 个叶子, 批量读两遍并比对
                int sample = Math.Min(30, leaves.Count);
                List<string> names = new List<string>();
                for (int i = 0; i < sample; i++)
                {
                    ItemAddress item = leaves[(int)((long)i * leaves.Count / sample)];
                    string symbol;
                    item.GetSymbol(out symbol);
                    names.Add(symbol);
                }
                List<PValue> v1, v2;
                Stopwatch sw = Stopwatch.StartNew();
                ushort st1 = driver.Read(names, out v1);
                long t1 = sw.ElapsedMilliseconds;
                sw.Restart();
                ushort st2 = driver.Read(names, out v2);
                long t2 = sw.ElapsedMilliseconds;
                if (st1 != 0 || st2 != 0) { Log("批量读失败 st1=" + st1 + " st2=" + st2); return; }
                int mismatch = 0;
                for (int i = 0; i < names.Count; i++)
                {
                    string a = v1[i] == null ? null : v1[i].ToStringWithType();
                    string b = v2[i] == null ? null : v2[i].ToStringWithType();
                    if (a != b) mismatch++;
                }
                Log("批量读×2比对: 抽样 " + names.Count + " 个, 第1遍 " + t1 + " ms, 第2遍 " + t2 + " ms, 不一致 " + mismatch + " 个");

                // 2) 至多 3 个可写 (ReadWrite) 的 BOOL 叶子: 读原值 → 取反写 → 读回验证 → 恢复原值
                int boolTests = 0, boolFail = 0;
                foreach (ItemAddress item in leaves)
                {
                    if (boolTests >= 3) break;
                    if (item.Datatype != AllenBradleyVarType.BOOL) continue;
                    if (item.ExternalAccess != ABExternalAccess.ReadWrite) continue;
                    string name;
                    item.GetSymbol(out name);
                    try
                    {
                        List<string> one = new List<string> { name };
                        List<PValue> rb;
                        if (driver.Read(one, out rb) != 0 || rb == null || rb.Count == 0) { boolFail++; boolTests++; continue; }
                        bool original = ((ValueBool)rb[0]).GetValue();
                        if (driver.Write(name, new ValueBool(!original)) != 0) { boolFail++; boolTests++; continue; }
                        driver.Read(one, out rb);
                        bool flipped = rb != null && rb.Count > 0 && ((ValueBool)rb[0]).GetValue() == !original;
                        driver.Write(name, new ValueBool(original));
                        driver.Read(one, out rb);
                        bool restored = rb != null && rb.Count > 0 && ((ValueBool)rb[0]).GetValue() == original;
                        Log("  BOOL 写往返 " + name + ": 原值=" + original + " 取反生效=" + flipped + " 已恢复=" + restored);
                        if (!flipped || !restored) boolFail++;
                        boolTests++;
                    }
                    catch (Exception ex)
                    {
                        Log("  BOOL 写往返 " + name + " 异常: " + ex.Message);
                        boolFail++; boolTests++;
                    }
                }
                Log("BOOL 写往返: " + boolTests + " 项, 失败 " + boolFail + " 项");
                Log("=== 读写冒烟结束 ===");
                SetStatus("冒烟完成: 比对不一致 " + mismatch + " / BOOL 失败 " + boolFail);
            }
            catch (Exception ex)
            {
                Log("冒烟异常: " + ex.Message);
                SetStatus("冒烟异常");
            }
        }

        // ---------------- 关闭 ----------------

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            try { driver.Disconnect(); } catch { }
        }
    }
}
