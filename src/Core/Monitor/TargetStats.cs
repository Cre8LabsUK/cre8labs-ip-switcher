using System.Collections.Generic;
using System.Linq;

namespace Cre8Labs.NetTools.Core.Monitor
{
    public enum TargetState { Waiting, Up, Slow, Down }

    /// <summary>
    /// Rolling ping history for one monitored device. Pure logic, so it can be tested without a network.
    /// A device is Down after <see cref="DownAfter"/> misses in a row, so one lost packet never raises an alarm.
    /// </summary>
    public sealed class TargetStats
    {
        public const int Window = 60;
        public const int DownAfter = 3;
        public const long SlowMs = 100;

        readonly Queue<long?> _samples = new Queue<long?>(); // null = no reply

        public TargetState State { get; private set; } = TargetState.Waiting;
        public int ConsecutiveFailures { get; private set; }
        public long? LastMs { get; private set; }
        public int Sent { get; private set; }

        /// <summary>Oldest first. Null entries are missed replies.</summary>
        public IReadOnlyCollection<long?> Samples => _samples;

        public double LossPercent => _samples.Count == 0 ? 0 : 100.0 * _samples.Count(s => s == null) / _samples.Count;

        public double? AverageMs
        {
            get
            {
                var ok = _samples.Where(s => s != null).Select(s => (double)s.Value).ToList();
                return ok.Count == 0 ? (double?)null : ok.Average();
            }
        }

        /// <summary>Records one ping. Returns the new state if it changed (for alerts), otherwise null.</summary>
        public TargetState? Add(bool ok, long ms)
        {
            Sent++;
            _samples.Enqueue(ok ? ms : (long?)null);
            while (_samples.Count > Window) _samples.Dequeue();
            LastMs = ok ? ms : (long?)null;
            ConsecutiveFailures = ok ? 0 : ConsecutiveFailures + 1;

            var next =
                ConsecutiveFailures >= DownAfter ? TargetState.Down :
                !ok ? (State == TargetState.Waiting ? TargetState.Waiting : State) : // a single miss keeps the current state
                ms >= SlowMs ? TargetState.Slow :
                TargetState.Up;
            if (next == State) return null;
            State = next;
            return next;
        }
    }
}
