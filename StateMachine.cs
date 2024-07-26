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

        private EventLoop _defaultEventLoop;
        public EventLoop InitializeEventLoop(Program program, int maxTaskPerLoop) => _defaultEventLoop = new EventLoop(program, maxTaskPerLoop);
        public void RunEventLoop() => _defaultEventLoop.Run();

        public class EventLoop
        {
            private readonly Program _program;
            private readonly int _maxTaskPerLoop;
            private readonly LinkedList<EventLoopTimer> _timers = new LinkedList<EventLoopTimer>();
            private readonly HashSet<EventLoopProbe> _probes = new HashSet<EventLoopProbe>();
            private readonly Queue<IEnumerator<EventLoopTask>> _tasks = new Queue<IEnumerator<EventLoopTask>>();
            private IEnumerator<EventLoopTask> _runningTask = null;

            public void Debug(string msg, int level=0) => _program.Debug(msg, level);

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
            public bool CancelTimeout(EventLoopTimer timer) => _timers.Remove(timer);
            public void ResetTimeout(EventLoopTimer timer) => timer?.Reset();

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
                    el.CancelTimeout(timer);
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
                        timer.Callback?.Invoke(this, timer);
                    // Test number of remaining ticks again in order to allow callback to reset timer
                    if (timer.Expired)
                        _timers.Remove(timerNode);
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
            private readonly long _initialTicks;
            private long _remainingTicks;

            public EventLoopTimer(EventLoopTimerCallback callback, long delay)
            {
                Callback = callback;
                _initialTicks = delay * TimeSpan.TicksPerMillisecond;
                _remainingTicks = _initialTicks;
            }

            public void Reset() => _remainingTicks = _initialTicks;
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