# AllenBradleyEipDriver

EtherNet/IP communication driver for Rockwell Allen-Bradley ControlLogix / CompactLogix / SoftLogix PLCs, implemented in pure C# with no external dependencies.

- Target framework: .NET Framework 4.0 (C# 7.3, buildable with VS2017+)
- Namespace: `AB_EIP_CONTROL_LOGIX_SERIES`
- Verified environment: SoftLogix5800 v21.11 (1789-L60/A), RSLogix 5000 v21 project

## Features

| Category | Details |
|---|---|
| Connection | TCP 44818, RegisterSession, ForwardOpen / LargeForwardOpen (automatic fallback when large-packet negotiation fails), KeepAlive |
| Reliability | Automatic reconnection on link loss (exponential backoff, configurable retries/intervals), connection sequence-number validation, stale-response cleanup, immediate detection of remote close |
| Read | Single tag, batch read (multi-service packing & splitting), array batch read `Read(tag, count)`, bit addressing (`Tag.0` / `Tag.DN`), whole-struct read, struct arrays |
| Write | Single tag, batch write, bit write (Read-Modify-Write 0x4E), whole-struct write, struct array element write |
| Tag tree | Online browse (lazy expansion), offline parsing of `.acd` project archives; both online and offline tree nodes carry the External Access flag |
| Device info | Friendly names for vendor / device type / product family / catalog number (CIP code tables); `ShortDescription` prints an RSLinx-style description |

## Quick Start

```csharp
using AB_EIP_CONTROL_LOGIX_SERIES;
using Microsoft.Extensions.Logging;

var driver = new AllenBradleyEipDriver();

// Optional: enable automatic reconnection (disabled by default)
driver.Reconnect.Enabled = true;
driver.OnConnectionLost   += () => Log("Connection lost, reconnecting...");
driver.OnReconnected      += () => Log("Reconnected");

// Optional: inject logging (Microsoft.Extensions.Logging.ILogger;
// defaults to NullLogger.Instance — no output when not assigned).
// Minimal implementation shown at the bottom of this snippet.
driver.Logger = new ConsoleLogger();

// Connect (slot defaults to 1, i.e. routing path 01 01)
uint status = driver.Connect("192.168.0.110");
if (status != 0) { /* failed */ }

// Device info
DeviceInfo info = driver.ControllerInfo;
Console.WriteLine(info.ShortDescription);   // 1789-L60/A / SoftLogix5800 v21.11

// Read
ushort st = driver.Read(new List<string> { "BOOL1", "ut1.userint1" }, out List<PValue> values);

// Write
st = driver.Write("ut1.userint1", new ValueInt(123));

// Tag tree (online)
IntPtr root = driver.LoadSymbolsFromPLC();
foreach (ItemAddress item in driver.Browse(root)) { /* expand recursively */ }
// Or fetch all symbols at once
List<ItemAddress> all = driver.LoadAllSymbolicInfo();

// Tag tree (offline .acd, no PLC connection required)
IntPtr root2 = driver.LoadSymbolsFromFile(@"D:\proj\tsoft.ACD");
ACDControllerInfo off = driver.OfflineControllerInfo;

driver.Disconnect();
```

Logged events (level): connection attempt / connected with device description / LargeForwardOpen negotiation & fallback / disconnect / online & offline tag tree loaded (Information); TCP or ForwardOpen failure / connection lost (Warning); reconnect attempts, success, and giving up after `Reconnect.MaxRetries` (Information / Information / Error). Reconnect logs are raised on a background thread — UI consumers must Invoke themselves.

```csharp
// Minimal ILogger implementation (dependency: Lib\Net4x.Extensions.Logging.Abstractions.dll):
sealed class ConsoleLogger : ILogger
{
    public IDisposable BeginScope<TState>(TState state) { return null; }
    public bool IsEnabled(LogLevel logLevel) { return true; }
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception exception, Func<TState, Exception, string> formatter)
    {
        Console.WriteLine("[{0}] {1}", logLevel, formatter(state, exception));
    }
}
```

## API Reference

### AllenBradleyEipDriver

| Member | Description |
|---|---|
| `uint Connect(string ip, byte slot = 1, int port = 44818)` | Establish TCP + CIP connection; non-zero means failure |
| `void Disconnect()` | Disconnect and release resources |
| `bool IsConnected` | Connection state |
| `DeviceInfo ControllerInfo` | Online device info (available after Connect) |
| `ACDControllerInfo OfflineControllerInfo` | Offline `.acd` project info (available after offline load) |
| `IntPtr LoadSymbolsFromPLC()` | Load the tag tree online; returns the ROOT handle |
| `IntPtr LoadSymbolsFromFile(string path)` | Load the tag tree from an `.acd` archive (`.acd` only) |
| `List<ItemAddress> Browse(IntPtr handle)` | Expand one level of the tree (lazy loading) |
| `List<ItemAddress> LoadAllSymbolicInfo()` | Fully expand and return all symbols |
| `ushort Read(List<string> tags, out List<PValue> values)` | Batch read |
| `ushort Read(string tag, int count, out List<PValue> values)` | Array / struct-array batch read |
| `ushort Write(string tag, PValue value)` | Single write (pass `ValueBool` for bit writes, with a `.bit` suffix on the tag) |
| `ushort Write(List<string> tags, List<PValue> values)` | Batch write |
| `ReconnectOptions Reconnect` | Reconnect config: `Enabled` / `MaxRetries` (0 = unlimited) / `InitialDelayMs` / `MaxDelayMs` / `Multiplier` |
| Events `OnConnectionLost` / `OnReconnecting(int)` / `OnReconnected` | Reconnect progress notifications (raised on a background thread; UI must Invoke) |

### ItemAddress (tree node / symbol)

| Member | Description |
|---|---|
| `string Name` / `IntPtr Handle` / `AllenBradleyVarType Datatype` | Node name / browse handle / data type |
| `ushort GetSymbol(out string path)` | Get the symbol path (e.g. `Program:MainProg1.local_bool1`, `ut11[0,0].userint1`) |
| `ushort GetChildCount(out int count)` | Child-node count (> 0 means Browse can continue) |
| `ABExternalAccess ExternalAccess` | External access: `ReadWrite` / `ReadOnly` / `None` / `Unknown` (members inherit from the owning tag) |

### PValue value types

`ValueBool/ValueSInt/ValueInt/ValueDInt/ValueLInt/ValueUSInt/ValueUInt/ValueUDInt/ValueULInt/ValueReal/ValueLReal/ValueString/ValueStruct/...` — each type provides `SetValue/GetValue` (including array-index overloads). Struct values are `ValueStruct`: `GetTypeName()` + `GetValues()` (dictionary of member names; nested structs/arrays resolved recursively).

## Symbol Path Syntax

- Controller-scoped tags: `Tag`, `Tag[2]`, `Tag[0,1]`, `Struct.member`, `Struct[0,0].member`
- Program scope: `Program:MainProg1.local_bool1`
- Module tags: `Local:3:C.Data[0]` (whole reads are limited by the PLC; reading per member is recommended)
- Bit addressing: `BOOL1.0` (numeric bit), `timer1.DN` (named bit); bit writes internally use Read-Modify-Write
- STRING is expanded as struct members `s1.LEN` / `s1.DATA[n]`, and can also be read/written as a whole

## Notes & Known Limitations

1. **Struct template resolution for whole-struct reads/writes**: the 2-byte struct handle in responses is the template instance number for predefined types (TIMER etc.), but the template CRC for user-defined UDTs. The driver resolves it in three steps: ① cached-handle match → ② direct instance lookup → ③ symbol-tree reverse lookup. **A cold whole-struct access on a user UDT (with the tag tree never loaded) throws an explicit error** — just call `LoadSymbolsFromPLC()` / `LoadAllSymbolicInfo()` first.
2. Tags with External Access = `None` are hidden entirely by Rockwell — neither online browse nor the offline tree lists them (consistent with RSLogix behavior).
3. Batch reads are automatically grouped by the negotiated packet size; Partial Transfer (0x06) automatically degrades to a single-tag whole read.
4. When writing a BOOL tag, the driver writes a whole byte (PLC BOOL storage semantics); use the `.bit` suffix for bit operations.
5. Thread safety: requests on a single connection are serialized internally (send lock); concurrent reads from multiple threads sharing one driver instance will be serialized — for throughput, prefer batch reads over multithreaded single reads.
6. Reconnect events are raised on a background thread; WinForms/WPF subscribers must Invoke back to the UI thread themselves.

## Source Layout

| File | Responsibility |
|---|---|
| `AllenBradleyEipDriver.cs` | Driver main class: connection management, read/write/browse/reconnect, struct template resolution |
| `TcpClient.cs` | TCP transport layer: async receive, PDU assembly/parsing, BlockingCollection queues, sequence-number validation |
| `Encapsulation.cs` | EIP encapsulation message build/parse (RegisterSession/RRData/UnitData/ForwardOpen...) |
| `EIPStructures.cs` | CIP/EIP structures and enums (including `ABExternalAccess`, error-code tables) |
| `StructCodec.cs` | Struct template codec (member offsets / BOOL host bits / nesting / STRING members) |
| `AllenBradleyACDFile.cs` | `.acd` project archive parsing (TagInfo.XML tag tree + Comps.Dat permissions/controller info) |
| `PValue.cs` | Value type system (scalars/arrays/STRING/structs) |
| `VarTreeStructures.cs` / `ItemAddress.cs` / `DeviceInfo.cs` | Tag-tree nodes, symbol addresses, device info |

## References & Acknowledgements

- Struct read/write, batch read/write, LargeForwardOpen, etc. ported from [node-ethernet-ip](https://github.com/BrianDAdams/node-ethernet-ip)
- `.acd` parsing based on the reverse-engineering work of [hutcheb/acd](https://github.com/hutcheb/acd)
- L5X parsing previously referenced [L5Sharp](https://github.com/tjkalkes/L5Sharp) (that parser has been removed; `.acd` only is supported now)

## Changelog

See [更新履历.txt](更新履历.txt) in the same directory.

## Test Tools

The workspace-level `..\..` `tools\` directory provides regression test tools (DumpTree tree diff, TestPerf performance benchmark, TestReconnect reconnect test, TestStruct* struct read/write verification); see `tools\README.txt` for usage.
**Integrators should start from the minimal example in `tools\QuickStart\`** (single-file Program.cs + project reference, matching the Quick Start above).
