using MessagePack;

namespace Nanalive.Sdk;

/// <summary>协议常量，与 JS SDK 一一对应。</summary>
public static class NanaLiveApi
{
    public const string ApiName = "NanaLiveControlAPI";
    public const string ApiVersion = "2.0";
    public const string Subprotocol = "nanalive-control-v2";
    public const int DefaultPort = 8312;
}

/// <summary>鉴权时提交给 NanaLive 的插件身份。</summary>
/// <remarks>
/// <see cref="PluginId"/> 请使用自己的反向域名标识，<see cref="Scopes"/>
/// 只申请实际用到的权限；首次申请的 token 需要用户在 NanaLive 插件页批准。
/// </remarks>
public sealed record Identity(
    string PluginId,
    string PluginName,
    string PluginDeveloper,
    string PluginVersion,
    IReadOnlyList<string> Scopes)
{
    internal object? ToMessagePack()
    {
        var scopes = new object?[Scopes.Count];
        for (var i = 0; i < Scopes.Count; i++)
        {
            scopes[i] = Mp.Str(Scopes[i]);
        }

        return Mp.Map(
            ("pluginID", Mp.Str(PluginId)),
            ("pluginName", Mp.Str(PluginName)),
            ("pluginDeveloper", Mp.Str(PluginDeveloper)),
            ("pluginVersion", Mp.Str(PluginVersion)),
            ("scopes", Mp.Array(scopes)));
    }
}

/// <summary>
/// schemaless MessagePack 值的构造与读取助手。
///
/// 协议数据统一用 <see cref="object"/> 树表示：对象是
/// <see cref="Dictionary{TKey,TValue}"/>（键为字符串）、数组是
/// <see cref="Array"/>、标量是 string/double/bool 等，与
/// <c>MessagePack.Resolvers.PrimitiveObjectResolver</c> 的编解码结果一致。
/// </summary>
public static class Mp
{
    private static readonly MessagePackSerializerOptions PrimitiveOptions =
        new(MessagePack.Resolvers.PrimitiveObjectResolver.Instance);

    public static object? Nil => null;

    public static object Str(string value) => value;

    public static object Num(double value) => value;

    public static object Bool(bool value) => value;

    public static object Map(params (string Key, object? Value)[] entries)
    {
        var map = new Dictionary<object, object?>(entries.Length);
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return map;
    }

    public static object Array(params object?[] items) => items;

    public static byte[] Serialize(object value) =>
        MessagePackSerializer.Serialize(value, PrimitiveOptions);

    public static object? Deserialize(byte[] payload) =>
        MessagePackSerializer.Deserialize<object>(payload, PrimitiveOptions);

    /// <summary>按字符串键读取 map 字段；非 map 或键不存在返回 null。</summary>
    public static object? GetField(this object? value, string key)
    {
        if (value is not IDictionary<object, object?> map)
        {
            return null;
        }

        return map.TryGetValue(key, out var result) ? result : null;
    }

    /// <summary>宽松读取字符串；非字符串返回 null。</summary>
    public static string? TryString(this object? value) => value as string;

    /// <summary>宽松读取数值（整数/浮点均可）；非数值返回 NaN。</summary>
    public static double TryNumber(this object? value) => value switch
    {
        double number => number,
        float number => number,
        long number => number,
        ulong number => number,
        int number => number,
        uint number => number,
        short number => number,
        ushort number => number,
        byte number => number,
        sbyte number => number,
        _ => double.NaN,
    };

    /// <summary>宽松读取数组；非数组返回 null。</summary>
    public static IList<object?>? TryList(this object? value) => value as object[];

    /// <summary>值是否为 MessagePack map。</summary>
    public static bool IsMap(this object? value) => value is IDictionary<object, object?>;

    /// <summary>值是否为 MessagePack nil。</summary>
    public static bool IsNil(this object? value) => value is null;
}
