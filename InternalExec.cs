using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace rcloneWinMount
{
    public class InternalExec
    {
        static string output = "";
        static string errOutput = "";
        public Dictionary<string, MemoryStream> cachedfiles = new Dictionary<string, MemoryStream>();

        public void init()
        {

        }

        public string Execute(string command, string remote, string filename)
        {
            output = null; errOutput = null;

            //set up cmd to call rclone
            Process process = new Process();
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = "/c rclone.exe " + command + " " + remote + "\"" + filename + "\"" + " --max-depth 1 --log-file verbose.log --verbose" + " --stats 5s";
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.StartInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (command == "cat")
            {
                process.StartInfo.StandardOutputEncoding = Encoding.Default;
                process.Start();

                filename = filename.Replace(@"\", "");
                using (StreamReader msStream = new StreamReader(process.StandardOutput.BaseStream))
                {
                    if (!cachedfiles.ContainsKey(filename))
                    {
                        MemoryStream tmp = new MemoryStream();
                        msStream.BaseStream.CopyTo(tmp);
                        cachedfiles.Add(filename, tmp);
                    }
                }

                return "";
            }
            else
            {
                process.OutputDataReceived += (sender, args) => proc_OutputDataReceived(sender, args);
                process.ErrorDataReceived += (sender, args) => proc_ErrorDataReceived(sender, args);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                if (String.IsNullOrEmpty(output)) { output = ""; }
                return output.Replace("\r", "");
            }
            
        }
        //process stdout
        static void proc_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                output += e.Data.ToString() + Environment.NewLine;
            }
        }
        //process stderr
        static void proc_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                errOutput += e.Data.ToString() + Environment.NewLine;
            }
        }

    }


}