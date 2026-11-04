using AB_EIP_CONTROL_LOGIX_SERIES;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace TestAllenBradleyEipDriver.Console
{
    class Program
    {
        static AllenBradleyEipDriver driver = new AllenBradleyEipDriver();

        static void Main(string[] args)
        {
            // demo 模式：跑完批量读验证后断开退出，不进入无限循环（供自动运行/调试）
            bool demoMode = args != null && args.Contains("demo");
            bool acdMode = args != null && args.Contains("acd");

            if (acdMode)
            {
                // ACD 离线模式：从工程归档加载变量树（无需连接），随后连 PLC 抽验符号可读性
                string acdPath = "D:\\KimiWorkspace\\AllenBradley\\tsoft.ACD";
                int pathIdx = args.ToList().IndexOf("acd");
                if (args.Length > pathIdx + 1)
                    acdPath = args[pathIdx + 1];

                global::System.Console.WriteLine("ACD 离线加载: " + acdPath);
                driver.LoadSymbolsFromFile(acdPath);
                ACDControllerInfo info = driver.OfflineControllerInfo;
                if (info != null)
                {
                    global::System.Console.WriteLine("离线控制器信息:");
                    global::System.Console.WriteLine("  " + info);
                    global::System.Console.WriteLine(string.Format("  VendorID:{0} ProductType:{1} ProductCode:{2}", info.VendorID, info.ProductType, info.ProductCode));
                    global::System.Console.WriteLine("  SN:" + (info.SerialNumber ?? "-") + " 软件:" + (info.SoftwareVersion ?? "-"));
                    global::System.Console.WriteLine(string.Format("  创建:{0} 修改:{1}",
                        info.ProjectCreationDate.HasValue ? info.ProjectCreationDate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-",
                        info.LastModifiedDate.HasValue ? info.LastModifiedDate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-"));
                }
                List<string> offlineSymbols = new List<string>();
                List<ItemAddress> offlineItems = new List<ItemAddress>(driver.LoadAllSymbolicInfo());
                foreach (ItemAddress item in offlineItems)
                {
                    if (item.GetSymbol(out string symbol) == 0)
                        offlineSymbols.Add(symbol);
                }
                global::System.Console.WriteLine($"离线符号总数: {offlineSymbols.Count}");
                global::System.Console.WriteLine("前 20 个符号(含外部访问权限):");
                int shown = 0;
                foreach (ItemAddress item in offlineItems)
                {
                    if (shown >= 20)
                        break;
                    if (item.GetSymbol(out string s) == 0)
                    {
                        global::System.Console.WriteLine(string.Format("  {0}  [{1}]", s, item.ExternalAccess));
                        shown++;
                    }
                }

                // 关键符号存在性检查（预定义类型 STRING/TIMER 与模块类型均按在线行为展开成员，
                // 故检查其成员路径：s1→s1.LEN/s1.DATA[0]，timer1→timer1.PRE 等）
                string[] keySymbols = { "BOOL1", "ba[0]", "ut1.userint1", "ut11[0,0].userint1", "ut11[0,1].userreal1", "Program:MainProg1.local_bool1", "s1.LEN", "s1.DATA[0]", "timer1.PRE", "intA[0]" };
                global::System.Console.WriteLine("关键符号检查:");
                foreach (string k in keySymbols)
                    global::System.Console.WriteLine(string.Format("  {0}: {1}", k, offlineSymbols.Contains(k) ? "存在" : "缺失!"));

                // 连上 PLC 抽验离线符号可读
                global::System.Console.WriteLine("连接 PLC 抽验离线符号...");
                if (driver.Connect("192.168.0.110") == 0)
                {
                    int ok = 0, fail = 0;
                    foreach (string sym in offlineSymbols.GetRange(0, Math.Min(50, offlineSymbols.Count)))
                    {
                        try
                        {
                            List<string> one = new List<string> { sym };
                            List<PValue> v = new List<PValue>();
                            if (driver.Read(one, out v) == 0) ok++;
                            else { fail++; global::System.Console.WriteLine("  读失败: " + sym); }
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            global::System.Console.WriteLine(string.Format("  异常: {0} -> {1}", sym, ex.Message));
                        }
                    }
                    global::System.Console.WriteLine(string.Format("抽验完成: 成功 {0}，失败 {1}", ok, fail));
                    driver.Disconnect();
                }
                else
                {
                    global::System.Console.WriteLine("连接 PLC 失败，跳过在线抽验。");
                }
                global::System.Console.WriteLine("ACD 模式运行结束。");
                return;
            }

            uint status = driver.Connect("192.168.0.110");
            if (status == 0)
            {
                global::System.Console.WriteLine("成功连接到控制器");
                DeviceInfo information = driver.ControllerInfo;
                global::System.Console.WriteLine($"ControllerName:{information.DeviceName}|Version:{information.Version}");
                global::System.Console.WriteLine($"ProductSeries:{information.ProductSeries}|Vendor:{information.Vendor}|DeviceType:{information.DeviceTypeName}");
                global::System.Console.WriteLine($"Summary:{information.ShortDescription}");
                driver.LoadSymbolsFromPLC();
                //driver.LoadSymbolsFromFile("E:\\DesktopDoc\\tsoft-Tags.TXT");
                List<ItemAddress> items = driver.LoadAllSymbolicInfo();
                global::System.Console.WriteLine("符号外部访问权限示例(前10):");
                for (int i = 0; i < Math.Min(10, items.Count); i++)
                    global::System.Console.WriteLine(string.Format("  {0}  [{1}]", items[i].Name, items[i].ExternalAccess));
                List<string> symbols = new List<string>();
                foreach(ItemAddress item in items)
                {
                    if (item.GetSymbol(out string symbol) == 0)
                        symbols.Add(symbol);
                }
                if (!demoMode)
                    status = driver.Write("ut1.str1", new ValueString("hymson234"));
                List<string> subSymbols = new List<string>();
                subSymbols.AddRange(symbols.GetRange(0, Math.Min(1000, symbols.Count)));
                subSymbols.Add("BOOL1");
                subSymbols.Add("BOOL11");
                subSymbols.Add("BOOL20");
                subSymbols.Add("var2");
                subSymbols.Add("s1");
                subSymbols.Add("ut1.str1");
                subSymbols.Add("ut1.str1");
                // demo 模式：挑几个可符号读的用户标签做一次批量读验证，然后断开退出
                List<string> demoTags = new List<string>();
                foreach (string sym in symbols)
                {
                    // 排除 Program: 程序域前缀、含冒号的模块标签、__ 系统标签、ZZZZZZZZZZ 打包 BOOL 宿主名
                    if (sym.Contains(":") || sym.StartsWith("__") || sym.Contains("ZZZZZZZZZZ"))
                        continue;
                    demoTags.Add(sym);
                    if (demoTags.Count >= 5)
                        break;
                }
                if (demoTags.Count == 0)
                    demoTags.AddRange(new List<string> { "BOOL1", "s1", "ut1.str1" });

                if (demoMode)
                {
                    List<string> sample = demoTags;
                    try
                    {
                        List<PValue> sampleValues = new List<PValue>();
                        ushort readStatus = driver.Read(sample, out sampleValues);
                        global::System.Console.WriteLine(string.Format("批量读状态 0x{0:X4}，读回 {1}/{2} 个标签", readStatus, sampleValues.Count, sample.Count));
                        for (int i = 0; i < sampleValues.Count; i++)
                            global::System.Console.WriteLine("  " + sample[i] + " = " + sampleValues[i].ToStringWithType());
                    }
                    catch (Exception ex)
                    {
                        global::System.Console.WriteLine("批量读异常: " + ex.Message);
                    }
                    driver.Disconnect();
                    global::System.Console.WriteLine("demo 模式运行结束。");
                    return;
                }

                List<PValue> values = new List<PValue>();
                while (true)
                {
                    Stopwatch stopwatch = new Stopwatch();
                    stopwatch.Start();
                    driver.Read(subSymbols, out values);
                    stopwatch.Stop();
                    long ms = stopwatch.ElapsedMilliseconds;
                    for (int i = 0; i < values.Count; i++)
                    {
                        string symbol = subSymbols[i];
                        global::System.Console.WriteLine($"[读取{values.Count}个变量耗时{ms}ms]{symbol} = {values[i].ToString()}");
                    }
                }
            }
            else
            {
                global::System.Console.WriteLine("连接控制器失败");
            }
            global::System.Console.WriteLine("按任意键断开连接");
            global::System.Console.ReadKey();
            driver.Disconnect();
        }
    }
}
