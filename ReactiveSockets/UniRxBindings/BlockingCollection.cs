using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReactiveSockets
{
    public class PseudoBlockingCollection<T>
    {
        readonly Queue<T> q = new Queue<T>();
        readonly object gate = new object();

        public void Add(T item)
        {
            Monitor.Enter(gate);
            try
            {
                q.Enqueue(item);
                Monitor.PulseAll(gate);
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        public IEnumerable<T> GetConsumingEnumerable()
        {
            bool hasNext;
            var current = default(T);
            while (true)
            {
                Monitor.Enter(gate);
                try
                {
                    hasNext = q.Count != 0;
                    if (hasNext)
                    {
                        current = q.Dequeue();
                    }
                    else
                    {
                        Monitor.Wait(gate);
                    }
                }
                finally
                {
                    Monitor.Exit(gate);
                }

                if (hasNext)
                {
                    yield return current;
                }
            }
        }
    }
}