using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace ConsoleArgs
{
    class Program
    {
        static void Main(string[] args)
        {
            string msg;
            if (args == null || args.Length == 0)
                msg = "Args is NULL";
            else
                msg = string.Join(',', args);


            Thread.Sleep(30 * 1000);


            Console.WriteLine(msg);
                        
            using (StreamWriter sw = new StreamWriter($"{Path.GetDirectoryName((Assembly.GetEntryAssembly().Location))}/log.txt", true))
            {
                sw.WriteLine($"[{DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")}] {msg}");
                sw.Close();
            }

            //Console.ReadLine();
        }
    }
}
