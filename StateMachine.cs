using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Configuration;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        const int MAX_LOG_SIZE = 1000;
        Queue<string> _debugLogs = new Queue<string>(MAX_LOG_SIZE);
        void Debug(string msg)
        {
            if (_debugLogs.Count >= MAX_LOG_SIZE) _debugLogs.Dequeue();
            _debugLogs.Enqueue(msg);
        }
        string DebugLogs => string.Join("\n", _debugLogs);

        EventLoop _defaultEventLoop;
        public EventLoop InitializeEventLoop(Program program, MyIni ini = null) => _defaultEventLoop ?? (_defaultEventLoop = new EventLoop(program, ini));
        public void RunEventLoop() => _defaultEventLoop.Run();

        class StateMachine<TState, TEvent>
        {
            struct EventTimeout
            {
                public long Delay;
                public EventLoopTimerCallback Handler;
            }

            protected TState _currentState;
            protected long _updateInterval;
            protected int _debug;

            private readonly Dictionary<TState, Dictionary<TEvent, EventLoopProbe>> _stateMachine = new Dictionary<TState, Dictionary<TEvent, EventLoopProbe>>();
            private readonly Dictionary<TState, EventTimeout?> _timers = new Dictionary<TState, EventTimeout?>();
            private readonly List<EventLoopProbe> _activeProbes = new List<EventLoopProbe>();
            private EventLoopTimer _activeEventTimeout;

            public readonly Program Pgm;
            public readonly EventLoop EvtLoop;
            public TState CurrentState => _currentState;

            public StateMachine(EventLoop eventLoop = null, MyIni ini = null)
            {
                EvtLoop = eventLoop ?? Pgm._defaultEventLoop;
                if (EvtLoop == null) throw new Exception("Event loop not initialized");
                Pgm = EvtLoop.Pgm;
                _updateInterval = ini?.Get("StateMachine", "UpdateInterval").ToInt64(100) ?? 100;
                _debug = ini?.Get("StateMachine", "debug").ToInt32() ?? 0;
            }

            public TState SetState(TState state)
            {
                if (_debug > 0) Pgm.Debug($"StateMachine#{GetHashCode():X}: SetState {state}");
                var oldState = _currentState;
                _currentState = state;
                UpdateProbes();
                return oldState;
            }

            public void AddState(TState state)
            {
                if (!_stateMachine.ContainsKey(state)) _stateMachine[state] = new Dictionary<TEvent, EventLoopProbe>();
                if (!_timers.ContainsKey(state)) _timers[state] = null;
            }

            public void SetEventHandler(TState state, TEvent @event, Func<bool> condition, EventLoopTask task)
            {
                AddState(state);
                var stateEvents = _stateMachine[state];
                if (stateEvents.ContainsKey(@event))
                {
                    EvtLoop.RemoveProbe(stateEvents[@event]);
                }
                var eventHandler = CreateEventHandler(task);
                var probe = EvtLoop.AddProbe(eventHandler, condition, _updateInterval);
                stateEvents[@event] = probe;
            }

            public void SetEventTimeout(TState state, long delay, EventLoopTask task)
            {
                AddState(state);
                var timeout = new EventTimeout()
                {
                    Delay = delay,
                    Handler = CreateTimeoutHandler(task),
                };
                _timers[state] = timeout;
            }

            public EventLoopTask WaitFor(Func<bool> fnCondition, long timeout = 0) => EvtLoop.WaitFor(fnCondition, _updateInterval, timeout);

            private EventLoopProbeCallback CreateEventHandler(EventLoopTask task)
            {
                return (el, probe) =>
                {
                    if (_debug > 0) Pgm.Debug($"StateMachine#{GetHashCode():X}.OnEvent({probe.Condition?.Method.Name}): {task?.Method.Name}");
                    DisableProbes();
                    _activeEventTimeout?.Reset();
                    if (task != null) EvtLoop.AddTask(task);
                    else UpdateProbes(false);
                };
            }

            private EventLoopTimerCallback CreateTimeoutHandler(EventLoopTask task)
            {
                return (el, timer) =>
                {
                    if (_debug > 0) Pgm.Debug($"StateMachine#{GetHashCode():X}.OnTimeout: {task?.Method.Name}");
                    DisableProbes();
                    _activeEventTimeout = null;
                    if (task != null) EvtLoop.AddTask(task);
                    else UpdateProbes();
                };
            }
            protected void DisableProbes()
            {
                foreach (var probe in _activeProbes) probe.Disable();
                _activeProbes.Clear();
            }

            protected void DisableTimeout()
            {
                if (_activeEventTimeout != null) EvtLoop.CancelTimer(_activeEventTimeout);
                _activeEventTimeout = null;
            }

            protected void UpdateProbes(bool resetTimeout = true)
            {
                DisableProbes();

                Dictionary<TEvent, EventLoopProbe> eventMap;
                if (!_stateMachine.TryGetValue(_currentState, out eventMap))
                {
                    if (resetTimeout) DisableTimeout();
                    if (_debug > 0) Pgm.Debug($"StateMachine#{GetHashCode():X}: All probes disabled");
                    return;
                }
                foreach (var eventHandler in eventMap.Values)
                {
                    if (eventHandler != null)
                    {
                        eventHandler.Enable();
                        _activeProbes.Add(eventHandler);
                    }
                }
                if (!_timers.ContainsKey(_currentState) || _timers[_currentState] == null)
                {
                    DisableTimeout();
                }
                else if (resetTimeout)
                {
                    DisableTimeout();
                    var timer = _timers[_currentState].Value;
                    _activeEventTimeout = EvtLoop.SetTimeout(timer.Handler, timer.Delay);
                }
                if (_debug > 0) Pgm.Debug($"StateMachine#{GetHashCode():X}: {_activeProbes.Count} probe active, timeout {_activeEventTimeout?.RemainingTime ?? -1}");
            }
        }

        public class EventLoop
        {
            public readonly Program Pgm;
            private readonly int _maxTaskPerLoop;
            private readonly LinkedList<EventLoopTimer> _timers = new LinkedList<EventLoopTimer>();
            private readonly HashSet<EventLoopProbe> _probes = new HashSet<EventLoopProbe>();
            private readonly Queue<IEnumerator<EventLoopTask>> _tasks = new Queue<IEnumerator<EventLoopTask>>();
            private IEnumerator<EventLoopTask> _runningTask = null;

            public EventLoop(Program program, MyIni ini = null)
            {
                Pgm = program;
                _maxTaskPerLoop = ini?.Get("EventLoop", "maxTaskPerLoop").ToInt32(5) ?? 5;
                Pgm.Runtime.UpdateFrequency |= UpdateFrequency.Update10;
            }

            public void AddTask(EventLoopTask task) => _tasks.Enqueue(task.Invoke(this).GetEnumerator());
            public void AddTask(IEnumerable<EventLoopTask> task) => _tasks.Enqueue(task.GetEnumerator());
            public void AddTask(IEnumerator<EventLoopTask> task) => _tasks.Enqueue(task);

            public EventLoopTimer SetTimeout(EventLoopTimerCallback task, long delay)
            {
                var timer = new EventLoopTimer(task, delay);
                _timers.AddFirst(timer);
                return timer;
            }
            public EventLoopTimer SetInterval(EventLoopTimerCallback task, long delay)
            {
                var timer = new EventLoopTimer(task, 0, delay);
                _timers.AddFirst(timer);
                return timer;
            }
            public bool CancelTimer(EventLoopTimer timer) => timer != null && _timers.Remove(timer);
            public void ResetTimer(EventLoopTimer timer) => timer?.Reset();

            public EventLoopProbe AddProbe(EventLoopProbeCallback cb, Func<bool> fnCondition, long minTimeBetweenUpdates) 
            {
                EventLoopProbe probe = new EventLoopProbe(cb, fnCondition, minTimeBetweenUpdates);
                _probes.Add(probe);
                return probe;
            }
            public void RemoveProbe(EventLoopProbe probe)
            {
                probe.Disable();
                _probes.Remove(probe);
            }

            public EventLoopTask Sleep(long delay)
            {
                var currentTask = _runningTask;
                SetTimeout((el, _) => el.AddTask(currentTask), delay);
                return null;
            }

            public EventLoopTask WaitFor(Func<bool> fnCondition, long minTimeBetweenUpdates, long timeout = 0)
            {
                var currentTask = _runningTask;
                EventLoopTimer timer = null;
                EventLoopProbe probe = null;
                probe = AddProbe((el, p) => {
                    el.CancelTimer(timer);
                    el.RemoveProbe(p);
                    el.AddTask(currentTask);
                }, fnCondition, minTimeBetweenUpdates);
                timer = SetTimeout((el, _) => {
                    el.RemoveProbe(probe);
                    el.AddTask(currentTask);
                }, timeout);
                probe.Enable();
                return null;
            }

            public void Run()
            {
                var elapsedTicks = Pgm.Runtime.TimeSinceLastRun.Ticks;
                RunTimers(elapsedTicks);
                RunProbes(elapsedTicks);
                RunTasks();
            }

            private void RunTimers(long elapsedTicks)
            {
                var nextTimerNode = _timers.First;
                while (nextTimerNode != null)
                {
                    var timerNode = nextTimerNode;
                    var timer = timerNode.Value;
                    nextTimerNode = timerNode.Next;

                    timer.Update(elapsedTicks);
                    if (timer.Expired)
                    {
                        timer.Callback?.Invoke(this, timer);
                        if (timer.ShouldRestart)
                            timer.Reset();
                        // Test again in order to allow callback to reset timer
                        if (timer.Expired && timerNode.List != null)
                            _timers.Remove(timerNode);
                    }
                }
            }

            private void RunProbes(long elapsedTicks)
            {
                var probes = _probes.ToArray();
                foreach (var probe in probes)
                {
                    if (!probe.Active) continue; 
                    if (probe.Update(elapsedTicks))
                    {
                        probe.Callback?.Invoke(this, probe);
                    }
                }
            }

            private void RunTasks()
            {
                int remainingTaskCount = _maxTaskPerLoop;
                while (_tasks.Count > 0)
                {
                    var task = _tasks.Dequeue();
                    _runningTask = task;
                    if (task.MoveNext())
                    {
                        if (task.Current != null) AddTask(CreateSubtask(task.Current, task));
                    }
                    else
                    {
                        task.Dispose();
                    }
                    _runningTask = null;
                    if (remainingTaskCount > 0)
                    {
                        remainingTaskCount --;
                        if (remainingTaskCount == 0) break;
                    }
                }
            }

            private IEnumerable<EventLoopTask> CreateSubtask(EventLoopTask subtask, IEnumerator<EventLoopTask> maintask)
            {
                foreach (var subresult in subtask.Invoke(this)) yield return subresult;
                AddTask(maintask);
            }
        }

        public delegate IEnumerable<EventLoopTask> EventLoopTask(EventLoop ev);
        public delegate void EventLoopTimerCallback(EventLoop ev, EventLoopTimer timer);
        public delegate void EventLoopProbeCallback(EventLoop ev, EventLoopProbe probe);

        public class EventLoopTimer
        {
            public EventLoopTimerCallback Callback;
            public bool Expired => _remainingTicks <= 0;
            public bool ShouldRestart => _restartTicks > 0;
            private readonly long _restartTicks;
            private long _remainingTicks;

            public EventLoopTimer(EventLoopTimerCallback callback, long delay, long restart=0)
            {
                Callback = callback;
                _restartTicks = restart * TimeSpan.TicksPerMillisecond;
                _remainingTicks = delay * TimeSpan.TicksPerMillisecond;
            }

            public void Reset() => _remainingTicks = _restartTicks;
            public void Update(long elapsedTicks) => _remainingTicks -= elapsedTicks;
            public long RemainingTime => _remainingTicks / TimeSpan.TicksPerMillisecond;
        }

        public class EventLoopProbe
        {
            public EventLoopProbeCallback Callback;
            private long _ticksLeftToUpdate = 0;
            private bool _active = false;
            private readonly long _ticksBetweenUpdates = 0;
            public readonly Func<bool> Condition;

            public bool Active => _active;

            public EventLoopProbe(EventLoopProbeCallback cb, Func<bool> fnCheckCondition, long minTimeBetweenUpdates)
            {
                Callback = cb;
                Condition = fnCheckCondition;
                _ticksBetweenUpdates = minTimeBetweenUpdates * TimeSpan.TicksPerMillisecond;
            }

            public bool Update(long elapsedTicks)
            {
                _ticksLeftToUpdate -= elapsedTicks;
                if (_ticksLeftToUpdate > 0) return false;
                _ticksLeftToUpdate = _ticksBetweenUpdates;
                return Condition();
            }

            public void Enable() => _active = true;
            public void Disable() => _active = false;
        }
    }
}