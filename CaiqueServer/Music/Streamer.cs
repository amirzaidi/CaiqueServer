﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CaiqueServer.Music
{
    class Streamer
    {
        private static IPEndPoint EndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 8000);
        private const string IcecastPass = "caiquev6";

        private static ConcurrentDictionary<string, Streamer> Streamers = new ConcurrentDictionary<string, Streamer>();
        private static CancellationTokenSource Stop = new CancellationTokenSource();
        private static ConcurrentBag<ManualResetEvent> ShutdownCompleted = new ConcurrentBag<ManualResetEvent>();

        internal static Streamer Get(string Chat)
        {
            return Streamers.GetOrAdd(Chat, delegate (string Id)
            {
                return new Streamer(Id);
            });
        }

        internal static void Shutdown()
        {
            Stop.Cancel();
            Parallel.ForEach(Streamers, (KVP, s) =>
            {
                KVP.Value.Skip();
            });

            var Shutdown = ShutdownCompleted.ToArray();
            if (Shutdown.Length != 0)
            {
                WaitHandle.WaitAll(Shutdown);
            }
        }

        internal Songdata Song;
        private ConcurrentQueue<Songdata> Queue = new ConcurrentQueue<Songdata>();
        private const int MaxQueued = 16;
        
        internal TaskCompletionSource<bool> Process;
        private TaskCompletionSource<bool> WaitAdd;

        private string Id;

        internal Streamer(string Chat)
        {
            Id = Chat;

            var Reset = new ManualResetEvent(false);
            ShutdownCompleted.Add(Reset);
            BackgroundStream().ContinueWith(delegate
            {
                Reset.Set();
            });
        }

        internal bool Enqueue(string Song)
        {
            var Results = Songdata.Search(Song, 1);
            if (Results.Count == 0)
            {
                return false;
            }

            return Enqueue(Results[0]);
        }

        internal bool Enqueue(Songdata Song)
        {
            if (Queue.Count >= MaxQueued)
            {
                return false;
            }

            Queue.Enqueue(Song);
            WaitAdd?.TrySetResult(true);
            return true;
        }

        internal void Skip()
        {
            Process?.TrySetCanceled();
        }

        private async Task BackgroundStream()
        {
            Console.WriteLine("Started background stream for " + Id);

            while (true)
            {
                try
                {
                    WaitAdd = new TaskCompletionSource<bool>();
                    await StreamUntilQueueEmpty();
                    await WaitAdd.Task;
                }
                catch (Exception Ex)
                {
                    Console.WriteLine(Id + " " + Ex.ToString());
                }
            }
        }

        private async Task StreamUntilQueueEmpty()
        {
            var ProcessStartInfo = new ProcessStartInfo
            {
                FileName = "Includes/ffmpeg",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };

            while (!Stop.IsCancellationRequested && Queue.TryDequeue(out Song))
            {
                ProcessStartInfo.Arguments = $"-re -i \"{Song.StreamUrl}\" -vn -content_type audio/aac -f adts ";
                if (Song.Type == SongType.YouTube)
                {
                    ProcessStartInfo.Arguments += "-c:a copy ";
                }
                else
                {
                    ProcessStartInfo.Arguments += $"-c:a aac -b:a 128k -ac 2 -ar 48k ";
                }
                ProcessStartInfo.Arguments += $"-v quiet -ice_public 1 icecast://source:{IcecastPass}@localhost:8000/{Id}";
                
                using (var Ffmpeg = new Process())
                {
                    Process = new TaskCompletionSource<bool>();

                    try
                    {
                        Ffmpeg.StartInfo = ProcessStartInfo;
                        Ffmpeg.EnableRaisingEvents = true;
                        Ffmpeg.Exited += delegate
                        {
                            Process.TrySetResult(true);
                        };

                        Stop.Token.Register(delegate
                        {
                            Process.TrySetCanceled();
                        });

                        Ffmpeg.Start();
                        Ffmpeg.PriorityClass = ProcessPriorityClass.BelowNormal;

                        Chat.Home.ById(Id).Distribute(new Firebase.Json.Event
                        {
                            Chat = Id,
                            Type = "play",
                            Text = Song.Title
                        });

                        await Process.Task;
                    }
                    catch (TaskCanceledException)
                    {
                    }

                    try
                    {
                        await Ffmpeg.StandardInput.WriteAsync("q");
                    }
                    catch { }
                }
            }
        }
    }
}
