using AB_EIP_CONTROL_LOGIX_SERIES;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace TestAllenBradleyEipDriver.Console
{
    /// <summary>
    /// AllenBradleyEipDriver (罗克韦尔 ControlLogix/CompactLogix/SoftLogix
    /// EtherNet/IP) 控制台测试 demo — 统一命令结构 (同步自
    /// AllenBradleyEipDriverDemo_Optimize，以当前核心库原生 API 实现):
    ///   (无参)                                打印用法
    ///   smoke &lt;host&gt; [port] [acd] [slot]    统一冒烟: 连接 → 取树 (带 acd:
    ///                                           离线树+在线树全量对拍) → 抽样读 →
    ///                                           批量读 → 写往返，退出码 0=通过
    ///   diag &lt;acd&gt; [csvOut]                 原生诊断: 离线工程导入 + 全叶清单 CSV
    ///                                           + 原生全展开统计
    /// </summary>
    class Program
    {
        static AllenBradleyEipDriver driver = new AllenBradleyEipDriver();

        static int Main(string[] args)
        {
            // ILogger 注入演示：把核心库生命周期日志（连接/断开/大包协商/重连/取树）打到控制台。
            // 不赋值时核心库内部使用 NullLogger.Instance，不产生任何输出。
            driver.Logger = new ConsoleLogger();

            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            if (args[0] == "smoke")
            {
                string host = args.Length > 1 ? args[1] : "192.168.0.110";
                int port = args.Length > 2 ? ParseInt(args[2]) : 0;
                string acdPath = args.Length > 3 ? args[3] : null;
                byte slot = args.Length > 4 ? (byte)Math.Min(255, Math.Max(0, ParseInt(args[4]))) : (byte)1;
                return RunSmoke(host, port, acdPath, slot);
            }

            if (args[0] == "diag")
            {
                string acdPath = args.Length > 1 ? args[1] : null;
                string csvOut = args.Length > 2 ? args[2] : null;
                if (acdPath == null)
                {
                    global::System.Console.WriteLine("diag 模式需要 .acd 工程归档路径:");
                    global::System.Console.WriteLine("  TestAllenBradleyEipDriver.Console.exe diag <acdPath> [csvOut]");
                    return 2;
                }
                return RunDiag(acdPath, csvOut);
            }

            PrintUsage();
            return 2;
        }

        static void PrintUsage()
        {
            global::System.Console.WriteLine("AllenBradleyEipDriver (罗克韦尔 EtherNet/IP) 驱动测试 demo");
            global::System.Console.WriteLine("用法:");
            global::System.Console.WriteLine("  (无参)                          打印本用法");
            global::System.Console.WriteLine("  smoke <host> [port] [acd] [slot]");
            global::System.Console.WriteLine("                                  统一冒烟 (需控制器在线, 如本地 SoftLogix)");
            global::System.Console.WriteLine("                                  带 acd: 离线树+在线树全量对拍");
            global::System.Console.WriteLine("                                  不带: 在线树冒烟; slot 默认 1");
            global::System.Console.WriteLine("  diag <acd> [csvOut]             原生诊断: 离线工程导入 + 全叶清单CSV + 全展开统计");
        }

        static int ParseInt(string text)
        {
            int v;
            return int.TryParse(text, out v) ? v : 0;
        }

        /// <summary>取当前树的全部符号路径（LoadAllSymbolicInfo + GetSymbol）。</summary>
        static List<string> GetAllSymbols()
        {
            List<string> symbols = new List<string>();
            foreach (ItemAddress item in driver.LoadAllSymbolicInfo())
            {
                string symbol;
                if (item.GetSymbol(out symbol) == 0)
                    symbols.Add(symbol);
            }
            return symbols;
        }

        /// <summary>过滤出可符号读的路径：排除 __ 系统标签与 ZZZZZZZZZZ BOOL 宿主；
        /// 含冒号的模块标签（Local:3:C...）不写读但程序域 Program: 标签是普通
        /// 控制器标签（与契约版口径一致），放行参与抽样。</summary>
        static bool IsReadable(string symbol)
        {
            if (symbol.StartsWith("__") || symbol.Contains("ZZZZZZZZZZ")) return false;
            if (symbol.Contains(":") && !symbol.StartsWith("Program:")) return false;
            return true;
        }

        /// <summary>统一冒烟: 连接 → 取树 (带 acd 时离线树 + 在线树全量对拍) → 抽样读 →
        /// 批量读 → 写往返；退出码 0=全部通过, 1=失败。</summary>
        static int RunSmoke(string host, int port, string acdPath, byte slot)
        {
            global::System.Console.WriteLine(string.Format(
                "==== 冒烟 (host={0}, port={1}, acd={2}, slot={3}) ====",
                host, port > 0 ? port.ToString() : "(默认44818)", acdPath ?? "(在线树)", slot));
            bool overall = true;

            // 1) 取树：带 acd 先离线加载（同时打印控制器信息）
            List<string> offlineSymbols = null;
            if (acdPath != null)
            {
                try
                {
                    driver.LoadSymbolsFromFile(acdPath);
                }
                catch (Exception ex)
                {
                    global::System.Console.WriteLine("离线树加载失败: " + ex.Message);
                    return 1;
                }
                ACDControllerInfo info = driver.OfflineControllerInfo;
                if (info != null)
                {
                    global::System.Console.WriteLine("离线控制器信息:");
                    global::System.Console.WriteLine("  " + info);
                    global::System.Console.WriteLine("  SN:" + (info.SerialNumber ?? "-") + " 软件:" + (info.SoftwareVersion ?? "-"));
                }
                offlineSymbols = GetAllSymbols();
                global::System.Console.WriteLine("离线符号总数: " + offlineSymbols.Count);
            }

            // 2) 连接
            uint st = driver.Connect(host, slot, port > 0 ? port : 44818);
            if (st != 0)
            {
                global::System.Console.WriteLine(string.Format("Connect 失败 0x{0:X} (在线步骤需控制器在线)", st));
                if (acdPath != null)
                    global::System.Console.WriteLine("(离线树构建已通过)");
                return 1;
            }

            try
            {
                // 3) 在线树
                driver.LoadSymbolsFromPLC();
                List<string> onlineSymbols = GetAllSymbols();
                global::System.Console.WriteLine("在线符号总数: " + onlineSymbols.Count);

                // 4) 离线/在线全量对拍（仅带 acd 时）
                if (offlineSymbols != null)
                {
                    var offlineSet = new HashSet<string>(offlineSymbols);
                    var onlineSet = new HashSet<string>(onlineSymbols);
                    var missingOnline = offlineSet.Where(s => !onlineSet.Contains(s)).ToList();
                    var missingOffline = onlineSet.Where(s => !offlineSet.Contains(s)).ToList();
                    int diff = missingOnline.Count + missingOffline.Count;
                    if (diff == 0)
                    {
                        global::System.Console.WriteLine("离线/在线全量对拍: 一致 (" + offlineSet.Count + " 个符号)");
                    }
                    else
                    {
                        overall = false;
                        global::System.Console.WriteLine(string.Format(
                            "离线/在线全量对拍: 差异 {0} (离线缺 {1}, 在线缺 {2})",
                            diff, missingOffline.Count, missingOnline.Count));
                        foreach (string s in missingOnline.Take(10))
                            global::System.Console.WriteLine("  在线缺少: " + s);
                        foreach (string s in missingOffline.Take(10))
                            global::System.Console.WriteLine("  离线缺少: " + s);
                    }
                }

                // 5) 抽样读（50 个可符号读叶子）
                List<string> readable = onlineSymbols.Where(IsReadable).ToList();
                List<string> sample = readable.Take(50).ToList();
                int ok = 0, fail = 0;
                foreach (string sym in sample)
                {
                    try
                    {
                        List<PValue> v = new List<PValue>();
                        if (driver.Read(new List<string> { sym }, out v) == 0) ok++;
                        else { fail++; global::System.Console.WriteLine("  读失败: " + sym); }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        global::System.Console.WriteLine(string.Format("  异常: {0} -> {1}", sym, ex.Message));
                    }
                }
                global::System.Console.WriteLine(string.Format("抽样读: {0}/{1} 成功", ok, sample.Count));
                if (fail > 0) overall = false;

                // 6) 批量读（前 100 个可符号读叶子）
                List<string> batch = readable.Take(100).ToList();
                if (batch.Count > 0)
                {
                    List<PValue> batchValues = new List<PValue>();
                    Stopwatch sw = Stopwatch.StartNew();
                    ushort batchStatus = driver.Read(batch, out batchValues);
                    sw.Stop();
                    bool batchOk = batchStatus == 0 && batchValues.Count == batch.Count;
                    global::System.Console.WriteLine(string.Format("批量读 {0} 标签: {1} (状态 0x{2:X4}, 读回 {3}, {4} ms)",
                        batch.Count, batchOk ? "成功" : "失败", batchStatus, batchValues.Count, sw.ElapsedMilliseconds));
                    if (!batchOk) overall = false;
                }

                // 7) 写往返（INT 成员 ut1.userint1：写 → 读回校验 → 恢复）
                if (readable.Contains("ut1.userint1"))
                {
                    try
                    {
                        List<PValue> orig = new List<PValue>();
                        driver.Read(new List<string> { "ut1.userint1" }, out orig);
                        int? origVal = orig.Count > 0 ? (int?)Convert.ToInt32(orig[0].ToStringWithType()) : null;
                        ushort wst = driver.Write("ut1.userint1", new ValueInt(12345));
                        List<PValue> back = new List<PValue>();
                        ushort rst = driver.Read(new List<string> { "ut1.userint1" }, out back);
                        bool writeOk = wst == 0 && rst == 0 && back.Count > 0 && Convert.ToInt32(back[0].ToStringWithType()) == 12345;
                        if (origVal.HasValue) driver.Write("ut1.userint1", new ValueInt((short)origVal.Value));
                        global::System.Console.WriteLine("写往返 ut1.userint1: " + (writeOk ? "成功 (已恢复原值)" : "失败"));
                        if (!writeOk) overall = false;
                    }
                    catch (Exception ex)
                    {
                        overall = false;
                        global::System.Console.WriteLine("写往返异常: " + ex.Message);
                    }
                }
                else
                {
                    global::System.Console.WriteLine("写往返: 跳过 (符号表中无 ut1.userint1)");
                }
            }
            finally
            {
                driver.Disconnect();
            }

            global::System.Console.WriteLine("==== 冒烟" + (overall ? "通过 ====" : "失败 ===="));
            return overall ? 0 : 1;
        }

        /// <summary>原生诊断（同步自 Optimize 版 diag 模式，以当前核心库原生 API 实现）：
        /// 离线 .acd 导入 → 控制器信息 → LoadAllSymbolicInfo 原生全展开统计（耗时+类型分布）
        /// → 全叶清单 CSV（Path,Type,Access）。</summary>
        static int RunDiag(string acdPath, string csvOut)
        {
            System.Console.WriteLine("==== 原生诊断 (acd=" + acdPath + ") ====");
            if (!File.Exists(acdPath))
            {
                System.Console.WriteLine("工程归档不存在: " + acdPath);
                return 1;
            }

            // 1) 离线工程导入 + 控制器信息
            try
            {
                driver.LoadSymbolsFromFile(acdPath);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("LoadSymbolsFromFile 失败: " + ex.Message);
                return 1;
            }
            ACDControllerInfo info = driver.OfflineControllerInfo;
            if (info != null)
            {
                System.Console.WriteLine("离线控制器信息:");
                System.Console.WriteLine("  " + info);
                System.Console.WriteLine("  SN:" + (info.SerialNumber ?? "-") + " 软件:" + (info.SoftwareVersion ?? "-"));
            }

            // 2) 原生全展开统计
            Stopwatch sw = Stopwatch.StartNew();
            List<ItemAddress> all = driver.LoadAllSymbolicInfo();
            sw.Stop();
            if (all == null)
            {
                System.Console.WriteLine("LoadAllSymbolicInfo 返回 null.");
                return 1;
            }
            System.Console.WriteLine(string.Format(
                "LoadAllSymbolicInfo 原生全展开: 叶子 {0} 个, 耗时 {1} ms", all.Count, sw.ElapsedMilliseconds));
            var histogram = new Dictionary<AllenBradleyVarType, int>();
            foreach (ItemAddress item in all)
            {
                int c;
                histogram[item.Datatype] = histogram.TryGetValue(item.Datatype, out c) ? c + 1 : 1;
            }
            System.Console.WriteLine("类型分布: " + string.Join(", ",
                histogram.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value)));

            // 3) 全叶清单 CSV
            if (string.IsNullOrEmpty(csvOut))
                csvOut = Path.Combine(Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location), "leaf_list.csv");
            using (StreamWriter writer = new StreamWriter(csvOut, false, Encoding.UTF8))
            {
                writer.WriteLine("Path,Type,Access");
                foreach (ItemAddress item in all)
                {
                    string symbol;
                    if (item.GetSymbol(out symbol) != 0) symbol = item.Name;
                    writer.WriteLine(symbol + "," + item.Datatype + "," + item.ExternalAccess);
                }
            }
            System.Console.WriteLine(string.Format("全叶清单已导出: {0} ({1} 行)", csvOut, all.Count));
            System.Console.WriteLine("==== 原生诊断结束 ====");
            return 0;
        }
    }
}
