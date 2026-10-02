using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WhiteBoard.Controls;
using WhiteBoard.Models;

namespace WhiteBoard.InputTests;

/// <summary>
/// 真实指针输入的模拟器：直接构造 Avalonia 的指针事件并投递给控件，
/// 从而在无窗口环境下复现「拖动 / 擦除」等交互路径。
/// </summary>
public sealed class PointerSim : IDisposable
{
    private ulong _timestamp = 1;

    // Avalonia 自带的 Pointer 实现了 IPointer（自定义实现不被允许，接口含不可实现成员）
    private readonly Pointer _pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);

    public IInputElement? Captured => _pointer.Captured;

    public void Dispose() => _pointer.Dispose();

    /// <summary>模拟一次「按下」。</summary>
    public void Press(Control target, Point position, bool right = false, KeyModifiers modifiers = KeyModifiers.None)
    {
        var props = new PointerPointProperties(
            right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
            right ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.LeftButtonPressed);

        var args = new PointerPressedEventArgs(
            target, _pointer, target, position, _timestamp++, props, modifiers, 1);

        target.RaiseEvent(args);
    }

    /// <summary>模拟一次「移动」。<paramref name="pressed"/> 表示当前仍处于按下状态。</summary>
    public void Move(Control target, Point position, bool pressed = true, bool right = false,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var mods = pressed
            ? (right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton)
            : RawInputModifiers.None;

        var props = new PointerPointProperties(mods, PointerUpdateKind.Other);

        var args = new PointerEventArgs(
            InputElement.PointerMovedEvent, target, _pointer, target, position,
            _timestamp++, props, modifiers);

        target.RaiseEvent(args);
    }

    /// <summary>模拟一次「抬起」。</summary>
    public void Release(Control target, Point position, bool right = false,
        KeyModifiers modifiers = KeyModifiers.None)
    {
        var props = new PointerPointProperties(
            right ? RawInputModifiers.RightMouseButton : RawInputModifiers.LeftMouseButton,
            right ? PointerUpdateKind.RightButtonReleased : PointerUpdateKind.LeftButtonReleased);

        var args = new PointerReleasedEventArgs(
            target, _pointer, target, position, _timestamp++, props, modifiers,
            right ? MouseButton.Right : MouseButton.Left);

        target.RaiseEvent(args);
    }

    /// <summary>模拟一次「滚轮」。</summary>
    public void Wheel(Control target, Point position, Vector delta, KeyModifiers modifiers = KeyModifiers.None)
    {
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
        var args = new PointerWheelEventArgs(
            target, _pointer, target, position, _timestamp++, props, modifiers, delta);

        target.RaiseEvent(args);
    }
}

/// <summary>测量一段代码的耗时与分配量。</summary>
public static class Bench
{
    public static (double Ms, long AllocBytes) Measure(Action action, int iterations = 1)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
            action();

        sw.Stop();

        var after = GC.GetAllocatedBytesForCurrentThread();
        return (sw.Elapsed.TotalMilliseconds / iterations, (after - before) / iterations);
    }
}
