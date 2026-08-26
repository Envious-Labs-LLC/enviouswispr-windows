using EnviousWispr.Core.Errors;
using EnviousWispr.Core.Input;
using EnviousWispr.Services.Input;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

const byte F24 = 0x87;
const byte Escape = 0x1B;
const int ConflictRegistrationId = 0x6204;
const uint ControlAltShift = 0x0001 | 0x0002 | 0x0004;

var target = new WindowsForegroundTargetProvider().CaptureForegroundTarget();
var conflictRegistered = NativeMethods.RegisterHotKey(
    0,
    ConflictRegistrationId,
    ControlAltShift,
    0x86);
var conflictDetected = false;
if (conflictRegistered)
{
    conflictDetected = !WindowsPushToTalkHook.TryCreate(
        "Ctrl+Alt+Shift+F23",
        out var conflictingHook,
        out var conflictError) &&
        conflictingHook is null &&
        conflictError?.Code == AppErrorCode.HotkeyConflict;
    NativeMethods.UnregisterHotKey(0, ConflictRegistrationId);
}

if (!WindowsPushToTalkHook.TryCreate("F24", out var hook, out var installationError) || hook is null)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        inputKind = "SyntheticSendInput",
        installed = false,
        installationError = installationError?.Code.ToString(),
        targetFrozen = target?.IsValid == true,
        conflictDetected,
    }));
    return 2;
}

var signals = Channel.CreateUnbounded<PushToTalkSignal>();
hook.Signalled += (_, args) => signals.Writer.TryWrite(args.Signal);

SendKey(F24, keyDown: true);
SendKey(F24, keyDown: true);
SendKey(F24, keyDown: false);
var holdReleaseSignals = await ReadSignalsAsync(signals.Reader, count: 2);

SendKey(F24, keyDown: true);
SendKey(Escape, keyDown: true);
SendKey(Escape, keyDown: false);
SendKey(F24, keyDown: false);
var cancellationSignals = await ReadSignalsAsync(signals.Reader, count: 2);

await hook.DisposeAsync();
var summary = new
{
    inputKind = "SyntheticSendInput",
    installed = true,
    targetFrozen = target?.IsValid == true,
    conflictDetected,
    pressRelease = holdReleaseSignals is
        [PushToTalkSignal.Pressed, PushToTalkSignal.Released],
    cancellation = cancellationSignals is
        [PushToTalkSignal.Pressed, PushToTalkSignal.Cancelled],
    teardownReleasedHook = !hook.IsInstalled,
};
Console.WriteLine(JsonSerializer.Serialize(summary));

return summary.targetFrozen &&
    summary.conflictDetected &&
    summary.pressRelease &&
    summary.cancellation &&
    summary.teardownReleasedHook
    ? 0
    : 4;

static async Task<IReadOnlyList<PushToTalkSignal>> ReadSignalsAsync(
    ChannelReader<PushToTalkSignal> reader,
    int count)
{
    var result = new List<PushToTalkSignal>(count);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while (result.Count < count)
    {
        result.Add(await reader.ReadAsync(timeout.Token));
    }

    return result;
}

static void SendKey(byte virtualKey, bool keyDown)
{
    var input = new Input
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyDown ? 0u : 0x0002u,
            },
        },
    };
    if (NativeMethods.SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
    {
        throw new InvalidOperationException("Synthetic keyboard input was rejected.");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Input
{
    public uint Type;
    public InputUnion Data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)]
    public KeyboardInput Keyboard;

    [FieldOffset(0)]
    public MouseInput Mouse;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    public int DeltaX;
    public int DeltaY;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);
}
