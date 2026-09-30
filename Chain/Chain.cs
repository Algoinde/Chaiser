using System.Collections.Concurrent;
using System.Diagnostics;

namespace Chaiser
{
    /// <summary>
    /// A stage turns one item into zero or more items of the next type (Elirium's parser chain,
    /// typed): RawFile -> Container -> NodeItem -> AssetsFileItem -> ObjectItem -> ... Items carry
    /// ranges (file, offset, node), not bytes; block decoding happens inside the node streams.
    /// </summary>
    public abstract class Stage
    {
        public abstract Type In { get; }
        public abstract Type Out { get; }

        /// <summary>Process this stage's outputs in parallel (each thread gets its own Worker).</summary>
        public bool FanOut { get; init; }

        internal abstract IEnumerable<object> RunObject(object item, Worker worker);
        public override string ToString() => GetType().Name;
    }

    public abstract class Stage<TIn, TOut> : Stage
    {
        public sealed override Type In => typeof(TIn);
        public sealed override Type Out => typeof(TOut);
        public abstract IEnumerable<TOut> Run(TIn item, Worker worker);

        internal sealed override IEnumerable<object> RunObject(object item, Worker worker)
        {
            foreach (var output in Run((TIn)item, worker)) yield return output;
        }
    }

    /// <summary>
    /// An item that could not be processed: which stage, which item, why. Failures skip the rest
    /// of the chain and land in the session (logged there), whether a stage threw or reported one.
    /// </summary>
    public sealed record Failure(Stage Stage, object Item, string Message, Exception Exception = null)
    {
        public override string ToString() => $"{Item}: {Message}";
    }

    /// <summary>
    /// State for one chain run over all its inputs (Elirium's Session): the profile, failures,
    /// counters, and whatever shared state stages need (<see cref="Shared{T}"/>). Everything here
    /// is thread-safe; a new run gets a new session.
    /// </summary>
    public sealed class Session
    {
        readonly ConcurrentQueue<Failure> m_Failures = new();
        readonly ConcurrentDictionary<Type, object> m_Shared = new();

        public Session(GameProfile profile = null) => Profile = profile ?? GameProfile.Endfield;

        public GameProfile Profile { get; }
        public Dialect Dialect => Profile.Dialect;
        public bool Verbose { get; init; }

        /// <summary>Items produced per stage index of the chain this session ran.</summary>
        internal int[] Produced = [];

        public IReadOnlyCollection<Failure> Failures => m_Failures;

        /// <summary>One instance of <typeparamref name="T"/> per session, created on first use.</summary>
        public T Shared<T>() where T : class, new() => (T)m_Shared.GetOrAdd(typeof(T), _ => new T());

        public void Fail(Failure failure)
        {
            m_Failures.Enqueue(failure);
            Console.Error.WriteLine($"  {failure.Item}: {failure.Message}");
            if (Verbose && failure.Exception != null) Console.Error.WriteLine(failure.Exception.StackTrace);
        }
    }

    /// <summary>Per-thread resources: read handles and resource streams, released when its input is done.</summary>
    public sealed class Worker : IDisposable
    {
        readonly Dictionary<string, FileStream> m_Handles = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<(Container, string), Stream> m_Resources = new();

        public Worker(Session session) => Session = session;

        public Session Session { get; }

        public FileStream Handle(string path)
        {
            if (!m_Handles.TryGetValue(path, out var handle))
            {
                m_Handles[path] = handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            }
            return handle;
        }

        /// <summary>
        /// A container's sibling resource node ("CAB-xxxx.resS"), opened lazily once per thread, so
        /// textures in several CABs share it. Kept here, not on the container: the stream reads
        /// through this thread's handle.
        /// </summary>
        public Stream Resource(Container container, string nodeName)
        {
            if (!m_Resources.TryGetValue((container, nodeName), out var stream))
            {
                var node = container.Archive.Nodes.FirstOrDefault(n => n.path == nodeName)
                    ?? throw new FileNotFoundException($"resource node '{nodeName}' not in container");
                m_Resources[(container, nodeName)] = stream = container.Archive.OpenNodeLazy(node, Handle(container.File.Path));
            }
            return stream;
        }

        /// <summary>Report an item a stage could not handle (the stage just yields nothing for it).</summary>
        public void Fail(Stage stage, object item, string message) => Session.Fail(new Failure(stage, item, message));

        public void Release()
        {
            foreach (var stream in m_Resources.Values) stream.Dispose();
            m_Resources.Clear();
            foreach (var handle in m_Handles.Values) handle.Dispose();
            m_Handles.Clear();
        }

        public void Dispose() => Release();
    }

    /// <summary>
    /// An ordered list of stages, type-checked on construction. Runs depth-first, so an item's
    /// resources (readers, cached blocks) live only while its outputs are processed; an item is
    /// disposed once consumed. Results keep input order even where stages fan out. A stage error
    /// becomes a Failure in the session; the rest of the run continues.
    /// </summary>
    public sealed class Chain
    {
        readonly Stage[] m_Stages;

        public IReadOnlyList<Stage> Stages => m_Stages;

        public Chain(params Stage[] stages)
        {
            if (stages.Length == 0) throw new ArgumentException("A chain needs at least one stage.");
            for (var i = 1; i < stages.Length; i++)
            {
                if (!stages[i].In.IsAssignableFrom(stages[i - 1].Out))
                    throw new ArgumentException($"{stages[i - 1]} outputs {stages[i - 1].Out.Name} but {stages[i]} takes {stages[i].In.Name}");
            }
            m_Stages = stages;
        }

        /// <summary>The rest of this chain from the first stage that takes a <typeparamref name="T"/>.</summary>
        public Chain From<T>()
        {
            var index = Array.FindIndex(m_Stages, s => s.In == typeof(T));
            if (index < 0) throw new ArgumentException($"No stage takes {typeof(T).Name}");
            return new Chain(m_Stages[index..]);
        }

        /// <summary>How many items the stage producing <typeparamref name="T"/> produced in <paramref name="session"/>.</summary>
        public int Produced<T>(Session session)
        {
            var index = Array.FindIndex(m_Stages, s => s.Out == typeof(T));
            return index < 0 || index >= session.Produced.Length ? 0 : Volatile.Read(ref session.Produced[index]);
        }

        /// <summary>
        /// Runs every input (in parallel, one at a time per thread; heaviest first when a weight is
        /// given) and returns the last stage's outputs in input order. Failures are in the session.
        /// </summary>
        public List<TOut> Run<TIn, TOut>(Session session, IReadOnlyList<TIn> inputs, Func<TIn, long> weight = null, Action<TIn, TimeSpan> done = null)
        {
            if (!m_Stages[0].In.IsAssignableFrom(typeof(TIn)))
                throw new ArgumentException($"{m_Stages[0]} takes {m_Stages[0].In.Name}, not {typeof(TIn).Name}");
            if (!typeof(TOut).IsAssignableFrom(m_Stages[^1].Out))
                throw new ArgumentException($"{m_Stages[^1]} outputs {m_Stages[^1].Out.Name}, not {typeof(TOut).Name}");
            session.Produced = new int[m_Stages.Length];

            var order = Enumerable.Range(0, inputs.Count);
            if (weight != null) order = order.OrderByDescending(i => weight(inputs[i]));
            var results = new List<object>[inputs.Count];
            // NoBuffering: hand out one input at a time; range partitioning gave one thread a whole
            // chunk of the largest inputs.
            Parallel.ForEach(Partitioner.Create(order.ToList(), EnumerablePartitionerOptions.NoBuffering),
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                () => new Worker(session),
                (i, _, worker) =>
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        results[i] = Process(0, inputs[i], worker);
                    }
                    finally
                    {
                        worker.Release();
                        if (inputs[i] is IDisposable disposable) disposable.Dispose();
                    }
                    done?.Invoke(inputs[i], Stopwatch.GetElapsedTime(started));
                    return worker;
                },
                worker => worker.Dispose());
            return results.SelectMany(r => r).Cast<TOut>().ToList();
        }

        List<object> Process(int index, object item, Worker worker)
        {
            if (index == m_Stages.Length) return [item];
            var stage = m_Stages[index];
            var outputs = new List<object>();
            if (!stage.FanOut)
            {
                foreach (var child in Enumerate(stage, item, worker))
                {
                    outputs.AddRange(Child(index, child, worker));
                }
                return outputs;
            }

            // Fan-out: materialize this item's outputs, then process them on all cores. Chunks keep
            // per-thread setup (file handles) amortized over several items.
            var children = Enumerate(stage, item, worker).ToList();
            var slots = new List<object>[children.Count];
            if (children.Count > 0)
            {
                Parallel.ForEach(Partitioner.Create(0, children.Count, Math.Max(1, children.Count / (Environment.ProcessorCount * 4))),
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    () => new Worker(worker.Session),
                    (range, _, childWorker) =>
                    {
                        for (var i = range.Item1; i < range.Item2; i++) slots[i] = Child(index, children[i], childWorker);
                        return childWorker;
                    },
                    childWorker => childWorker.Dispose());
            }
            foreach (var slot in slots) outputs.AddRange(slot);
            return outputs;
        }

        /// <summary>A stage's outputs for one item; an exception ends them with a Failure.</summary>
        static IEnumerable<object> Enumerate(Stage stage, object item, Worker worker)
        {
            using var outputs = stage.RunObject(item, worker).GetEnumerator();
            while (true)
            {
                object child;
                try
                {
                    if (!outputs.MoveNext()) yield break;
                    child = outputs.Current;
                }
                catch (Exception e)
                {
                    worker.Session.Fail(new Failure(stage, item, $"{e.GetType().Name}: {e.Message}", e));
                    yield break;
                }
                yield return child;
            }
        }

        List<object> Child(int index, object child, Worker worker)
        {
            Interlocked.Increment(ref worker.Session.Produced[index]);
            var last = index + 1 == m_Stages.Length;
            try
            {
                return Process(index + 1, child, worker);
            }
            finally
            {
                // The caller owns the chain's results; everything else is consumed here.
                if (!last && child is IDisposable disposable) disposable.Dispose();
            }
        }
    }
}
