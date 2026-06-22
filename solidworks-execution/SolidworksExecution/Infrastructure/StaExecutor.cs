using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SolidworksExecution.Infrastructure
{
    /// <summary>
    /// Runs delegates on a single, long-lived, dedicated STA thread. (P0.1)
    ///
    /// SolidWorks COM objects must be called from an STA apartment. OWIN / Web API request
    /// threads are MTA, and while COM marshaling lets lightweight calls (e.g. FeatureExtrusion3)
    /// limp across the apartment boundary, heavier or more apartment-sensitive calls fail off-STA:
    /// InsertSheetMetalBaseFlange2 silently returned null and add_edge_feature (edge fillet)
    /// deadlocked (ContextSwitchDeadlock). Routing ALL COM-touching work through one STA thread —
    /// including the GetActiveObject attach — makes the RCW live on the STA, fixing both.
    ///
    /// A single worker thread also serializes COM access, which suits the single-writer,
    /// process-global state model the OperationGuard already assumes.
    /// </summary>
    public sealed class StaExecutor
    {
        public static readonly StaExecutor Instance = new StaExecutor();

        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();

        private StaExecutor()
        {
            var thread = new Thread(Worker)
            {
                IsBackground = true,
                Name = "SolidWorksStaThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Worker()
        {
            // Each job sets its own completion event and captures its own exception, so a throwing
            // job never tears down the worker loop.
            foreach (var work in _queue.GetConsumingEnumerable())
                work();
        }

        /// <summary>Marshal <paramref name="func"/> onto the STA thread and block until it returns.</summary>
        public T Run<T>(Func<T> func)
        {
            T result = default(T);
            Exception captured = null;
            using (var done = new ManualResetEventSlim(false))
            {
                _queue.Add(() =>
                {
                    try { result = func(); }
                    catch (Exception ex) { captured = ex; }
                    finally { done.Set(); }
                });
                done.Wait();
            }
            if (captured != null)
                throw new InvalidOperationException("STA execution failed: " + captured.Message, captured);
            return result;
        }
    }
}
