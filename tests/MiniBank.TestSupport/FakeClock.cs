namespace MiniBank.TestSupport;

/// <summary>Управляемые часы: время стоит на месте, пока тест не сдвинет его.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = start;

    public DateTimeOffset Now
    {
        get { lock (_gate) return _now; }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate) _now += delta;
    }

    public void Set(DateTimeOffset value)
    {
        lock (_gate) _now = value;
    }

    public override DateTimeOffset GetUtcNow() => Now;
}
