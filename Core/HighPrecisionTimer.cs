using System;
using System.Diagnostics;
using System.Threading;

namespace boston_timing_system.Core
{
    public class HighPrecisionTimer : IDisposable
    {
        private readonly Stopwatch _stopwatch = new();
        private Timer? _tickTimer;
        private readonly object _lock = new();

        public event Action<TimeSpan>? OnTick;

        public bool IsRunning
        {
            get
            {
                lock (_lock)
                {
                    return _stopwatch.IsRunning;
                }
            }
        }

        public TimeSpan Elapsed
        {
            get
            {
                lock (_lock)
                {
                    return _stopwatch.Elapsed;
                }
            }
        }

        public long ElapsedMilliseconds
        {
            get
            {
                lock (_lock)
                {
                    return _stopwatch.ElapsedMilliseconds;
                }
            }
        }

        public void Start(int intervalMs = 20)
        {
            lock (_lock)
            {
                if (!_stopwatch.IsRunning)
                {
                    _stopwatch.Start();

                    _tickTimer?.Dispose();
                    _tickTimer = new Timer(TimerCallback, null, 0, intervalMs);
                }
            }
        }

        public TimeSpan Stop()
        {
            lock (_lock)
            {
                if (_stopwatch.IsRunning)
                {
                    _stopwatch.Stop();
                }

                _tickTimer?.Dispose();
                _tickTimer = null;

                TimeSpan finalElapsed = _stopwatch.Elapsed;
                OnTick?.Invoke(finalElapsed);
                return finalElapsed;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _stopwatch.Reset();
                _tickTimer?.Dispose();
                _tickTimer = null;
                OnTick?.Invoke(TimeSpan.Zero);
            }
        }

        public void Restart(int intervalMs = 20)
        {
            lock (_lock)
            {
                Reset();
                Start(intervalMs);
            }
        }

        private void TimerCallback(object? state)
        {
            TimeSpan currentElapsed;
            lock (_lock)
            {
                if (!_stopwatch.IsRunning)
                {
                    return;
                }
                currentElapsed = _stopwatch.Elapsed;
            }

            OnTick?.Invoke(currentElapsed);
        }

        public void Dispose()
        {
            _tickTimer?.Dispose();
            _tickTimer = null;
        }
    }
}
