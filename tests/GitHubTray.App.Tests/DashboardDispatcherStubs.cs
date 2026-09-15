// Portable tests compile the real dashboard with inert dispatcher signatures, not a WinUI runtime.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public List<DispatcherQueueTimer> Timers { get; } = [];

        public DispatcherQueueTimer CreateTimer()
        {
            var timer = new DispatcherQueueTimer();
            Timers.Add(timer);
            return timer;
        }

        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }

    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; } = true;
        public bool IsRunning { get; private set; }
        public event Action<DispatcherQueueTimer, object>? Tick;

        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;

        public void Fire()
        {
            if (!IsRunning)
            {
                return;
            }
            if (!IsRepeating)
            {
                IsRunning = false;
            }
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public enum Symbol
    {
        Clock,
        Document,
        Comment,
        Library
    }
}

namespace WinRT
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GeneratedBindableCustomPropertyAttribute : Attribute;
}
