//(c) Качмар Сергей

using ManualPacketSerialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReskanaProgect.Helpers
{
    public class Polling<T>
    {
        public Action<T> Complete;

        private volatile int status;
        private ConcurrentQueue<T> buffer = new ConcurrentQueue<T>();

        public Polling()
        {
        }

        public void QueueData(in T data)
        {
            buffer.Enqueue(data);

            if (status == 0)
            {
                status = 1;
                ThreadPool.UnsafeQueueUserWorkItem(x =>
                    {
                        //TODO
                        while (buffer.TryDequeue(out var item))
                            Complete?.Invoke(item);
                    }, null);
            }
        }
    }
}
