//
// Batcher.cs
//
// Author:
//     Zachary Gramana  <zack@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc
// Copyright (c) 2014 .NET Foundation
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//
// Copyright (c) 2014 Couchbase, Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
// except in compliance with the License. You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed under the
// License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
// either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
//

using System;
using System.Collections.Generic;
using Couchbase.Lite;
using Couchbase.Lite.Support;
using Couchbase.Lite.Util;
using Sharpen;
using System.Threading.Tasks;
using System.Threading;
using Couchbase.Lite.Internal;

namespace Couchbase.Lite.Support
{
    /// <summary>
    /// Utility that queues up objects until the queue fills up or a time interval elapses,
    /// then passes all the objects at once to a client-supplied processor block.
    /// </summary>
    /// <remarks>
    /// Utility that queues up objects until the queue fills up or a time interval elapses,
    /// then passes all the objects at once to a client-supplied processor block.
    /// </remarks>
    internal class Batcher<T>
    {
        private readonly static string Tag = "Batcher";

        private readonly TaskFactory workExecutor;

        private Task flushFuture;

        private readonly int capacity;

        private readonly int delay;

        private int scheduledDelay;

        private IList<T> inbox;

        private readonly Action<IList<T>> processor;

        private Boolean scheduled;

        private long lastProcessedTime;

        private readonly Action processNowRunnable;

        private readonly Object locker;

        /// <summary>Initializes a batcher.</summary>
        /// <remarks>Initializes a batcher.</remarks>
        /// <param name="workExecutor">the work executor that performs actual work</param>
        /// <param name="capacity">The maximum number of objects to batch up. If the queue reaches this size, the queued objects will be sent to the processor immediately.
        ///     </param>
        /// <param name="delay">The maximum waiting time to collect objects before processing them. In some circumstances objects will be processed sooner.
        ///     </param>
        /// <param name="processor">The callback/block that will be called to process the objects.
        ///     </param>
        public Batcher(TaskFactory workExecutor, int capacity, int delay, Action<IList<T>> processor, CancellationTokenSource tokenSource = null)
        {
            processNowRunnable = new Action(()=>
            {
                try
                {
                    if (tokenSource != null && tokenSource.IsCancellationRequested) 
                    {
                        return;
                    }
                    ProcessNow();
                }
                catch (Exception e)
                {
                    // we don't want this to crash the batcher
                    Log.E(Tag, "BatchProcessor throw exception", e);
                }
            });

            this.locker = new Object ();
            this.workExecutor = workExecutor;
            this.cancellationSource = tokenSource;
            this.capacity = capacity;
            this.delay = delay;
            this.processor = processor;
        }

        public void ProcessNow()
        {
            Log.V(Tag, "processNow() called");

            scheduled = false;

            var toProcess = new List<T>();
            lock (locker)
            {
                if (inbox == null || inbox.Count == 0)
                {
                    Log.V(Tag, "processNow() called, but inbox is empty");
                    return;
                }
                else if (inbox.Count <= capacity)
                {
                    Log.V(Tag, "inbox size <= capacity, adding " + inbox.Count + " items from inbox -> toProcess");
                    toProcess.AddRange(inbox);
                    inbox = null;
                }
                else 
                {
                    Log.V(Tag, "processNow() called, inbox size: " + inbox.Count);

                    int i = 0;
                    foreach (T item in inbox)
                    {
                        toProcess.AddItem(item);
                        i += 1;
                        if (i >= capacity)
                        {
                            break;
                        }
                    }

                    foreach (T item in toProcess)
                    {
                        Log.D(Tag, "processNow() removing " + item + " from inbox");
                        inbox.Remove(item);
                    }

                    Log.D(Tag, "inbox.size() > capacity, moving " + toProcess.Count + " items from inbox -> toProcess array");

                    // There are more objects left, so schedule them Real Soon:
                    ScheduleWithDelay(DelayToUse());
                }
            }

            if (toProcess != null && toProcess.Count > 0)
            {
                Log.D(Tag, "invoking processor with " + toProcess.Count + " items ");
                processor(toProcess);
            }
            else
            {
                Log.D(Tag, "nothing to process");
            }

            var newTime = Runtime.CurrentTimeMillis();
            Interlocked.Exchange(ref lastProcessedTime, newTime);
        }

        CancellationTokenSource cancellationSource;

        public void QueueObjects(IList<T> objects)
        {
            lock (locker)
            {
                Log.V(Tag, "queuObjects called with " + objects.Count + " objects");

                if (objects == null || objects.Count == 0)
                {
                    return;
                }

                if (inbox == null)
                {
                    inbox = new List<T>();
                }

                Log.V(Tag, "inbox size before adding objects: " + inbox.Count);
                foreach (T item in objects)
                {
                    inbox.Add(item);
                }

                ScheduleWithDelay(DelayToUse());
            }
        }

        /// <summary>Adds an object to the queue.</summary>
        public void QueueObject(T o)
        {
            IList<T> objects = new AList<T>();
            objects.Add(o);
            QueueObjects(objects);
        }

        /// <summary>Sends queued objects to the processor block (up to the capacity).</summary>
        public void Flush()
        {
            lock (locker)
            {
                ScheduleWithDelay(DelayToUse());
            }
        }

        /// <summary>Sends _all_ the queued objects at once to the processor block.</summary>
        public void FlushAll()
        {
            lock (locker)
            {
                while(inbox.Count > 0)
                {
                    Unschedule();

                    var toProcess = new List<T>();
                    foreach (T item in inbox)
                    {
                        toProcess.Add(item);
                    }

                    processor(toProcess);
                    lastProcessedTime = Runtime.CurrentTimeMillis();
                }
            }
        }

        /// <summary>Number of items to be processed.</summary>
        public int Count()
        {
            lock (locker) {
                if (inbox == null) {
                    return 0;
                }
                return inbox.Count;
            }
        }

        /// <summary>Empties the queue without processing any of the objects in it.</summary>
        public void Clear()
        {
            lock (locker) {
                Log.V(Tag, "clear() called, setting inbox to null");
                Unschedule();
                if (inbox != null) {
                    inbox.Clear();
                    inbox = null;
                }
            }
        }

        private void ScheduleWithDelay(Int32 suggestedDelay)
        {
            if (!scheduled)
                Log.V(Tag, "scheduleWithDelay called with delay: " + suggestedDelay + " ms");

            if (scheduled && (suggestedDelay < scheduledDelay))
            {
                Log.V(Tag, "already scheduled and : " + suggestedDelay + " < " + scheduledDelay + " --> unscheduling");
                Unschedule();
            }

            if (!scheduled)
            {
                Log.D(Database.Tag, "not already scheduled");

                scheduled = true;
                scheduledDelay = suggestedDelay;

                Log.D(Tag, "workExecutor.schedule() with delay: " + suggestedDelay + " ms");

                cancellationSource = new CancellationTokenSource();
                flushFuture = Task.Delay(scheduledDelay)
                    .ContinueWith(task => 
                    {
                        if(!(task.IsCanceled && cancellationSource.IsCancellationRequested))
                        {
                            workExecutor.StartNew(processNowRunnable);
                        }
                    }, cancellationSource.Token);
            }
        }

        private void Unschedule()
        {
            Log.V(Tag, "unschedule() called");
            scheduled = false;
            if (cancellationSource != null && flushFuture != null)
            {
                try
                {
                    cancellationSource.Cancel(true);
                }
                catch (Exception)
                {
                    // Swallow it.
                } 
                Log.V(Tag, "cancallationSource.Cancel() called");
            }
            else
            {
                Log.V(Tag, "cancellationSource or flushFutre was null, doing nothing");
            }
        }

        /// <summary>
        /// Calculates the delay to use when scheduling the next batch of objects to process.
        /// </summary>
        /// <remarks>
        /// There is a balance required between clearing down the input queue as fast as possible
        /// and not exhausting downstream system resources such as sockets and http response buffers
        /// by processing too many batches concurrently.
        /// </remarks>
        /// <returns>The to use.</returns>
        private Int32 DelayToUse()
        {
            int delayToUse = delay;

            long delta = Runtime.CurrentTimeMillis() - lastProcessedTime;

            if (delta >= delay)
            {
                delayToUse = 0;
            }

            Log.V(Tag, "delayToUse() delta: " + delta + ", delayToUse: " + delayToUse + ", delay: " + delay);

            return delayToUse;
        }
    }
}
