using System;
using AgOpenGPS.Core;

namespace AgOpenGPS.ConsoleApp
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine($"Loaded AgOpenGPS.Core assembly: {typeof(ApplicationCore).Assembly.FullName}");
        }
    }
}
