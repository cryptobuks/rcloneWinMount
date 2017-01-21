using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using DokanNet;
using DokanNet.Logging;
using static DokanNet.FormatProviders;
using FileAccess = DokanNet.FileAccess;
using System.Text.RegularExpressions;
using System.Security.Principal;

namespace rcloneWinMount
{
    internal class rcloneMountPoint : IDokanOperations
    {

        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                              FileAccess.Execute |
                                              FileAccess.GenericExecute | FileAccess.GenericWrite |
                                              FileAccess.GenericRead;

        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        private ConsoleLogger logger = new ConsoleLogger("[AKRCLONE] ");
        InternalExec InternalExecHandler = new InternalExec();
        string lastfilename = "";
        string remoteName = "";
        private const int ReadTimeout = 30000;
        IList<FileInformation> lastfiles = new List<FileInformation>();

        public rcloneMountPoint(string remoteName)
        {
            this.remoteName = remoteName;
        }

        private string GetPath(string fileName)
        {
            return "C:\\" + fileName;
        }

        private NtStatus Trace(string method, string fileName, DokanFileInfo info, NtStatus result,
            params object[] parameters)
        {
#if TRACE
            var extraParameters = parameters != null && parameters.Length > 0
                ? ", " + string.Join(", ", parameters.Select(x => string.Format(DefaultFormatProvider, "{0}", x)))
                : string.Empty;

            logger.Debug(($"{method}('{fileName}', {info}{extraParameters}) -> {result}"));
#endif

            return result;
        }

        private NtStatus Trace(string method, string fileName, DokanFileInfo info,
            FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes,
            NtStatus result)
        {
#if TRACE
            logger.Debug(($"{method}('{fileName}', {info}, [{access}], [{share}], [{mode}], [{options}], [{attributes}]) -> {result}"));
#endif

            return result;
        }

        #region Implementation of IDokanOperations

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
            FileOptions options, FileAttributes attributes, DokanFileInfo info)
        {

            if (mode == FileMode.CreateNew)
            {

                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
               DokanResult.Success);
            }
            FileInformation fdf = new FileInformation();
            if (GetFileInformation(fileName, out fdf, info ) == 0)
            {
                if (!ignoredFile(fileName, info) && fdf.Attributes != FileAttributes.Directory)
                {
                    int loc = 0;
                    bool fileexists = false;
                    int pos = fileName.LastIndexOf("\\") + 1;
                    string fn = fileName.Substring(pos, fileName.Length - pos);
                    for (int i = 0; i < lastfiles.Count; i++)
                    {
                        if (lastfiles[i].FileName == fn)
                        {
                            loc = i;
                            fileexists = true;
                            break;
                        }
                        else
                        {
                            string s = fn + " !- " + lastfiles[i].FileName;
                        }
                    }
                    if (!fileexists) { return DokanResult.FileNotFound; }
                    if (!InternalExecHandler.cachedfiles.ContainsKey(fileName.Replace(@"\", "")))
                    {
                        //call rclone cat which will spit out binary data to cachedfiles array
                        InternalExecHandler.Execute("cat", remoteName, fileName);
                    }

                    while (!InternalExecHandler.cachedfiles.ContainsKey(fileName.Replace(@"\", "")))
                    {
                        //wait for it to fill up
                        System.Threading.Thread.Sleep(1000);
                    }
                    fileName = fileName.Replace(@"\", "");
                    MemoryStream compare = InternalExecHandler.cachedfiles[fileName] as MemoryStream;

                    info.Context = compare;
                }         


                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
               DokanResult.Success);
            }
            else
            {
                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                DokanResult.FileNotFound);
            }

            
        }

        public void Cleanup(string fileName, DokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Console.WriteLine($"{nameof(Cleanup)}('{fileName}', {info} - entering");
#endif

            (info.Context as FileStream)?.Dispose();
            info.Context = null;

            if (info.DeleteOnClose)
            {
                if (info.IsDirectory)
                {
                    Directory.Delete(GetPath(fileName));
                }
                else
                {
                    File.Delete(GetPath(fileName));
                }
            }
            Trace(nameof(Cleanup), fileName, info, DokanResult.Success);
        }

        public void CloseFile(string fileName, DokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Console.WriteLine($"{nameof(CloseFile)}('{fileName}', {info} - entering");
#endif

            (info.Context as FileStream)?.Dispose();
            info.Context = null;
            Trace(nameof(CloseFile), fileName, info, DokanResult.Success);
                // could recreate cleanup code here but this is not called sometimes
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, DokanFileInfo info)
        {
            bytesRead = 0;
            if (info.Context == null || ignoredFile(fileName, info))
            {
                Console.WriteLine("file doesnt exist: " + fileName);
                bytesRead = 0;
                return Trace(nameof(ReadFile), fileName, info, DokanResult.FileNotFound, "out " + bytesRead.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                MemoryStream stream = info.Context as MemoryStream;
                lock (stream)
                {
                    stream.Position = offset;
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                }
                return Trace(nameof(ReadFile), fileName, info, DokanResult.Success, "out " + bytesRead.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
            }
            
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, DokanFileInfo info)
        {
            if (info.Context == null)
            {
                using (var stream = new FileStream(GetPath(fileName), FileMode.Open, System.IO.FileAccess.Write))
                {
                    stream.Position = offset;
                    stream.Write(buffer, 0, buffer.Length);
                    bytesWritten = buffer.Length;
                }
            }
            else
            {
                var stream = info.Context as FileStream;
                lock (stream) //Protect from overlapped write
                {
                    stream.Position = offset;
                    stream.Write(buffer, 0, buffer.Length);
                }
                bytesWritten = buffer.Length;
            }
            return Trace(nameof(WriteFile), fileName, info, DokanResult.Success, "out " + bytesWritten.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus FlushFileBuffers(string fileName, DokanFileInfo info)
        {
            try
            {
                ((FileStream) (info.Context)).Flush();
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.Success);
            }
            catch (IOException)
            {
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.DiskFull);
            }
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, DokanFileInfo info)
        {
            //set empty fileinfo
            fileInfo = new FileInformation{};
            //scan array for file
            int loc = 0;
            bool fileexists = false;
            int pos = fileName.LastIndexOf("\\") + 1;
            string fn = fileName.Substring(pos, fileName.Length - pos);
            for (int i = 0; i < lastfiles.Count; i++)
            {               
                if (lastfiles[i].FileName == fn)
                {
                    loc = i; 
                    fileInfo = lastfiles[loc];
                    fileexists = true;
                    break;
                }
                else
                {
                    string s = fn + " !- " + lastfiles[i].FileName;
                }
            }
            if (!fileexists)
            {
                var filePath = GetPath(fileName);
                FileSystemInfo finfo = new FileInfo(filePath);
                if (!finfo.Exists)
                    finfo = new DirectoryInfo(filePath);

                fileInfo = new FileInformation
                {
                    FileName = fileName,
                    Attributes = finfo.Attributes,
                    CreationTime = finfo.CreationTime,
                    LastAccessTime = finfo.LastAccessTime,
                    LastWriteTime = finfo.LastWriteTime,
                    Length = (finfo as FileInfo)?.Length ?? 0,
                };
            }

            //string s = null;

            return Trace(nameof(GetFileInformation), fileName, info, DokanResult.Success);
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, DokanFileInfo info)
        {
            // Return DokanResult.NotImplemented in FindFilesWithPattern

            lastfilename = fileName;

            files = new List<FileInformation>();
            if (fileName == @"\") { fileName = ""; }

            string[] returnedfiles = InternalExecHandler.Execute("lsd", remoteName, fileName).Split('\n');
            returnedfiles = returnedfiles.Take(returnedfiles.Count() - 1).ToArray();
            foreach (string item in returnedfiles)
            {
                var finfo = new FileInformation
                {
                    //organize stored/remote dir information
                    FileName = item.Replace(Regex.Match(item, @"([ \t]+)?(-)?[0-9]+ [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(.[0-9]+)?[ \t]+(-)?([0-9]+([ ]{1}))?").Value, ""),
                    Attributes = FileAttributes.Directory,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = null,
                    CreationTime = Convert.ToDateTime(Regex.Match(item, @"[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}").Value),
                };
                files.Add(finfo);
            }

            returnedfiles = InternalExecHandler.Execute("lsl", remoteName, fileName).Split('\n');
            returnedfiles = returnedfiles.Take(returnedfiles.Count() - 1).ToArray();
            foreach (string item in returnedfiles)
            {
                var finfo = new FileInformation
                {
                    //organize stored/remote file information
                    FileName = item.Replace(Regex.Match(item, @"([ \t]+)?(-)?[0-9]+ [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}(.[0-9]+)?[ \t]+(-)?([0-9]+([ ]{1}))?").Value, ""),
                    Attributes = FileAttributes.Normal,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = null,
                    CreationTime = Convert.ToDateTime(Regex.Match(item, @"[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}").Value),
                    Length = Convert.ToInt64(Regex.Match(item, @"([0-9])+").Value)
                };
                files.Add(finfo);
            }


            lastfiles = files;

            return Trace(nameof(FindFiles), fileName, info, DokanResult.Success);
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, DokanFileInfo info)
        {
            try
            {
                File.SetAttributes(GetPath(fileName), attributes);
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.Success, attributes.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.AccessDenied, attributes.ToString());
            }
            catch (FileNotFoundException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.FileNotFound, attributes.ToString());
            }
            catch (DirectoryNotFoundException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.PathNotFound, attributes.ToString());
            }
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
            DateTime? lastWriteTime, DokanFileInfo info)
        {
            try
            {
                var filePath = GetPath(fileName);
                if (creationTime.HasValue)
                    File.SetCreationTime(filePath, creationTime.Value);

                if (lastAccessTime.HasValue)
                    File.SetLastAccessTime(filePath, lastAccessTime.Value);

                if (lastWriteTime.HasValue)
                    File.SetLastWriteTime(filePath, lastWriteTime.Value);

                return Trace(nameof(SetFileTime), fileName, info, DokanResult.Success, creationTime, lastAccessTime,
                    lastWriteTime);
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileTime), fileName, info, DokanResult.AccessDenied, creationTime, lastAccessTime,
                    lastWriteTime);
            }
            catch (FileNotFoundException)
            {
                return Trace(nameof(SetFileTime), fileName, info, DokanResult.FileNotFound, creationTime, lastAccessTime,
                    lastWriteTime);
            }
        }

        public NtStatus DeleteFile(string fileName, DokanFileInfo info)
        {
            var filePath = GetPath(fileName);

            if (Directory.Exists(filePath))
                return Trace(nameof(DeleteFile), fileName, info, DokanResult.AccessDenied);

            if (!File.Exists(filePath))
                return Trace(nameof(DeleteFile), fileName, info, DokanResult.FileNotFound);

            if (File.GetAttributes(filePath).HasFlag(FileAttributes.Directory))
                return Trace(nameof(DeleteFile), fileName, info, DokanResult.AccessDenied);

            return Trace(nameof(DeleteFile), fileName, info, DokanResult.Success);
            // we just check here if we could delete the file - the true deletion is in Cleanup
        }

        public NtStatus DeleteDirectory(string fileName, DokanFileInfo info)
        {
            return Trace(nameof(DeleteDirectory), fileName, info,
                Directory.EnumerateFileSystemEntries(GetPath(fileName)).Any()
                    ? DokanResult.DirectoryNotEmpty
                    : DokanResult.Success);
            // if dir is not empty it can't be deleted
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, DokanFileInfo info)
        {
            var oldpath = GetPath(oldName);
            var newpath = GetPath(newName);

            (info.Context as FileStream)?.Dispose();
            info.Context = null;

            var exist = info.IsDirectory ? Directory.Exists(newpath) : File.Exists(newpath);

            try
            {

                if (!exist)
                {
                    info.Context = null;
                    if (info.IsDirectory)
                        Directory.Move(oldpath, newpath);
                    else
                        File.Move(oldpath, newpath);
                    return Trace(nameof(MoveFile), oldName, info, DokanResult.Success, newName,
                        replace.ToString(CultureInfo.InvariantCulture));
                }
                else if (replace)
                {
                    info.Context = null;

                    if (info.IsDirectory) //Cannot replace directory destination - See MOVEFILE_REPLACE_EXISTING
                        return Trace(nameof(MoveFile), oldName, info, DokanResult.AccessDenied, newName,
                            replace.ToString(CultureInfo.InvariantCulture));

                    File.Delete(newpath);
                    File.Move(oldpath, newpath);
                    return Trace(nameof(MoveFile), oldName, info, DokanResult.Success, newName,
                        replace.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(MoveFile), oldName, info, DokanResult.AccessDenied, newName,
                    replace.ToString(CultureInfo.InvariantCulture));
            }
            return Trace(nameof(MoveFile), oldName, info, DokanResult.FileExists, newName,
                replace.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetEndOfFile(string fileName, long length, DokanFileInfo info)
        {
            try
            {
                ((FileStream) (info.Context)).SetLength(length);
                return Trace(nameof(SetEndOfFile), fileName, info, DokanResult.Success,
                    length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(SetEndOfFile), fileName, info, DokanResult.DiskFull,
                    length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus SetAllocationSize(string fileName, long length, DokanFileInfo info)
        {
            try
            {
                ((FileStream) (info.Context)).SetLength(length);
                return Trace(nameof(SetAllocationSize), fileName, info, DokanResult.Success,
                    length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(SetAllocationSize), fileName, info, DokanResult.DiskFull,
                    length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus LockFile(string fileName, long offset, long length, DokanFileInfo info)
        {
            try
            {
                ((FileStream) (info.Context)).Lock(offset, length);
                return Trace(nameof(LockFile), fileName, info, DokanResult.Success,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(LockFile), fileName, info, DokanResult.AccessDenied,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, DokanFileInfo info)
        {
            try
            {
                ((FileStream) (info.Context)).Unlock(offset, length);
                return Trace(nameof(UnlockFile), fileName, info, DokanResult.Success,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(UnlockFile), fileName, info, DokanResult.AccessDenied,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus GetDiskFreeSpace(out long free, out long total, out long used, DokanFileInfo info)
        {
            used = 1000000000000000;
            free = 1000000000000000;
            total = 1000000000000000;
            return Trace(nameof(GetDiskFreeSpace), null, info, DokanResult.Success, "out " + free.ToString(),
                "out " + total.ToString(), "out " + used.ToString());
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
            out string fileSystemName, DokanFileInfo info)
        {
            volumeLabel = "DOKAN_RCLONE";
            fileSystemName = "NTFS";

            features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.CaseSensitiveSearch |
                       FileSystemFeatures.PersistentAcls | FileSystemFeatures.SupportsRemoteStorage |
                       FileSystemFeatures.UnicodeOnDisk;

            return Trace(nameof(GetVolumeInformation), null, info, DokanResult.Success, "out " + volumeLabel,
                "out " + features.ToString(), "out " + fileSystemName);
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections,
            DokanFileInfo info)
        {
            try
            {
                //get user
                SecurityIdentifier identity = WindowsIdentity.GetCurrent().Owner;
                //check if file is directory or not and set appropriate security method
                security = !info.IsDirectory ? (FileSystemSecurity)new FileSecurity() : new DirectorySecurity();
                //add access rule for full control (maybe make read only in future)
                security.SetAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
                //disable protection and preserve inheritance
                security.SetAccessRuleProtection(false, true);
                //done
                return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.Success, sections.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                security = null;
                return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.AccessDenied, sections.ToString());
            }
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
            DokanFileInfo info)
        {
            try
            {
                if (info.IsDirectory)
                {
                    Directory.SetAccessControl(GetPath(fileName), (DirectorySecurity) security);
                }
                else
                {
                    File.SetAccessControl(GetPath(fileName), (FileSecurity) security);
                }
                return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.Success, sections.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.AccessDenied, sections.ToString());
            }
        }

        public NtStatus Mounted(DokanFileInfo info)
        {
            return Trace(nameof(Mounted), null, info, DokanResult.Success);
        }

        public NtStatus Unmounted(DokanFileInfo info)
        {
            return Trace(nameof(Unmounted), null, info, DokanResult.Success);
        }

        public NtStatus FindStreams(string fileName, IntPtr enumContext, out string streamName, out long streamSize,
            DokanFileInfo info)
        {
            streamName = string.Empty;
            streamSize = 0;
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented, enumContext.ToString(),
                "out " + streamName, "out " + streamSize.ToString());
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, DokanFileInfo info)
        {
            streams = new FileInformation[0];
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented);
        }

        public IList<FileInformation> FindFilesHelper(string fileName, string searchPattern)
        {
            IList<FileInformation> files = new DirectoryInfo(GetPath(fileName))
                .GetFileSystemInfos(searchPattern)
                .Select(finfo => new FileInformation
                {
                    Attributes = finfo.Attributes,
                    CreationTime = finfo.CreationTime,
                    LastAccessTime = finfo.LastAccessTime,
                    LastWriteTime = finfo.LastWriteTime,
                    Length = (finfo as FileInfo)?.Length ?? 0,
                    FileName = finfo.Name
                }).ToArray();

            return files;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
            DokanFileInfo info)
        {
            files = null;

            return Trace(nameof(FindFilesWithPattern), fileName, info, DokanResult.NotImplemented);
        }

        public bool ignoredFile(string fileName, DokanFileInfo info)
        {
            if (fileName == "\\")
            {
                return true;
            }
            else if (fileName.ToLower().Contains("desktop.ini") || fileName.ToLower().Contains("autorun.inf"))
            {
                return true;
            }
            else if (info.IsDirectory)
            {
                return true;
            }
            else
            {
                return false;
            }
        }


        #endregion Implementation of IDokanOperations
    }
}