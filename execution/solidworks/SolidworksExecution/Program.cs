using System;
using System.Threading;
using Microsoft.Owin.Hosting;
using SolidworksExecution.Infrastructure;

namespace SolidworksExecution
{
    class Program
    {
        // Signalled to stop the server. Lets the host stay alive WITHOUT depending on
        // Console.ReadLine() — so the exe can run headless (launched in the background)
        // and not exit immediately when stdin is not an interactive console.
        private static readonly ManualResetEventSlim _shutdown = new ManualResetEventSlim(false);

        // SolidWorks COM requires an STA thread. We host OWIN from a dedicated STA thread.
        static void Main(string[] args)
        {
            // Graceful Ctrl+C when run interactively; headless runs stay up until the
            // process is killed.
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; _shutdown.Set(); };

            Exception threadException = null;

            var staThread = new Thread(() =>
            {
                try
                {
                    string baseAddress = "http://localhost:5000/";
                    using (WebApp.Start<Startup>(url: baseAddress))
                    {
                        Console.WriteLine("SolidworksExecution server running at " + baseAddress);
                        Console.WriteLine("Press Ctrl+C to stop.");
                        ExecLog.Write("server started at " + baseAddress);
                        _shutdown.Wait();
                        ExecLog.Write("server shutting down");
                    }
                }
                catch (Exception ex)
                {
                    threadException = ex;
                    ExecLog.Write("FATAL host error: " + ex);
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = false;
            staThread.Name = "SolidWorksSTAHost";
            staThread.Start();
            staThread.Join();

            if (threadException != null)
            {
                Console.Error.WriteLine("Fatal error: " + threadException.Message);
                Environment.Exit(1);
            }
        }
    }
}
