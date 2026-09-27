using System;

namespace Perisc.Safety;

/// <summary>
/// 时钟（**实现级扩展**，非标准 API 面；用法同 <see cref="SafetyUiHost"/>）。
/// <para>
/// 存在的理由有三条，都不是"为了好看"：
/// </para>
/// <list type="number">
/// <item>6.1.3 要求「时钟回拨不得延长许可」——回拨保护只能建立在**单一取时点**上，
/// 散落各处读系统时间时，任何一处漏掉保护都会让整个保护失效；</item>
/// <item>可测性：许可到期、审计按日分片、降级阈值（2.6.2 的 30 秒）都是时间驱动行为，
/// 不能在自检里被复现就无法给出 9.6.3 要求的证据；</item>
/// <item>一致性：审计时间戳、分片边界、许可判定、守护心跳若各读各的时钟，
/// 会出现"同一条记录里两个时间来自不同基准"的情形。</item>
/// </list>
/// <para>
/// 全仓**唯一**允许直接读系统时间的地方是本接口的实现类；其余位置一律经本接口取值。
/// </para>
/// </summary>
public interface ISafetyClock
{
    /// <summary>取当前时刻。实现**应当**保证单调（不因系统回拨而倒退）。</summary>
    DateTimeOffset Now();
}

/// <summary>
/// 单调系统时钟（默认实现）：取本机时刻，但永不小于已观察到的最大值。
/// <para>
/// 这与原 <c>SafetyProcessState.Now()</c> 的行为逐行一致——本次改造只把它从运行时里搬出来，
/// 语义（6.1.3 的回拨保护）没有任何变化。
/// </para>
/// </summary>
public sealed class MonotonicClock : ISafetyClock
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _source;
    private DateTimeOffset _maxObserved;

    public MonotonicClock()
        : this(() => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// 带底层取时源的单调时钟。
    /// <para>
    /// 存在的理由：6.1.3 的"回拨不得延长许可"是一条**安全属性**，安全属性必须有可复现的证据
    /// （9.6.3），而"等系统时间真的被回拨"是等不到的。把底层取时源做成依赖，
    /// 自检就能用 <see cref="ManualClock"/> 主动把时间往回调，直接断言保护生效——
    /// 否则这条保护只能靠读代码相信它存在。
    /// </para>
    /// </summary>
    public MonotonicClock(Func<DateTimeOffset> source)
    {
        _source = source;
        _maxObserved = source();
    }

    public MonotonicClock(DateTimeOffset start)
        : this(() => start)
    {
    }

    /// <summary>已观察到的最大值（只读观察，不推进）。</summary>
    public DateTimeOffset MaxObserved
    {
        get
        {
            lock (_gate)
            {
                return _maxObserved;
            }
        }
    }

    public DateTimeOffset Now()
    {
        // 先读底层源，再在锁内取较大者：锁只保护比较与写回，不包住取时调用。
        var now = _source();
        lock (_gate)
        {
            if (now > _maxObserved)
            {
                _maxObserved = now;
            }
            return _maxObserved;
        }
    }
}

/// <summary>
/// 手动时钟：时刻完全由调用方推进。
/// <para>
/// 供启动自检使用——自检要在一次进程内验证"到期""回拨 30 秒""跨日分片切换"这类行为，
/// 不可能等真实时间流逝。生产路径不构造此类型。
/// </para>
/// </summary>
public sealed class ManualClock : ISafetyClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset Now()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>按增量推进（负值即模拟系统回拨）。</summary>
    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }
    }

    /// <summary>直接设定时刻。</summary>
    public void Set(DateTimeOffset value)
    {
        lock (_gate)
        {
            _now = value;
        }
    }
}
