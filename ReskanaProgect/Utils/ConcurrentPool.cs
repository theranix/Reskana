//(c) Качмар Сергей


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReskanaProgect.Utils
{
    public class ObjectPool<T>
    {
        private readonly ConcurrentQueue<T> objects;
        private readonly Func<T> objectGenerator;

        public ObjectPool(Func<T> objectGenerator, int preloadCapacity)
        {
            this.objectGenerator = objectGenerator;
            objects = new ConcurrentQueue<T>();

            for (int i = 0; i < preloadCapacity; i++)
            {
                objects.Enqueue(objectGenerator());
            }
        }

        public T Get() => objects.TryDequeue(out T item) ? item : objectGenerator();
        public void Return(T item) => objects.Enqueue(item);
    }
}
