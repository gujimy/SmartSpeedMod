using System.Collections;
using System.Reflection;
using GameLogic;

namespace SmartSpeedMod
{
    /// <summary>只查询指定操作序号；游戏会在计时器取消或结束时移除对应条目。</summary>
    internal static class TimerDetector
    {
        private static readonly FieldInfo Timers = typeof(OperationTimer).GetField(
            "timerDict", BindingFlags.Static | BindingFlags.NonPublic);

        internal static bool HasActiveOperation(long actionSn)
        {
            try
            {
                var timers = Timers?.GetValue(null) as IDictionary;
                return timers?[actionSn] != null;
            }
            catch { return false; }
        }
    }
}
