using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ICSharpCode.SharpZipLib.Checksums;
using ICSharpCode.SharpZipLib.Zip;
using wyUpdate.Common;
using wyUpdate.Compression.Vcdiff;

namespace wyUpdate
{
    class InstallUpdate
    {
        #region Private Variable

        public ContainerControl Sender = null;

        public Delegate SenderDelegate = null;

        //Used for unzipping
        public string Filename;
        public string OutputDirectory;

        //Backupfiles
        public string TempDirectory;
        public string ProgramDirectory;

        //Modify registry, executing/optimizing files
        public UpdateDetails UpdtDetails = null;

        //for self update
        private string m_NewIUPClientLoc = "";
        public string OldIUPClientLoc = "";

        //for writing the client data file

        public ClientFileType ClientFileType;

        public UpdateEngine ClientFile = null;

        public bool SkipProgressReporting = false;


        //cancellation & pausing
        private volatile bool canceled = false;
        //private volatile bool paused = false;
        #endregion Private Variables


        #region Constructors

        //Uninstalling contructor
        public InstallUpdate(string clientFileLoc, Delegate senderDelegate, ContainerControl sender)
        {
            Filename = clientFileLoc;
            Sender = sender;
            SenderDelegate = senderDelegate;
        }

        // Constructor for backing up files, closing processes, and replacing files.
        public InstallUpdate(string tempDir, string programDir, Delegate senderDelegate, ContainerControl sender)
        {
            TempDirectory = tempDir;
            ProgramDirectory = programDir;
            Sender = sender;
            SenderDelegate = senderDelegate;
        }

        // Constructor for unziping files.
        public InstallUpdate(string filename, string outputDirectory, ContainerControl sender, Delegate senderDelegate)
        {
            Sender = sender;
            SenderDelegate = senderDelegate;
            OutputDirectory = outputDirectory;
            Filename = filename;
        }

        #endregion Constructors

        public const int TotalUpdateSteps = 7;

        public static int GetRelativeProgess(int stepOn, int stepProgress)
        {
            return ((stepOn * 100) / TotalUpdateSteps) + (stepProgress / (TotalUpdateSteps));
        }

        //Methods
        private void ExtractUpdateFile()
        {
            int totalFiles = 0;
            int filesDone = 0;

            ZipInputStream tempS = new ZipInputStream(File.OpenRead(Filename));
            ZipEntry tempEntry;

            while ((tempEntry = tempS.GetNextEntry()) != null)
            {
                totalFiles++;
            }

            tempS.Close();

            using (ZipInputStream s = new ZipInputStream(File.OpenRead(Filename)))
            {
                ZipEntry theEntry;

                while ((theEntry = s.GetNextEntry()) != null)
                {
                    if (!SkipProgressReporting)
                    {
                        ThreadHelper.ReportProgress(Sender, SenderDelegate,
                            "Extracting " + Path.GetFileName(theEntry.Name),
                            totalFiles > 0 ?
                               GetRelativeProgess(1, (int)((filesDone * 100) / totalFiles)) :
                               GetRelativeProgess(1, 0));

                        filesDone++;
                    }


                    string directoryName = Path.Combine(OutputDirectory, Path.GetDirectoryName(theEntry.Name));
                    string fileName = Path.GetFileName(theEntry.Name);

                    // create directory
                    if (directoryName.Length > 0)
                    {
                        Directory.CreateDirectory(directoryName);
                    }

                    if (fileName != String.Empty)
                    {
                        using (FileStream streamWriter = File.Create(Path.Combine(directoryName, fileName)))
                        {

                            int size = 2048;
                            byte[] data = new byte[2048];
                            do
                            {
                                if (canceled)
                                    break; //stop decompressing file

                                //read compressed data
                                size = s.Read(data, 0, data.Length);

                                //write to uncompressed file
                                streamWriter.Write(data, 0, size);

                            } while (size > 0);
                        }

                        if (canceled)
                            break; //stop outputting new files

                        File.SetLastWriteTime(Path.Combine(directoryName, fileName), theEntry.DateTime);
                    }
                }
            }//end using(ZipInputStream ... )
        }

        private void UpdateRegistry(ref List<RegChange> rollbackRegistry)
        {
            int i = 0;

            ThreadHelper.ReportProgress(Sender, SenderDelegate, string.Empty, GetRelativeProgess(5, 0));

            foreach (RegChange change in UpdtDetails.RegistryModifications)
            {
                if (canceled)
                    break;

                i++;

                ThreadHelper.ReportProgress(Sender, SenderDelegate,
                    change.ToString(),
                    GetRelativeProgess(5, (int)((i * 100) / UpdtDetails.RegistryModifications.Count)));
                
                //execute the regChange, while storing the opposite operation
                change.ExecuteOperation(rollbackRegistry);
            }
        }

        private void UpdateFiles(string tempDir, string progDir, string backupFolder, List<FileFolder> rollbackList, ref int totalDone, ref int totalFiles)
        {
            DirectoryInfo tempDirInf = new DirectoryInfo(tempDir);

            //create an array of files using FileInfo object
            //get all files for the current directory
            FileInfo[] tempFiles = tempDirInf.GetFiles("*");


            for (int i = 0; i < tempFiles.Length; i++)
            {
                if (canceled)
                    break;

                ThreadHelper.ReportProgress(Sender, SenderDelegate, 
                    "Updating " + tempFiles[i].Name,
                    GetRelativeProgess(4, (int)((totalDone * 100) / totalFiles)));

                if (File.Exists(Path.Combine(progDir, tempFiles[i].Name)))
                {
                    //backup
                    File.Copy(Path.Combine(progDir, tempFiles[i].Name), Path.Combine(backupFolder, tempFiles[i].Name), true);

                    //replace
                    File.Copy(tempFiles[i].FullName, Path.Combine(progDir, tempFiles[i].Name), true);
                    
                    //Old method (didn't work on Win 98/ME):
                    //File.Replace(tempFiles[i].FullName, Path.Combine(progDir, tempFiles[i].Name), Path.Combine(backupFolder, tempFiles[i].Name));
                }
                else
                {
                    //move file
                    File.Move(tempFiles[i].FullName, Path.Combine(progDir, tempFiles[i].Name));

                    //add filename to "rollback" list
                    rollbackList.Add(new FileFolder(Path.Combine(progDir, tempFiles[i].Name)));
                }

                //update % done
                totalDone++;
            }

            if (canceled)
                return;

            DirectoryInfo[] tempDirs = tempDirInf.GetDirectories("*");
            string newProgDir;

            for (int i = 0; i < tempDirs.Length; i++)
            {
                if (canceled)
                    break;

                newProgDir = Path.Combine(progDir, tempDirs[i].Name);

                if (!Directory.Exists(newProgDir))
                {
                    //create the prog subdirectory (no backup folder needed)
                    Directory.CreateDirectory(newProgDir);

                    //add to "rollback" list
                    rollbackList.Add(new FileFolder(newProgDir, true));
                }
                else
                {
                    //prog subdirectory exists, create a backup folder
                    Directory.CreateDirectory(Path.Combine(backupFolder, tempDirs[i].Name));
                }

                //backup all of the files in that directory
                UpdateFiles(tempDirs[i].FullName, newProgDir, Path.Combine(backupFolder, tempDirs[i].Name), rollbackList, ref totalDone, ref totalFiles);
            }
        }

        /// <summary>
        /// Checks a list of files to see if they are running, and returns 1 if they are.
        /// </summary>
        /// <param name="procs">list of filenames (full paths)</param>
        /// <returns>1 if processes need to be close, 0 if none exist</returns>
        private bool ProcessesNeedClosing(FileInfo[] baseFiles)
        {
            System.Diagnostics.Process[] aProcess = System.Diagnostics.Process.GetProcesses();

            bool ProcNeedClosing = false;

            foreach (System.Diagnostics.Process proc in aProcess)
            {
                foreach (FileInfo filename in baseFiles)
                {
                    try
                    {
                        //are one of the exe's in baseDir running?
                        if (proc.MainModule.FileName.ToLower() == filename.FullName.ToLower())
                        {
                            ProcNeedClosing = true;
                        }
                    }
                    catch (Exception) { }
                }
            }

            return ProcNeedClosing;
        }

        private void KillProcess(string filename)
        {
            Process[] aProcess = Process.GetProcesses();

            foreach (Process proc in aProcess)
            {
                try
                {
                    if (proc.MainModule.FileName.ToLower() == filename.ToLower())
                    {
                        proc.Kill();
                    }
                }
                catch (Exception) { }
            }
        }

        // unzip the update to the temp folder
        public void RunUnzipProcess()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            Exception except = null;

            string updtDetailsFilename = Path.Combine(TempDirectory, "updtdetails.udt");

            try
            {
                ExtractUpdateFile();

                try
                {
                    // remove update file (it's no longer needed)
                    File.Delete(Filename);
                }
                catch (Exception) { }


                // Try to load the update details file

                if (File.Exists(updtDetailsFilename))
                {
                    UpdtDetails = new UpdateDetails();
                    UpdtDetails.Load(updtDetailsFilename);
                }

                if (Directory.Exists(Path.Combine(TempDirectory, "patches")))
                {
                    string tempFilename;

                    // patch the files
                    foreach (UpdateFile file in UpdtDetails.UpdateFiles)
                    {
                        if (file.DeltaPatchRelativePath != null)
                        {
                            tempFilename = Path.Combine(TempDirectory, file.RelativePath);

                            // create the directory to store the patched file
                            if (!Directory.Exists(Path.GetDirectoryName(tempFilename)))
                                Directory.CreateDirectory(Path.GetDirectoryName(tempFilename));

                            using (FileStream original = File.OpenRead(FixUpdateDetailsPaths(file.RelativePath)))
                            using (FileStream patch = File.OpenRead(Path.Combine(TempDirectory, file.DeltaPatchRelativePath)))
                            using (FileStream target = File.Open(tempFilename, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                            {
                                VcdiffDecoder.Decode(original, patch, target);
                            }

                            // the 'last write time' of the patch file is really the 'lwt' of the dest. file
                            File.SetLastWriteTime(tempFilename, File.GetLastWriteTime(Path.Combine(TempDirectory, file.DeltaPatchRelativePath)));

                            // verify the file has bee patched correctly, if not throw an exception
                            if (GetAdler32(tempFilename) != file.NewFileAdler32)
                                throw new PatchApplicationException("Patch failed to apply to " + FixUpdateDetailsPaths(file.RelativePath));
                        }
                    }


                    try
                    {
                        // remove the patches directory (frees up a bit of space)
                        Directory.Delete(Path.Combine(TempDirectory, "patches"), true);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                except = ex;
            }


            if (canceled || except != null)
            {
                //report cancellation
                ThreadHelper.ReportProgress(Sender, SenderDelegate, "Cancelling update...", -1);

                //Delete temporary files

                if (except.GetType() != typeof(PatchApplicationException))
                {
                    // remove the entire temp directory
                    try
                    {
                        Directory.Delete(OutputDirectory, true);
                    }
                    catch (Exception) { }
                }
                else
                {
                    //only 'gut' the folder leaving the server file

                    string[] dirs = Directory.GetDirectories(TempDirectory);

                    foreach (string dir in dirs)
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch (Exception) { }
                    }

                    // remove the update details
                    if (File.Exists(updtDetailsFilename))
                    {
                        File.Delete(updtDetailsFilename);
                    }
                }

                ThreadHelper.ReportError(Sender, SenderDelegate, string.Empty, except);
            }
            else
            {
                ThreadHelper.ReportSuccess(Sender, SenderDelegate, "Extraction complete");
            }
        }

        public void RunUpdateFiles()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            //check if folders exist, and count files to be moved
            string backupFolder = Path.Combine(TempDirectory, "backup");
            string[] backupFolders = new string[6];
            string[] origFolders = { "base", "system", "appdata", "comappdata", "comdesktop", "comstartmenu" };
            string[] destFolders = { ProgramDirectory, 
                Environment.GetFolderPath(Environment.SpecialFolder.System), 
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                SystemFolders.CommonAppData, 
                SystemFolders.CommonDesktop, 
                SystemFolders.CommonProgramsStartMenu };


            List<FileFolder> rollbackList = new List<FileFolder>();
            int totalDone = 0;

            Exception except = null; // store any errors

            try
            {
                int totalFiles = 0;
                //count the files and create backup folders
                for (int i = 0; i < origFolders.Length; i++)
                {
                    //does orig folder exist?
                    if (Directory.Exists(Path.Combine(TempDirectory, origFolders[i])))
                    {
                        //orig folder exists, set backup & orig folder locations
                        backupFolders[i] = Path.Combine(backupFolder, origFolders[i]);
                        origFolders[i] = Path.Combine(TempDirectory, origFolders[i]);
                        Directory.CreateDirectory(backupFolders[i]);

                        //delete "newer" client, if it will overwrite this client
                        deleteClientInPath(destFolders[i], origFolders[i]);

                        //count the total files
                        totalFiles += CountFiles(origFolders[i]);
                    }
                }


                //run the backup & replace
                for (int i = 0; i < origFolders.Length; i++)
                {
                    if (canceled)
                        break;

                    if (backupFolders[i] != null) //if the backup folder exists
                    {
                        UpdateFiles(origFolders[i], destFolders[i], backupFolders[i], rollbackList, ref totalDone, ref totalFiles);
                    }
                }

                DeleteFilesAndInstallShortcuts(destFolders, backupFolder, rollbackList);
            }
            catch (Exception ex)
            {
                except = ex;
            }

            //write the list of newly created files and folders
            RollbackUpdate.WriteRollbackFiles(Path.Combine(backupFolder, "fileList.bak"), rollbackList);

            if (canceled || except != null)
            {
                RollbackUpdate.RollbackFiles(TempDirectory, ProgramDirectory);

                ThreadHelper.ReportError(Sender, SenderDelegate, string.Empty, except);
            }
            else
            {
                //backup & replace was successful
                ThreadHelper.ReportSuccess(Sender, SenderDelegate, string.Empty);
            }
        }

        private void DeleteFilesAndInstallShortcuts(string[] destFolders, string backupFolder, List<FileFolder> rollbackList)
        {
            bool installDesktopShortcut = true, installStartMenuShortcut = true;

            //see if at least one previous shortcut on the desktop exists
            foreach (string shortcut in UpdtDetails.PreviousDesktopShortcuts)
            {
                if (File.Exists(Path.Combine(destFolders[4], shortcut.Substring(11))))
                {
                    installDesktopShortcut = true;
                    break;
                }
                else
                    installDesktopShortcut = false;
            }

            //see if at least one previous shortcut in the start menu folder exists
            foreach (string shortcut in UpdtDetails.PreviousSMenuShortcuts)
            {
                if (File.Exists(Path.Combine(destFolders[5], shortcut.Substring(13))))
                {
                    installStartMenuShortcut = true;
                    break;
                }
                else
                    installStartMenuShortcut = false;
            }

            string tempPath, tempFile;

            // delete the marked files
            foreach (UpdateFile file in UpdtDetails.UpdateFiles)
            {
                if (file.DeleteFile)
                {
                    tempPath = Path.Combine(backupFolder, file.RelativePath.Substring(0, file.RelativePath.LastIndexOf('\\')));

                    // check if the backup folder exists (create it if not)
                    if (!Directory.Exists(tempPath))
                        Directory.CreateDirectory(tempPath);

                    tempFile = FixUpdateDetailsPaths(file.RelativePath);

                    if (File.Exists(tempFile))
                    {
                        //backup the file
                        File.Copy(tempFile, Path.Combine(tempPath, Path.GetFileName(tempFile)));

                        //delete the file
                        File.Delete(tempFile);
                    }
                }
            }

            //delete empty folders by working backwords to kill nested folders, e.g.:
            //  MyFolder\
            //  MyFolder\Sub1\
            //  MyFolder\Sub2\
            for (int i = UpdtDetails.FoldersToDelete.Count - 1; i >= 0; i--)
            {
                tempPath = FixUpdateDetailsPaths(UpdtDetails.FoldersToDelete[i]);

                try
                {
                    // only recursively delete StartMenu subdirectories when they're not empty
                    // otherwise the folder has to be empty to be deleted

                    Directory.Delete(tempPath, UpdtDetails.FoldersToDelete[i].StartsWith("coms"));

                    rollbackList.Add(new FileFolder(tempPath, false));
                }
                catch (Exception) { }
            }

            // create the shortcuts
            for (int i = 0; i < UpdtDetails.ShortcutInfos.Count; i++)
            {
                //get the first 4 letters of the shortcut's path
                tempFile = UpdtDetails.ShortcutInfos[i].RelativeOuputPath.Substring(0, 4);

                //if we can't install to that folder then continue to the next shortcut
                if (tempFile == "comd" && !installDesktopShortcut
                    || tempFile == "coms" && !installStartMenuShortcut)
                {
                    continue;
                }

                tempFile = FixUpdateDetailsPaths(UpdtDetails.ShortcutInfos[i].RelativeOuputPath);

                // see if the shortcut already exists
                if (File.Exists(tempFile))
                {
                    tempPath = Path.Combine(backupFolder, UpdtDetails.ShortcutInfos[i].RelativeOuputPath.Substring(0, UpdtDetails.ShortcutInfos[i].RelativeOuputPath.LastIndexOf('\\')));

                    // check if the backup folder exists (create it if not)
                    if (!Directory.Exists(tempPath))
                        Directory.CreateDirectory(tempPath);

                    // backup the existing shortcut
                    File.Copy(tempFile, Path.Combine(tempPath, Path.GetFileName(tempFile)), true);

                    // delete the shortcut
                    File.Delete(tempFile);
                }
                else
                    //add file to "rollback" list
                    rollbackList.Add(new FileFolder(tempFile));

                tempPath = Path.GetDirectoryName(tempFile);

                //if the folder doesn't exist
                if (!Directory.Exists(tempPath))
                {
                    //create the directory
                    Directory.CreateDirectory(tempPath);

                    //add to the rollback list
                    rollbackList.Add(new FileFolder(tempPath, true));
                }

                ShellShortcut shellShortcut = new ShellShortcut(tempFile);
                shellShortcut.Path = ParseText(UpdtDetails.ShortcutInfos[i].Path);
                shellShortcut.WorkingDirectory = ParseText(UpdtDetails.ShortcutInfos[i].WorkingDirectory);
                shellShortcut.WindowStyle = UpdtDetails.ShortcutInfos[i].WindowStyle;
                shellShortcut.Description = UpdtDetails.ShortcutInfos[i].Description;
                //shellShortcut.IconPath
                //shellShortcut.IconIndex = 0;
                shellShortcut.Save();
            }
        }

        private long GetAdler32(string fileName)
        {
            Adler32 adler = new Adler32();
            int totalComplete = 0;
            long fileSize = new FileInfo(fileName).Length;
            int sourceBytes;
            adler.Reset();

            FileStream fs = new FileStream(fileName, FileMode.Open);
            byte[] buffer = new byte[4096];

            do
            {
                sourceBytes = fs.Read(buffer, 0, buffer.Length);
                totalComplete += sourceBytes;

                adler.Update(buffer, 0, sourceBytes);

                // break on cancel
                if (canceled)
                    break;

            } while (sourceBytes > 0);

            fs.Close();

            return adler.Value;
        }

        //count files in the directory and subdirectories
        private static int CountFiles(string directory)
        {
            return new DirectoryInfo(directory).GetFiles("*", SearchOption.AllDirectories).Length;
        }


        public void RunSelfUpdate()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            Exception except = null;

            try
            {
                //extract downloaded self update
                ExtractUpdateFile();

                try
                {
                    // remove update file (it's no longer needed)
                    File.Delete(Filename);
                }
                catch (Exception) { }


                //find and forcibly close oldClientLocation
                KillProcess(OldIUPClientLoc);

                string updtDetailsFilename = Path.Combine(OutputDirectory, "updtdetails.udt");

                if (File.Exists(updtDetailsFilename))
                {
                    UpdtDetails = new UpdateDetails();
                    UpdtDetails.Load(updtDetailsFilename);

                    //remove the file to prevent conflicts with the regular product update
                    File.Delete(updtDetailsFilename);
                }



                // generate files from patches

                if (Directory.Exists(Path.Combine(OutputDirectory, "patches")))
                {
                    // set the base directory to the home of the client file
                    ProgramDirectory = Path.GetDirectoryName(OldIUPClientLoc);
                    TempDirectory = OutputDirectory;

                    string tempFilename;

                    // patch the file (assume only one - wyUpdate.exe)

                    if (UpdtDetails.UpdateFiles[0].DeltaPatchRelativePath != null)
                    {
                        tempFilename = Path.Combine(TempDirectory, UpdtDetails.UpdateFiles[0].RelativePath);

                        // create the directory to store the patched file
                        if (!Directory.Exists(Path.GetDirectoryName(tempFilename)))
                            Directory.CreateDirectory(Path.GetDirectoryName(tempFilename));

                        using (FileStream original = File.OpenRead(OldIUPClientLoc))
                        using (FileStream patch = File.OpenRead(Path.Combine(TempDirectory, UpdtDetails.UpdateFiles[0].DeltaPatchRelativePath)))
                        using (FileStream target = File.Open(tempFilename, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                        {
                            VcdiffDecoder.Decode(original, patch, target);
                        }

                        // the 'last write time' of the patch file is really the 'lwt' of the dest. file
                        File.SetLastWriteTime(tempFilename, File.GetLastWriteTime(Path.Combine(TempDirectory, UpdtDetails.UpdateFiles[0].DeltaPatchRelativePath)));

                        // verify the file has bee patched correctly, if not throw an exception
                        if (GetAdler32(tempFilename) != UpdtDetails.UpdateFiles[0].NewFileAdler32)
                            throw new PatchApplicationException("Patch failed to apply to " + FixUpdateDetailsPaths(UpdtDetails.UpdateFiles[0].RelativePath));
                    }


                    try
                    {
                        // remove the patches directory (frees up a bit of space)
                        Directory.Delete(Path.Combine(TempDirectory, "patches"), true);
                    }
                    catch (Exception) { }
                }




                //find self in Path.Combine(OutputDirectory, "base")
                bool optimize = FindNewClient();


                //transfer new client to the directory (Note: this assumes a standalone client - i.e. no dependencies)
                File.Copy(m_NewIUPClientLoc, OldIUPClientLoc, true);

                //Optimize client if necessary
                if (optimize)
                    NGenInstall(OldIUPClientLoc);

                //cleanup the client update files to prevent conflicts with the product update
                File.Delete(m_NewIUPClientLoc);
                Directory.Delete(Path.Combine(OutputDirectory, "base"));
            }
            catch (Exception ex)
            {
                except = ex;
            }

            if (canceled || except != null)
            {
                //report cancellation
                ThreadHelper.ReportProgress(Sender, SenderDelegate, "Cancelling update...", -1);

                //Delete temporary files
                if (except.GetType() != typeof(PatchApplicationException))
                {
                    // remove the entire temp directory
                    try
                    {
                        Directory.Delete(OutputDirectory, true);
                    }
                    catch (Exception) { }
                }
                else
                {
                    //only 'gut' the folder leaving the server file

                    string[] dirs = Directory.GetDirectories(TempDirectory);

                    foreach (string dir in dirs)
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                        }
                        catch (Exception) { }
                    }
                }

                ThreadHelper.ReportError(Sender, SenderDelegate, string.Empty, except);
            }
            else
            {
                ThreadHelper.ReportSuccess(Sender, SenderDelegate, "Self update complete");
            }
        }

        private bool FindNewClient()
        {
            //first search the update details file
            for (int i = 0; i < UpdtDetails.UpdateFiles.Count; i++)
            {
                if (UpdtDetails.UpdateFiles[i].IsNETAssembly)
                {
                    //optimize (ngen) the file
                    m_NewIUPClientLoc = Path.Combine(OutputDirectory, UpdtDetails.UpdateFiles[i].RelativePath);

                    return true;
                }
            }

            //not found yet, so keep searching
            //get a list of files in the "base" folder
            string[] files = Directory.GetFiles(Path.Combine(OutputDirectory, "base"), "*.exe", SearchOption.AllDirectories);

            if (files.Length >= 1)
            {
                m_NewIUPClientLoc = files[0];
            }
            else
            {
                throw new Exception("Self update client couldn't be found.");
            }

            //not ngen-able
            return false;
        }


        public void RunUpdateRegistry()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            string backupFolder = Path.Combine(TempDirectory, "backup");
            List<RegChange> rollbackRegistry = new List<RegChange>();

            //parse variables in the regChanges
            for (int i = 0; i < UpdtDetails.RegistryModifications.Count; i++)
                UpdtDetails.RegistryModifications[i] = ParseRegChange(UpdtDetails.RegistryModifications[i]);

            Exception except = null;
            try
            {
                UpdateRegistry(ref rollbackRegistry);
            }
            catch (Exception ex)
            {
                except = ex;
            }

            RollbackUpdate.WriteRollbackRegistry(Path.Combine(backupFolder, "regList.bak"), ref rollbackRegistry);

            if (canceled || except != null)
            {
                RollbackUpdate.RollbackRegistry(TempDirectory, ProgramDirectory);
                ThreadHelper.ReportError(Sender, SenderDelegate, string.Empty, except);
            }
            else
            {
                //registry modification completed sucessfully
                ThreadHelper.ReportSuccess(Sender, SenderDelegate, string.Empty);
            }
        }

        public void RunUpdateClientDataFile()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon
            try
            {
                OutputDirectory = Path.Combine(TempDirectory, "ClientData");
                Directory.CreateDirectory(OutputDirectory);

                string oldClientFile = null;

                // see if a 1.1+ client file exists (client.wyc)
                if (ClientFileType != ClientFileType.Final
                    && File.Exists(Path.Combine(Path.GetDirectoryName(Filename), "client.wyc")))
                {
                    oldClientFile = Filename;
                    Filename = Path.Combine(Path.GetDirectoryName(Filename), "client.wyc");
                    ClientFileType = ClientFileType.Final;
                }


                if (ClientFileType == ClientFileType.PreRC2)
                {
                    //convert pre-RC2 client file by saving images to disk
                    string tempImageFilename;

                    //create the top image
                    if (ClientFile.TopImage != null)
                    {
                        ClientFile.TopImageFilename = "t.png";

                        tempImageFilename = Path.Combine(OutputDirectory, "t.png");
                        ClientFile.TopImage.Save(tempImageFilename, System.Drawing.Imaging.ImageFormat.Png);
                    }

                    //create the side image
                    if (ClientFile.SideImage != null)
                    {
                        ClientFile.SideImageFilename = "s.png";

                        tempImageFilename = Path.Combine(OutputDirectory, "s.png");
                        ClientFile.SideImage.Save(tempImageFilename, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                else
                {
                    //Extract the contents of the client data file
                    ExtractUpdateFile();

                    if (File.Exists(Path.Combine(OutputDirectory, "iuclient.iuc")))
                    {
                        // load and merge the existing file

                        UpdateEngine tempClientFile = new UpdateEngine();
                        tempClientFile.LoadClientData(Path.Combine(OutputDirectory, "iuclient.iuc"));
                        tempClientFile.InstalledVersion = ClientFile.InstalledVersion;
                        ClientFile = tempClientFile;
                    

                        File.Delete(Path.Combine(OutputDirectory, "iuclient.iuc"));
                    }
                }

                List<UpdateFile> updateDetailsFiles = UpdtDetails.UpdateFiles;

                FixUpdateFilesPaths(updateDetailsFiles);


                //write the uninstall file
                RollbackUpdate.WriteUninstallFile(Path.Combine(OutputDirectory, "uninstall.dat"), 
                    Path.Combine(TempDirectory, "backup\\regList.bak"),
                    Path.Combine(TempDirectory, "backup\\fileList.bak"), 
                    updateDetailsFiles);

                List<UpdateFile> files = new List<UpdateFile>();
                
                //add all the files in the outputDirectory
                AddFiles(OutputDirectory.Length + 1, OutputDirectory, ref files);

                //recompress all the client data files
                string tempClient = Path.Combine(OutputDirectory, "client.file");
                ClientFile.SaveClientFile(files, tempClient);

                //replace the original
                File.Copy(tempClient, Filename, true);


                if (oldClientFile != null)
                {
                    // delete the old client file
                    File.Delete(oldClientFile);
                }
            }
            catch (Exception) { }

            ThreadHelper.ReportSuccess(Sender, SenderDelegate, string.Empty);
        }

        //creates list of files to add to client data file
        private void AddFiles(int charsToTrim, string dir, ref List<UpdateFile> files)
        {
            string[] filenames = Directory.GetFiles(dir);
            string[] dirs = Directory.GetDirectories(dir);

            foreach (string file in filenames)
            {
                files.Add(new UpdateFile(file, file.Substring(charsToTrim), false));
            }

            foreach (string directory in dirs)
            {
                AddFiles(charsToTrim, directory, ref files);
            }
        }


        public void RunDeleteTemporary()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            try
            {
                //delete the temp directory
                Directory.Delete(TempDirectory, true);
            }
            catch (Exception) { }

            ThreadHelper.ReportSuccess(Sender, SenderDelegate, string.Empty);
        }


        public void RunUninstall()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            List<UninstallFileInfo> filesToUninstall = new List<UninstallFileInfo>();
            List<string> foldersToDelete = new List<string>();

            List<RegChange> registryToDelete = new List<RegChange>();

            //Load the list of files, folders etc. from the client file (Filename)
            RollbackUpdate.ReadUninstallData(Filename, ref filesToUninstall, ref foldersToDelete, ref registryToDelete);

            //uninstall files
            foreach (UninstallFileInfo file in filesToUninstall)
            {
                try
                {
                    if (file.UnNGENFile)
                        NGenUninstall(file.Path);

                    if (file.DeleteFile)
                        File.Delete(file.Path);
                }
                catch (Exception) { }
            }

            //uninstall folders
            for (int i = foldersToDelete.Count-1; i >= 0; i--)
            {
                //delete the last folder first (this fixes the problem of nested folders)
                try
                {
                    //directory must be empty in order to delete it
                    Directory.Delete(foldersToDelete[i]);
                }
                catch (Exception) { }
            }


            //tell the sender that we're uninstalling reg now:
            Sender.BeginInvoke(SenderDelegate, new object[] { 0, 1, "", null });

            //uninstall registry
            foreach (RegChange reg in registryToDelete)
            {
                reg.ExecuteOperation();
            }

            //All done
            Sender.BeginInvoke(SenderDelegate, new object[] { 0, 2, "", null });
        }


        public void RunProcessesCheck()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            FileInfo[] files = new DirectoryInfo(ProgramDirectory).GetFiles("*.exe", SearchOption.AllDirectories);

            RemoveSelfFromProcesses(ref files);

            //check for (and delete) a newer client if it exists
            deleteClientInPath(ProgramDirectory, Path.Combine(TempDirectory, "base"));

            bool procNeedClosing = ProcessesNeedClosing(files);

            if (!procNeedClosing)
            {
                //no processes need closing, all done
                files = null;
            }

            Sender.BeginInvoke(SenderDelegate, new object[] { files, true });
        }

        private void RemoveSelfFromProcesses(ref FileInfo[] files)
        {
            int offset = 0;

            for (int i = 0; i < files.Length; i++)
            {
                if (ProcessIsSelf(files[i].FullName))
                {
                    offset++;
                }
                else if (offset > 0)
                {
                    files[i - offset] = files[i];
                }
            }

            if (offset > 0)
                Array.Resize(ref files, files.Length - offset);
        }

        public static bool ProcessIsSelf(string processPath)
        {
            string self = Assembly.GetExecutingAssembly().Location,
                vhostFile = self.Substring(0, self.Length - 3) + "vshost.exe"; //for debugging

            if (processPath.ToLower() == self.ToLower() || processPath.ToLower() == vhostFile.ToLower())
                return true;
            else
                return false;
        }


        public void RunPreExecute()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            ProcessStartInfo psi = null;

            // simply update the progress bar to show the 3rd step is entirely complete
            ThreadHelper.ReportProgress(Sender, SenderDelegate, string.Empty, GetRelativeProgess(3, 0));

            for (int i = 0; i < UpdtDetails.UpdateFiles.Count; i++)
            {
                if (UpdtDetails.UpdateFiles[i].Execute && 
                    UpdtDetails.UpdateFiles[i].ExBeforeUpdate)
                {
                    psi = new ProcessStartInfo();

                    //use the absolute path
                    psi.FileName = FixUpdateDetailsPaths(UpdtDetails.UpdateFiles[i].RelativePath);

                    if (!string.IsNullOrEmpty(psi.FileName))
                    {
                        //command line arguments
                        if (!string.IsNullOrEmpty(UpdtDetails.UpdateFiles[i].CommandLineArgs))
                            psi.Arguments = ParseText(UpdtDetails.UpdateFiles[i].CommandLineArgs);

                        //start the process
                        Process p = Process.Start(psi);

                        if (UpdtDetails.UpdateFiles[i].WaitForExecution)
                            p.WaitForExit();
                    }
                }
            }

            ThreadHelper.ReportSuccess(Sender, SenderDelegate, string.Empty);
        }

        public void RunOptimizeExecute()
        {
            Thread.CurrentThread.IsBackground = true; //make them a daemon

            // simply update the progress bar to show the 6th step is entirely complete
            ThreadHelper.ReportProgress(Sender, SenderDelegate, string.Empty, GetRelativeProgess(6, 0));

            string filename = null;

            //optimize everything but "temp" files
            for (int i = 0; i < UpdtDetails.UpdateFiles.Count; i++)
            {
                if (UpdtDetails.UpdateFiles[i].IsNETAssembly)
                {
                    //if not a temp file
                    if (UpdtDetails.UpdateFiles[i].RelativePath.Length >= 4 &&
                        UpdtDetails.UpdateFiles[i].RelativePath.Substring(0, 4) != "temp")
                    {
                        //optimize (ngen) the file
                        filename = FixUpdateDetailsPaths(UpdtDetails.UpdateFiles[i].RelativePath);

                        if (!string.IsNullOrEmpty(filename))
                            NGenInstall(filename); //optimize the file
                    }
                }
            }

            ThreadHelper.ReportProgress(Sender, SenderDelegate, string.Empty, GetRelativeProgess(6, 50));

            ProcessStartInfo psi = null;

            //execute files
            for (int i = 0; i < UpdtDetails.UpdateFiles.Count; i++)
            {
                if (UpdtDetails.UpdateFiles[i].Execute &&
                !UpdtDetails.UpdateFiles[i].ExBeforeUpdate)
                {
                    psi = new ProcessStartInfo();

                    //use the absolute path
                    psi.FileName = FixUpdateDetailsPaths(UpdtDetails.UpdateFiles[i].RelativePath);

                    if (!string.IsNullOrEmpty(psi.FileName))
                    {
                        //command line arguments
                        if (!string.IsNullOrEmpty(UpdtDetails.UpdateFiles[i].CommandLineArgs))
                            psi.Arguments = ParseText(UpdtDetails.UpdateFiles[i].CommandLineArgs);

                        //start the process
                        Process p = Process.Start(psi);

                        if (UpdtDetails.UpdateFiles[i].WaitForExecution)
                            p.WaitForExit();
                    }
                }
            }

            ThreadHelper.ReportProgress(Sender, SenderDelegate, string.Empty, GetRelativeProgess(6, 100));

            //TODO: Make command processing more versatile
            //Process text commands like $refreshicons()
            if (!string.IsNullOrEmpty(UpdtDetails.PostUpdateCommands))
                ParseCommandText(UpdtDetails.PostUpdateCommands);

            ThreadHelper.ReportSuccess(Sender, SenderDelegate, string.Empty);
        }

        #region NGen Install

        string clrPath = null;

        [DllImport("mscoree.dll")]
        private static extern int GetCORSystemDirectory([MarshalAs(UnmanagedType.LPWStr)]StringBuilder pbuffer, int cchBuffer, ref int dwlength);

        private static string GetClrInstallationDirectory()
        {
            int MAX_PATH = 260;
            StringBuilder sb = new StringBuilder(MAX_PATH);
            GetCORSystemDirectory(sb, MAX_PATH, ref MAX_PATH);
            return sb.ToString();
        }

        private void NGenInstall(string filename)
        {
            if (string.IsNullOrEmpty(clrPath))
            {
                clrPath = GetClrInstallationDirectory();
            }

            Process proc = new Process();

            proc.StartInfo.FileName = Path.Combine(clrPath, "ngen.exe");
            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            proc.StartInfo.Arguments = " install \"" + filename + "\"" + " /nologo";

            proc.Start();

            proc.WaitForExit();
        }

        private void NGenUninstall(string filename)
        {
            if (string.IsNullOrEmpty(clrPath))
            {
                clrPath = GetClrInstallationDirectory();
            }

            Process proc = new Process();

            proc.StartInfo.FileName = Path.Combine(clrPath, "ngen.exe");
            proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            proc.StartInfo.Arguments = " uninstall \"" + filename + "\"" + " /nologo";

            proc.Start();

            proc.WaitForExit();
        }

        #endregion NGen Install

        private void FixUpdateFilesPaths(List<UpdateFile> updateFiles)
        {
            UpdateFile tempUFile;

            //replace every relative path with an absolute path
            for (int i = 0; i < updateFiles.Count; i++)
            {
                if (updateFiles[i].IsNETAssembly)
                {
                    tempUFile = updateFiles[i];

                    tempUFile.Filename = FixUpdateDetailsPaths(tempUFile.RelativePath);

                    updateFiles[i] = tempUFile;
                }
            }
        }

        private string FixUpdateDetailsPaths(string relPath)
        {
            if (relPath.Length < 4)
                return null;

            switch (relPath.Substring(0,4))
            {
                case "base":
                    return Path.Combine(ProgramDirectory, relPath.Substring(5));
                case "syst": //system
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), relPath.Substring(7));
                case "temp":
                    return Path.Combine(TempDirectory, relPath);
                case "appd": //appdata
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relPath.Substring(8));
                case "coma": //comappdata
                    return Path.Combine(SystemFolders.CommonAppData, relPath.Substring(11));
                case "comd": //comdesktop
                    return Path.Combine(SystemFolders.CommonDesktop, relPath.Substring(11));
                case "coms": //comstartmenu
                    return Path.Combine(SystemFolders.CommonProgramsStartMenu, relPath.Substring(13));
            }

            return null;
        }

        //handle thread cancelation
        public void Cancel()
        {
            this.canceled = true;
        }

        #region RelativePaths

        public enum PathAttribute { File = 0, Directory = 0x10 }
        public const Int32 MAX_PATH = 260;

        [DllImport("shlwapi.dll", CharSet = CharSet.Auto)]
        public static extern bool PathRelativePathTo(
             [Out] StringBuilder pszPath,
             [In] string pszFrom,
             [In] uint dwAttrFrom,
             [In] string pszTo,
             [In] uint dwAttrTo
        );

        private void deleteClientInPath(string destPath, string origPath)
        {
            string tempClientLoc = ClientInTempBase(destPath, origPath);

            if (tempClientLoc != null)
                File.Delete(tempClientLoc);
        }

        //returns a non-null string filename of the Client in the tempbase
        //if the Running Client will be overwritten by the Temp Client
        private string ClientInTempBase(string actualBase, string tempBase)
        {
            //relative path from origFolder to client location
            StringBuilder strBuild = new StringBuilder(MAX_PATH);
            string tempStr = Assembly.GetExecutingAssembly().Location;

            //find the relativity of the actualBase and this running client
            bool bRet = PathRelativePathTo(
                strBuild,
                actualBase, (uint)PathAttribute.Directory,
                tempStr, (uint)PathAttribute.File
            );

            if (bRet && strBuild.Length >= 2)
            {
                //get the first two characters
                tempStr = strBuild.ToString().Substring(0, 2);

                if (tempStr == @".\") //if client is in the destPath
                {
                    tempStr = Path.Combine(tempBase, strBuild.ToString());

                    if (File.Exists(tempStr))
                        return tempStr;
                }
            }

            return null;
        }

        #endregion Relativepaths

        #region Parse variables

        private RegChange ParseRegChange(RegChange reg)
        {
            if (reg.RegValueKind == Microsoft.Win32.RegistryValueKind.MultiString ||
                reg.RegValueKind == Microsoft.Win32.RegistryValueKind.String)
            {
                reg.ValueData = ParseText((string)reg.ValueData);
            }
            return reg;
        }

        private string ParseText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            List<string> excludeVariables = new List<string>();

            return ParseVariableText(text, ref excludeVariables);
        }

        private string ParseVariableText(string text, ref List<string> excludeVariables)
        {
            //parse a string, and return a pretty string (sans %%)
            StringBuilder returnString = new StringBuilder();
            string tempString;

            int firstIndex;
            int currentIndex;

            firstIndex = text.IndexOf('%', 0);

            if (firstIndex == -1)
            {
                //return the original
                return text;
            }

            returnString.Append(text.Substring(0, firstIndex));

            while (firstIndex != -1)
            {
                //find the next percent sign
                currentIndex = text.IndexOf('%', firstIndex + 1);

                //if no closing percent sign...
                if (currentIndex == -1)
                {
                    //return the rest of the string
                    returnString.Append(text.Substring(firstIndex, text.Length - firstIndex));
                    return returnString.ToString();
                }
                else
                {
                    //return the content of the variable
                    tempString = VariableToPretty(text.Substring(firstIndex + 1, currentIndex - firstIndex - 1), ref excludeVariables);

                    //if the variable isn't defined
                    if (tempString == null)
                    {
                        //return the string with the percent signs
                        returnString.Append(text.Substring(firstIndex, currentIndex - firstIndex));
                    }
                    else
                    {
                        //variable exists, add the parsed content
                        returnString.Append(tempString);
                        currentIndex++;
                        if (currentIndex == text.Length)
                        {
                            return returnString.ToString();
                        }
                    }
                }

                firstIndex = currentIndex;
                tempString = null;
            }

            return returnString.ToString();
        }

        private string VariableToPretty(string variable, ref List<string> excludeVariables)
        {
            variable = variable.ToLower();

            if (excludeVariables.Contains(variable))
                return null;

            string returnValue = null;

            excludeVariables.Add(variable);

            switch (variable)
            {
                case "basedir":
                    returnValue = ParseVariableText(ProgramDirectory, ref excludeVariables);
                    break;
                default:
                    excludeVariables.RemoveAt(excludeVariables.Count - 1);
                    return null;
            }

            //allow the variable to be processed again
            excludeVariables.Remove(variable);

            return returnValue;
        }

        #endregion Parse variables

        #region Execute Commands

        private static void ParseCommandText(string text)
        {
            int lastDollarIndex = text.LastIndexOf('$');
            int beginParen = -1, endParen = -1;

            CommandName currCommand = CommandName.NULL;

            //if no $'s found
            if (lastDollarIndex == -1)
                return;

            do
            {
                beginParen = text.IndexOf('(', lastDollarIndex);

                if (beginParen != -1)
                {
                    //get the text between the '$' and the '('
                    currCommand = GetCommandName(text.Substring(lastDollarIndex + 1, beginParen - lastDollarIndex - 1));

                    if (currCommand != CommandName.NULL)
                    {
                        endParen = IndexOfNonEnclosed(')', ref text, beginParen);

                        if (endParen != -1)
                        {
                            //replace the command, contents, and parenthesis
                            //with the modified contents
                            ExecuteTextCommand(currCommand);
                            text = text.Remove(lastDollarIndex, endParen - lastDollarIndex + 1);
                        }
                    }
                }

                lastDollarIndex = LastIndexOfReal('$', ref text, 0, lastDollarIndex - 1);

            } while (lastDollarIndex != -1);
        }

        private static int IndexOfNonEnclosed(char ch, ref string str, int startIndex)
        {
            for (int i = startIndex; i < str.Length; i++)
            {
                if (str[i] == ch)
                {
                    //if not the first of last char
                    if (i > 0 && i < str.Length - 2)
                    {
                        //if not enclosed in single quotes
                        if (str[i - 1] != '\'' || str[i + 1] != '\'')
                            return i;
                    }
                    else
                        return i;
                }
            }

            return -1;
        }

        private static int LastIndexOfReal(char ch, ref string str, int startIndex, int endIndex)
        {
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (str[i] == ch)
                    return i;
            }

            return -1;
        }

        public enum CommandName { NULL = -1, refreshicons }

        private static CommandName GetCommandName(string command)
        {
            CommandName name = CommandName.NULL;

            try
            {
                name = (CommandName)Enum.Parse(typeof(CommandName), command, true);
            }
            catch (Exception) { }

            return name;
        }

        private static void ExecuteTextCommand(CommandName command)
        {
            switch (command)
            {
                case CommandName.refreshicons:
                    //refresh shell icons
                    SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
                    break;
            }
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(long wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        #endregion
    }
}