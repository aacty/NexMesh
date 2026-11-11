namespace NexMesh.Core.AutoApi;

using System.Globalization;

/// <summary>
/// 简单类型快路径转换器(实测对比:TypeDescriptor.GetConverter().ConvertFromInvariantString
/// int 175ns/24B → 直接 Parse 18ns/0B,约 10 倍且零分配)。
/// 命中类型表走 InvariantCulture 直接 Parse;枚举等冷类型返回 false 由调用方回退 TypeDescriptor(行为不变)。
/// 数值超范围抛 OverflowException——与格式错误同为客户端输入错误(调用方 400,修复此前 500)。
/// </summary>
internal static class SimpleConverter
{
    /// <summary>尝试快路径转换;未命中类型表(枚举/奇特类型)返回 false,调用方回退 TypeDescriptor。</summary>
    public static bool TryConvert(string raw, Type underlying, out object? value)
    {
        object? parsed;
        if (underlying == typeof(int)) { parsed = int.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(long)) { parsed = long.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(string)) { parsed = raw; }
        else if (underlying == typeof(bool)) { parsed = bool.Parse(raw); }
        else if (underlying == typeof(Guid)) { parsed = Guid.Parse(raw); }
        else if (underlying == typeof(double)) { parsed = double.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(decimal)) { parsed = decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(DateTime)) { parsed = DateTime.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(DateTimeOffset)) { parsed = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(short)) { parsed = short.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(byte)) { parsed = byte.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(float)) { parsed = float.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(uint)) { parsed = uint.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(ulong)) { parsed = ulong.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(sbyte)) { parsed = sbyte.Parse(raw, CultureInfo.InvariantCulture); }
        else if (underlying == typeof(ushort)) { parsed = ushort.Parse(raw, CultureInfo.InvariantCulture); }
        else
        {
            value = null;
            return false; // 枚举等冷类型 → TypeDescriptor 回退
        }
        value = parsed;
        return true;
    }
}
