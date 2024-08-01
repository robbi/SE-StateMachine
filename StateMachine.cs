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

        static EventLoop _defaultEventLoop;
        public static EventLoop InitializeEventLoop(Program program, int maxTaskPerLoop) => _defaultEventLoop ?? (_defaultEventLoop = new EventLoop(program, maxTaskPerLoop));
        public static void RunEventLoop() => _defaultEventLoop.Run();

        class StateMachine<TState, TEvent>
        {
            struct EventTimeout
            {
                public long Delay;
                public EventLoopTimerCallback Handler;
            }

            protected readonly EventLoop _eventLoop;
            protected TState _currentState;
            protected long _updateInterval;

            private readonly Dictionary<TState, Dictionary<TEvent, EventLoopProbe>> _stateMachine = new Dictionary<TState, Dictionary<TEvent, EventLoopProbe>>();
            private readonly Dictionary<TState, EventTimeout?> _timers = new Dictionary<TState, EventTimeout?>();
            private readonly List<EventLoopProbe> _activeProbes = new List<EventLoopProbe>();
            private EventLoopTimer _activeEventTimeout;

            public TState CurrentState => _currentState;

            public StateMachine(EventLoop eventLoop = null, long updateInterval = 100)
            {
                _eventLoop = eventLoop ?? _defaultEventLoop;
                if (_eventLoop == null) throw new Exception("Event loop not initialized");
                _updateInterval = updateInterval;
            }

            public TState SetState(TState state)
            {
                var oldState = _currentState;
                if (!_stateMachine.ContainsKey(state)) throw new Exception($"Cannot set state to unknown {state}");
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
                    _eventLoop.RemoveProbe(stateEvents[@event]);
                }
                var eventHandler = CreateEventHandler(task);
                var probe = _eventLoop.AddProbe(eventHandler, condition, _updateInterval);
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

            public EventLoopTask WaitFor(Func<bool> fnCondition, long timeout = 0) => _eventLoop.WaitFor(fnCondition, _updateInterval, timeout);

            private EventLoopProbeCallback CreateEventHandler(EventLoopTask task)
            {
                return (el, probe) =>
                {
                    DisableProbes();
                    DisableTimeout();
                    _eventLoop.AddTask(task);
                };
            }

            private EventLoopTimerCallback CreateTimeoutHandler(EventLoopTask task)
            {
                return (el, timer) =>
                {
                    DisableProbes();
                    DisableTimeout();
                    _eventLoop.AddTask(task);
                };
            }
            protected void DisableProbes()
            {
                foreach (var probe in _activeProbes) probe.Disable();
                _activeProbes.Clear();
            }

            protected void DisableTimeout()
            {
                if (_activeEventTimeout != null) _eventLoop.CancelTimer(_activeEventTimeout);
                _activeEventTimeout = null;
            }

            protected void UpdateProbes(bool resetTimeout = true)
            {
                DisableProbes();
                if (resetTimeout) DisableTimeout();

                Dictionary<TEvent, EventLoopProbe> eventMap;
                if (!_stateMachine.TryGetValue(_currentState, out eventMap)) return;
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
                    if (!resetTimeout) DisableTimeout();
                }
                else
                {
                    var timer = _timers[_currentState].Value;
                    _activeEventTimeout = _eventLoop.SetTimeout(timer.Handler, timer.Delay);
                }
            }
        }

        public class EventLoop
        {
            private readonly Program _program;
            private readonly int _maxTaskPerLoop;
            private readonly LinkedList<EventLoopTimer> _timers = new LinkedList<EventLoopTimer>();
            private readonly HashSet<EventLoopProbe> _probes = new HashSet<EventLoopProbe>();
            private readonly Queue<IEnumerator<EventLoopTask>> _tasks = new Queue<IEnumerator<EventLoopTask>>();
            private IEnumerator<EventLoopTask> _runningTask = null;

            // public void Debug(string msg, int level=0) => _program.Debug(msg, level); //DEBUG

            public EventLoop(Program program, int maxTaskPerLoop)
            {
                _program = program;
                _maxTaskPerLoop = maxTaskPerLoop;

                _program.Runtime.UpdateFrequency |= UpdateFrequency.Update10;
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
            public bool CancelTimer(EventLoopTimer timer) => _timers.Remove(timer);
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
                var elapsedTicks = _program.Runtime.TimeSinceLastRun.Ticks;
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
        }

        public class EventLoopProbe
        {
            public EventLoopProbeCallback Callback;
            private long _ticksLeftToUpdate = 0;
            private bool _active = false;
            private readonly long _ticksBetweenUpdates = 0;
            private readonly Func<bool> _checkCondition;

            public bool Active => _active;

            public EventLoopProbe(EventLoopProbeCallback cb, Func<bool> fnCheckCondition, long minTimeBetweenUpdates)
            {
                Callback = cb;
                _checkCondition = fnCheckCondition;
                _ticksBetweenUpdates = minTimeBetweenUpdates * TimeSpan.TicksPerMillisecond;
            }

            public bool Update(long elapsedTicks)
            {
                _ticksLeftToUpdate -= elapsedTicks;
                if (_ticksLeftToUpdate > 0) return false;
                _ticksLeftToUpdate = _ticksBetweenUpdates;
                return _checkCondition();
            }

            public void Enable() => _active = true;
            public void Disable() => _active = false;
        }
    }
}