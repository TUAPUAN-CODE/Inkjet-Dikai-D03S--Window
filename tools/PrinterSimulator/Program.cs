using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;

namespace Dikai.Simulator
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            var o = new SimulatedPrinter.Options();
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + a);
                    switch (a)
                    {
                        case "--port": o.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--bind": o.Bind = IPAddress.Parse(Next()); break;
                        case "--st": o.Station = byte.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--print-ms": o.PrintIntervalMs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--queue": o.QueueCapacity = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--files": o.ExtraFiles = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--page": o.FileListPageSize = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--report": o.ReportIntervalSec = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--blank-names": o.BlankFileNames = true; break;
                        case "--reject-vars-in-34h": o.RejectVarsInSelect = true; break;
                        case "-h":
                        case "--help":
                        case "/?":
                            PrintHelp();
                            return 0;
                        default:
                            throw new ArgumentException("unknown option " + args[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                PrintHelp();
                return 2;
            }

            o.Log = s => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + s);
            using (var sim = new SimulatedPrinter(o))
            {
                try
                {
                    sim.Start();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Cannot listen on port " + o.Port + ": " + ex.Message);
                    return 1;
                }
                Console.WriteLine("Dikai DGCP printer simulator");
                Console.WriteLine("  listening " + o.Bind + ":" + sim.Port + "   ST=" + o.Station);
                Console.WriteLine("  templates: TEST(text1)  LOT(lot,date)  1..5" + (o.ExtraFiles > 0 ? "  FILE001..FILE" + o.ExtraFiles.ToString("000", CultureInfo.InvariantCulture) : ""));
                Console.WriteLine("  press Ctrl+C to stop");
                var quit = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    quit.Set();
                };
                quit.WaitOne();
            }
            return 0;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("PrinterSimulator [options]");
            Console.WriteLine("  --port 5000       TCP port");
            Console.WriteLine("  --bind 127.0.0.1  local IP to listen on (0.0.0.0 = allow other PCs)");
            Console.WriteLine("  --st 1            printer station number (ST)");
            Console.WriteLine("  --print-ms 1000   auto print interval while running (0 = only on trigger)");
            Console.WriteLine("  --queue 3         buffer size for 35h/49h (error 08 when full)");
            Console.WriteLine("  --files 0         extra dummy templates FILE001..");
            Console.WriteLine("  --page 100        file names per 41h packet");
            Console.WriteLine("  --report 0        push 3Fh status every N seconds (0 = off)");
            Console.WriteLine("  --blank-names     answer 41h with empty (00) file names, like some real units");
            Console.WriteLine("  --reject-vars-in-34h  answer error 6 when 34h carries variables, like some real units");
        }
    }
}
