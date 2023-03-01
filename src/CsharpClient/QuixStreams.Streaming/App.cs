using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mono.Unix;
using Mono.Unix.Native;
using QuixStreams;
using QuixStreams.Streaming.Raw;

namespace QuixStreams.Streaming
{
    /// <summary>
    /// Helper class to handle default streaming behaviors and handle automatic resource cleanup on shutdown
    /// </summary>
    public static class App
    {
        private static readonly ConcurrentDictionary<ITopicConsumer, bool> topicConsumers = new ConcurrentDictionary<ITopicConsumer, bool>();
        private static readonly ConcurrentDictionary<IRawTopicConsumer, bool> rawTopicConsumers = new ConcurrentDictionary<IRawTopicConsumer, bool>();
        private static readonly ConcurrentDictionary<ITopicProducer, bool> topicProducers = new ConcurrentDictionary<ITopicProducer, bool>();
        private static readonly ConcurrentDictionary<IRawTopicProducer, bool> rawTopicProducers = new ConcurrentDictionary<IRawTopicProducer, bool>();
        
        
        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

        private delegate bool HandlerRoutine(CtrlType sig);

        private enum CtrlType {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }

        /// <summary>
        /// Helper method to handle default streaming behaviors and handle automatic resource cleanup on shutdown
        /// It also ensures topic consumers defined at the time of invocation are subscribed to receive messages.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token to abort. Use when you wish to manually stop streaming for other reason that shutdown.</param>
        /// <param name="beforeShutdown">The callback to invoke before shutting down</param>
        public static void Run(CancellationToken cancellationToken = default, Action beforeShutdown = null)
        {
            var logger = Logging.CreateLogger<object>();
            var waitForProcessShutdownStart = new ManualResetEventSlim();
            var waitForMainExit = new ManualResetEventSlim();
            Action actualBeforeShutdown = () =>
            {
                if (beforeShutdown == null) return;
                logger.LogDebug("Invoking user provided shutdown callback");
                try
                {
                    beforeShutdown();
                    logger.LogDebug("Invoked user provided shutdown callback");
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Exception while invoking user provided shutdown callback");                    
                }
            };

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    var handler = new HandlerRoutine(type =>
                    {
                        logger.LogDebug("Termination signal: {0}", type);
                        waitForProcessShutdownStart.Set();
                        return false;
                    });
                    SetConsoleCtrlHandler(handler, true);
                    
                    AppDomain.CurrentDomain.ProcessExit += (sender, e) =>   
                    {
                        // Don't unwind until main exits
                        waitForMainExit.Wait();
                    };                    
                    break;
                case PlatformID.MacOSX:
                case PlatformID.Unix:
                    Task.Run(() =>
                    {
                        var signals = new []
                        {
                            new UnixSignal(Signum.SIGTERM),
                            new UnixSignal(Signum.SIGQUIT),
                            new UnixSignal(Signum.SIGINT)
                        };
                        var signalIndex = UnixSignal.WaitAny(signals);
                        var signal = signals[signalIndex];
                        logger.LogDebug("Termination signal: {0}", signal.Signum);
                        waitForProcessShutdownStart.Set();
                    }, cancellationToken);
                    AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
                    {
                        // Don't unwind until main exits
                        waitForMainExit.Wait();
                    };
                    break;
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                case PlatformID.WinCE:
                case PlatformID.Xbox:
                    Console.CancelKeyPress += (sender, args) =>
                    {
                        waitForProcessShutdownStart.Set();
                    };
                    AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
                    {
                        // We got a SIGTERM, signal that graceful shutdown has started
                        waitForProcessShutdownStart.Set();
                        
                        // Don't unwind until main exits
                        waitForMainExit.Wait();
                    };                    
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if (cancellationToken != default)
            {
                cancellationToken.Register(() =>
                {
                    waitForProcessShutdownStart.Set();

                    // Don't unwind until main exits
                    waitForMainExit.Wait();
                });
            }

            var opened = 0;
            
            // Init
            foreach (var topicConsumer in topicConsumers)
            {
                topicConsumer.Key.Subscribe();
                opened++;
            }
            
            foreach (var topicConsumer in rawTopicConsumers)
            {
                topicConsumer.Key.Subscribe();
                opened++;
            }
            if (opened > 0) logger.LogDebug("Opened {0} topics for reading", opened);
            else logger.LogDebug("There were no topics top open for reading");

            // Wait for shutdown to start
            waitForProcessShutdownStart.Wait();

            logger.LogDebug($"Waiting for graceful shutdown");
            var sw = Stopwatch.StartNew();
            actualBeforeShutdown();
            foreach (var disposable in topicConsumers)
            {
                try
                {
                    disposable.Key.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Exception while disposing {0}", disposable.GetType().FullName);
                }
            }
            
            foreach (var disposable in rawTopicConsumers)
            {
                try
                {
                    disposable.Key.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Exception while disposing {0}", disposable.GetType().FullName);
                }
            }
            
            foreach (var disposable in topicProducers)
            {
                try
                {
                    disposable.Key.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Exception while disposing {0}", disposable.GetType().FullName);
                }
            }

            foreach (var disposable in rawTopicProducers)
            {
                try
                {
                    disposable.Key.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Exception while disposing {0}", disposable.GetType().FullName);
                }
            }
            
            rawTopicConsumers.Clear();
            topicConsumers.Clear();
            topicProducers.Clear();
            rawTopicProducers.Clear();

            logger.LogDebug("Graceful shutdown completed in {0}", sw.Elapsed);

            // Now we're done with main, tell the shutdown handler
            waitForMainExit.Set();
        }

        internal static void Register(TopicConsumer topicConsumer)
        {
            if (topicConsumers.TryAdd(topicConsumer, false)) topicConsumer.OnDisposed += (s, e) => topicConsumers.TryRemove(topicConsumer, out _);
        }
        

        internal static void Register(RawTopicConsumer rawTopicConsumer)
        {
            if (rawTopicConsumers.TryAdd(rawTopicConsumer, false)) rawTopicConsumer.OnDisposed += (s, e) => rawTopicConsumers.TryRemove(rawTopicConsumer, out _);
        }

        internal static void Register(TopicProducer rawTopicProducer)
        {
            if (topicProducers.TryAdd(rawTopicProducer, false)) rawTopicProducer.OnDisposed += (s, e) => topicProducers.TryRemove(rawTopicProducer, out _);
        }

        internal static void Register(RawTopicProducer rawTopicProducer)
        {
            if (rawTopicProducers.TryAdd(rawTopicProducer, false)) rawTopicProducer.OnDisposed += (s, e) => rawTopicProducers.TryRemove(rawTopicProducer, out _);
        }
    }
}