using System;
using DokanNet;

namespace rcloneWinMount
{
    internal class Program
    {
        private static void Main(string[] remoteNameArg)
        {
            if (remoteNameArg.Length == 0)
            {
                Console.WriteLine("No Remote defined!");
                Console.WriteLine("Press Return to Exit");
                Console.ReadLine();
            }
            else
            {               
                try
                {
                    var mirror = new rcloneMountPoint(remoteNameArg[0]);
                    mirror.Mount("s:\\", DokanOptions.DebugMode | DokanOptions.NetworkDrive, 5);

                    Console.WriteLine(@"Success");
                }
                catch (DokanException ex)
                {
                    Console.WriteLine(@"Error: " + ex.Message);
                }
            }
            
        }
    }
}