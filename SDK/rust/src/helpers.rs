//! 与 JS SDK 的 `executableHotkeys`、`parameterValueAfterTicks`、
//! `writeParameterCommand` 对应的助手函数。

use crate::ValueExt;
use rmpv::Value;

/// 每多少刻度走完参数全量程（与 JS SDK 一致）。
const FULL_RANGE_TICKS: f64 = 40.0;

fn as_f64(value: Option<&Value>) -> f64 {
    match value {
        Some(Value::F64(number)) => *number,
        Some(Value::F32(number)) => *number as f64,
        Some(Value::Integer(integer)) => match integer.as_i64() {
            Some(number) => number as f64,
            None => integer.as_u64().map(|number| number as f64).unwrap_or(f64::NAN),
        },
        _ => f64::NAN,
    }
}

/// 从按键目录中过滤出可执行的按键（`executable === true`）。
pub fn executable_hotkeys(hotkeys: &[Value]) -> Vec<Value> {
    hotkeys
        .iter()
        .filter(|hotkey| {
            hotkey.get("executable") == Some(&Value::Boolean(true))
        })
        .cloned()
        .collect()
}

/// 参数当前值按旋钮刻度推算后的目标值：每 40 刻度走完全量程，并钳制在
/// `min`/`max` 之内。无效输入的回退行为与 JS 版一致。
pub fn parameter_value_after_ticks(parameter: Option<&Value>, ticks: f64) -> f64 {
    let Some(parameter) = parameter else {
        return 0.0;
    };
    let value = as_f64(parameter.get("value"));
    let min = as_f64(parameter.get("min"));
    let max = as_f64(parameter.get("max"));
    if !ticks.is_finite() || ticks == 0.0 {
        return if value.is_finite() { value } else { 0.0 };
    }
    let span = max - min;
    let step = if span == 0.0 || !span.is_finite() {
        1.0
    } else {
        span / FULL_RANGE_TICKS
    };
    let next = value + ticks * step;
    if !next.is_finite() {
        return value;
    }
    // 与 JS 的 Math.min(max, Math.max(min, next)) 一致。
    next.min(max).max(min)
}

/// 构造写入单个参数值的 `ParameterWriteRequest` 命令；
/// `parameter_id` 为空或 `value` 非有限时返回 `None`。
pub fn write_parameter_command(parameter_id: Option<&str>, value: f64) -> Option<Value> {
    let parameter_id = parameter_id.filter(|id| !id.is_empty())?;
    if !value.is_finite() {
        return None;
    }
    Some(Value::Map(vec![
        ("messageType".into(), "ParameterWriteRequest".into()),
        (
            "data".into(),
            Value::Map(vec![(
                "parameters".into(),
                Value::Map(vec![(parameter_id.into(), value.into())]),
            )]),
        ),
    ]))
}
