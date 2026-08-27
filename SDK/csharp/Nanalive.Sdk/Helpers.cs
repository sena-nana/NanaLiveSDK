namespace Nanalive.Sdk;

/// <summary>与 JS SDK 的 <c>executableHotkeys</c>、<c>parameterValueAfterTicks</c>、
/// <c>writeParameterCommand</c> 对应的助手函数。</summary>
public static class Helpers
{
    /// <summary>每多少刻度走完参数全量程（与 JS SDK 一致）。</summary>
    public const double FullRangeTicks = 40.0;

    /// <summary>从按键目录中过滤出可执行的按键（<c>executable == true</c>）。</summary>
    public static List<object> ExecutableHotkeys(IEnumerable<object> hotkeys)
    {
        var result = new List<object>();
        foreach (var hotkey in hotkeys)
        {
            if (hotkey.GetField("executable") is true)
            {
                result.Add(hotkey);
            }
        }

        return result;
    }

    /// <summary>参数当前值按旋钮刻度推算后的目标值：每 40 刻度走完全量程，
    /// 并钳制在 <c>min</c>/<c>max</c> 之内。无效输入的回退行为与 JS 版一致。</summary>
    public static double ParameterValueAfterTicks(object? parameter, double ticks)
    {
        if (parameter is null)
        {
            return 0.0;
        }

        var value = parameter.GetField("value")?.TryNumber() ?? double.NaN;
        var min = parameter.GetField("min")?.TryNumber() ?? double.NaN;
        var max = parameter.GetField("max")?.TryNumber() ?? double.NaN;
        if (!double.IsFinite(ticks) || ticks == 0)
        {
            return double.IsFinite(value) ? value : 0.0;
        }

        var span = max - min;
        var step = span == 0 || !double.IsFinite(span) ? 1.0 : span / FullRangeTicks;
        var next = value + ticks * step;
        if (!double.IsFinite(next))
        {
            return value;
        }

        // 与 JS 的 Math.min(max, Math.max(min, next)) 一致。
        return Math.Min(max, Math.Max(min, next));
    }

    /// <summary>构造写入单个参数值的 <c>ParameterWriteRequest</c> 命令；
    /// <paramref name="parameterId"/> 为空或 <paramref name="value"/> 非有限时返回 null。</summary>
    public static object? WriteParameterCommand(string? parameterId, double value)
    {
        if (string.IsNullOrEmpty(parameterId) || !double.IsFinite(value))
        {
            return null;
        }

        return Mp.Map(
            ("messageType", Mp.Str("ParameterWriteRequest")),
            ("data", Mp.Map(
                ("parameters", Mp.Map((parameterId!, Mp.Num(value)))))));
    }
}
