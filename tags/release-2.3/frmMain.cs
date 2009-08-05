using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices; //launching elevated client
using System.Text;
using System.Threading;
using System.Windows.Forms;
using wyUpdate.Common;
using wyUpdate.Downloader;

namespace wyUpdate
{
    public partial class frmMain : Form
    {
        #region Private variables

        public bool IsAdmin;

        public readonly UpdateEngine update = new UpdateEngine();
        VersionChoice updateFrom;

        UpdateDetails updtDetails;

        FileDownloader downloader;
        InstallUpdate installUpdate;

        readonly ClientLanguage clientLang = new ClientLanguage();

        int frameOn;
        bool isCancelled;

        string error;
        string errorDetails;

        //The full filename of the update & servers files 
        string updateFilename;
        string serverFileLoc;

        //client file location
        string clientFileLoc;

        private string serverOverwrite;

        //the base directory (same path as the executable, unless specified)
        string baseDirectory;
        //the extract directory
        string tempDirectory;

        readonly PanelDisplay panelDisplaying = new PanelDisplay(500, 320);

        // should the client download the server file to check for updates
        bool checkForUpdate;

        //does the client need elevation?
        bool needElevation;
        bool willSelfUpdate;

        //--Uninstalling
        bool uninstalling;

        //--Silent updating/uninstalling
        bool isSilent;
        public int ReturnCode { get; set; }

        // Wait Mode (aka API mode)
        UpdateHelper updateHelper;
        System.Windows.Forms.Timer sendGotPreInstallInfo;
        bool isWaitMode;
        bool dontDestroyTempFolder; //custom temp directory to store downloaded updates

        //-- Self update
        public bool SelfUpdating;
        string selfUpdateFileLoc;
        string oldClientLocation;
        string clientSFLoc;

        //used after a self update or elevation
        bool continuingUpdate; 

        //Pre-RC2 compatability:
        ClientFileType clientFileType;

        bool selfUpdateFromRC1;
        string newClientLocation; //self update from RC1

        // handle hidden form
        bool _isApplicationRun = true;
        bool StartFormHidden;
        
        // start hidden, close if no update, show if update
        bool QuickCheck;
        bool QuickCheckNoErr;

        #region Threads

        delegate void ShowProgressDelegate(int percentDone, bool statusDone, string extraStatus, Exception ex);
        delegate void UninstallProgressDel(int percentDone, int stepOn, string extraStatus, Exception ex);
        delegate void CheckProcessesDel(List<FileInfo> files, bool statusDone);

        delegate void ChangeRollbackDelegate(bool rbRegistry);

        #endregion

        #endregion Private variables

        public frmMain(string[] args)
        {
            updateHelper = new UpdateHelper(this);
            updateHelper.SenderProcessClosed += UpdateHelper_SenderProcessClosed;
            updateHelper.RequestReceived += UpdateHelper_RequestReceived;

            //sets to SegoeUI on Vista
            Font = SystemFonts.MessageBoxFont;

            // check if user is an admin for windows 2000+, otherwise assume we are
            IsAdmin = Environment.OSVersion.Version.Major > 4 ? VistaTools.IsUserAnAdmin() : true;

            InitializeComponent();

            //enable Lazy SSL for all downloads
            FileDownloader.EnableLazySSL();

            //resize the client so its client region = 500x360
            if (ClientRectangle.Width != 500)
                Width = (Width - ClientRectangle.Width) + 500;

            if (ClientRectangle.Height != 360)
                Height = (Height - ClientRectangle.Height) + 360;

            //add the panelDisplaying to form
            panelDisplaying.TabIndex = 0;
            Controls.Add(panelDisplaying);

            //process commandline argument
            Arguments commands = new Arguments(args);
            ProcessArguments(commands);

            try
            {
                // load the self update information
                if (!string.IsNullOrEmpty(selfUpdateFileLoc))
                {
                    LoadSelfUpdateData(selfUpdateFileLoc);

                    //if the loaded file is from RC1, then update self and bail out
                    if (selfUpdateFromRC1)
                    {
                        //install the new client, and relaunch it to continue the update
                        if (needElevation && NeedElevationToUpdate())
                        {
                            //the user "elevated" as a non-admin user
                            //warn the user of their idiocy
                            error = clientLang.AdminError;

                            //set to false so new client won't be launched in frmMain_Load()
                            selfUpdateFromRC1 = false;
                            
                            ShowFrame(-1);
                        }
                        else
                        {
                            needElevation = false;

                            //Install the new client
                            File.Copy(newClientLocation, oldClientLocation, true);

                            //Relaunch self in OnLoad()
                        }

                        //bail out
                        return;
                    }
                }

                //Load the client information
                if (clientFileType == ClientFileType.PreRC2)
                    //TODO: stop supporting old client files after 1.0 Final.
                    update.OpenObsoleteClientFile(clientFileLoc);
                else
                    update.OpenClientFile(clientFileLoc, clientLang);

                clientLang.SetVariables(update.ProductName, update.InstalledVersion);
            }
            catch (Exception ex)
            {
                clientLang.SetVariables(update.ProductName, update.InstalledVersion);

                error = "Client file failed to load. The client.wyc file might be corrupt.";
                errorDetails = ex.Message;

                ShowFrame(-1);
                return;
            }

            //sets up Next & Cancel buttons
            SetButtonText();

            //set header alignment, etc.
            panelDisplaying.HeaderImageAlign = update.HeaderImageAlign;

            if (update.HeaderTextIndent >= 0)
                panelDisplaying.HeaderIndent = update.HeaderTextIndent;

            panelDisplaying.HideHeaderDivider = update.HideHeaderDivider;

            try
            {
                if (!string.IsNullOrEmpty(update.HeaderTextColorName))
                    panelDisplaying.HeaderTextColor = Color.FromName(update.HeaderTextColorName);
            }
            catch { }

            //load the Side/Top images
            panelDisplaying.TopImage = update.TopImage;
            panelDisplaying.SideImage = update.SideImage;

            if (SelfUpdating)
            {
                try
                {
                    //load the self-update server file
                    LoadClientServerFile(null);
                    clientLang.NewVersion = update.NewVersion;
                }
                catch (Exception ex)
                {
                    error = clientLang.ServerError;
                    errorDetails = ex.Message;

                    ShowFrame(-1);
                    return;
                }

                if (needElevation && NeedElevationToUpdate())
                {
                    //the user "elevated" as a non-admin user
                    //warn the user of their idiocy
                    error = clientLang.AdminError;

                    ShowFrame(-1);
                }
                else
                {
                    needElevation = false;

                    //begin updating the product
                    ShowFrame(3);
                }
            }
            else if (continuingUpdate) //continuing from elevation or self update (or both)
            {
                try
                {
                    //load the server file (without filling the 'changes' box)
                    LoadServerFile(false);
                }
                catch (Exception ex)
                {
                    error = clientLang.ServerError;
                    errorDetails = ex.Message;

                    ShowFrame(-1);
                    return;
                }

                if (needElevation && NeedElevationToUpdate())
                {
                    //the user "elevated" as a non-admin user
                    //warn the user of their idiocy
                    error = clientLang.AdminError;

                    ShowFrame(-1);
                }
                else
                {
                    needElevation = false;

                    //begin updating the product
                    ShowFrame(3);
                }
            }
            else if (!uninstalling)
                checkForUpdate = true;
        }

        protected override void SetVisibleCore(bool value)
        {
            if (_isApplicationRun)
            {
                _isApplicationRun = false;

                base.SetVisibleCore(StartFormHidden ? false : value);


                // run the OnLoad code

                if (uninstalling)
                {
                    ShowFrame(7);
                }
                else if (selfUpdateFromRC1)
                {
                    //if the loaded file is from RC1, then update self and bail out

                    //Relaunch self
                    StartSelfElevated();
                }
                else if (checkForUpdate)
                    // begin check for updates
                    ShowFrame(1);

                return;
            }

            base.SetVisibleCore(value);
        }


        private void ProcessArguments(Arguments commands)
        {
            if (commands["supdf"] != null)
            {
                //the client is in self update mode
                selfUpdateFileLoc = commands["supdf"];
            }
            else
            {
                //client data file
                if (commands["cdata"] != null)
                {
                    clientFileLoc = commands["cdata"];

                    if (clientFileLoc.EndsWith("iuc", StringComparison.InvariantCultureIgnoreCase))
                        clientFileType = ClientFileType.PreRC2;
                    else if (clientFileLoc.EndsWith("iucz", StringComparison.InvariantCultureIgnoreCase))
                        clientFileType = ClientFileType.RC2;
                    else
                        clientFileType = ClientFileType.Final;
                }
                else
                {
                    clientFileLoc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.wyc");
                    clientFileType = ClientFileType.Final;

                    //try the RC-2 filename
                    if (!File.Exists(clientFileLoc))
                    {
                        clientFileLoc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "iuclient.iucz");
                        clientFileType = ClientFileType.RC2;
                    }
                    
                    //try Pre-RC2 filename
                    if (!File.Exists(clientFileLoc))
                    {
                        //if it doesn't exist, try without the 'z'
                        clientFileLoc = clientFileLoc.Substring(0, clientFileLoc.Length - 1);
                        clientFileType = ClientFileType.PreRC2;
                    }
                }

                //set basedirectory as the location of the executable
                baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

                if (commands["basedir"] != null && Directory.Exists(commands["basedir"]))
                {
                    //if the specified directory exists, then set as directory
                    baseDirectory = commands["basedir"];
                }

                if (commands["tempdir"] != null && Directory.Exists(commands["tempdir"]))
                {
                    //set the temp directory
                    tempDirectory = commands["tempdir"];
                }
                else //if the tempDir hasn't been created
                {
                    //create my own "random" temp dir.
                    tempDirectory = Path.Combine(Path.GetTempPath(), @"wyup" + DateTime.Now.ToString("ddMMssfff"));
                    Directory.CreateDirectory(tempDirectory);
                }

                //uninstall any newly created folders, files, or registry
                if (commands["uninstall"] != null)
                    uninstalling = true;


                // wait mode - for automatic updates
                if (commands["wait"] != null)
                {
                    sendGotPreInstallInfo = new System.Windows.Forms.Timer
                                                {
                                                    Enabled = false, Interval = 1
                                                };

                    sendGotPreInstallInfo.Tick += sendGotPreInstallInfo_Tick;

                    isWaitMode = true;

                    if (commands["tempdir"] != null)
                        dontDestroyTempFolder = true;
                }
                
                if (commands["quickcheck"] != null)
                {
                    StartFormHidden = true;
                    QuickCheck = true;

                    if (commands["noerr"] != null)
                        QuickCheckNoErr = true;
                }

                // load the passed server argument
                if (commands["server"] != null)
                    serverOverwrite = commands["server"];


                //only allow silent uninstalls 
                //TODO: allow silent checking and updating
                if (uninstalling && commands["s"] != null)
                {
                    isSilent = true;

                    WindowState = FormWindowState.Minimized;
                    ShowInTaskbar = false;
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            //only warn if after the welcome page
            //and not self updating/elevating
            if (needElevation || willSelfUpdate || SelfUpdating || isSilent ||
                isCancelled || panelDisplaying.TypeofFrame == FrameType.WelcomeFinish)
            {
                //close the form
                e.Cancel = false;
            }
            else //currently updating
            {
                //stop closing
                e.Cancel = true;

                //prompt the user if they really want to cancel
                CancelUpdate();
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            //if not self updating, then delete temp files.
            if (!(needElevation || willSelfUpdate || SelfUpdating || dontDestroyTempFolder))
            {
                //if the temp directory exists, remove it
                if (Directory.Exists(tempDirectory))
                {
                    try
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                    catch { }
                }
            }

            base.OnClosed(e);
        }

        #region Downloading, updating, and checking processes (async)

        // update the label & progress bar when downloading/updating
        private void ShowProgress(int percentDone, bool done, string extraStatus, Exception ex)
        {
            //update progress bar
            if (percentDone >= 0 && percentDone <= 100)
                panelDisplaying.Progress = percentDone;

            //update bottom status
            if (extraStatus != panelDisplaying.ProgressStatus && extraStatus != "")
                panelDisplaying.ProgressStatus = extraStatus;

            if (done && ex == null)
            {
                if (isCancelled)
                    Close(); //close the form
                else if (frameOn == 1)
                {
                    //set the serverfile location
                    serverFileLoc = downloader.DownloadingTo;
                    try
                    {
                        //load the server file into memory
                        LoadServerFile(true);
                    }
                    catch (NoUpdatePathToNewestException)
                    {
                        //there is no update path to the newest version
                        ShowFrame(6);
                        return;
                    }
                    catch (Exception e)
                    {
                        ex = e; //error occured, show error screen
                    }
                }
                else
                {
                    if (update.CurrentlyUpdating == UpdateOn.DownloadingUpdate)
                        updateFilename = downloader.DownloadingTo;

                    // continue on to next step
                    StepCompleted();
                }
            }

            
            if (ex != null)
            {
                //Show the error (rollback has already been done)
                if (frameOn == 1)
                {
                    error = clientLang.ServerError;
                    errorDetails = ex.Message;
                }
                else
                {
                    if (update.CurrentlyUpdating == UpdateOn.DownloadingUpdate)
                    {
                        //a download error occurred
                        error = clientLang.DownloadError;
                        errorDetails = ex.Message;
                    }
                    else // an update error occurred
                    {
                        // if the exception was PatchApplicationException, then
                        //see if a catch-all update exists (and the catch-all update isn't the one that failed)
                        if (ex.GetType() == typeof(PatchApplicationException) &&
                            updateFrom != update.VersionChoices[update.VersionChoices.Count - 1] &&
                            update.VersionChoices[update.VersionChoices.Count - 1].Version == update.NewVersion)
                        {
                            updateFrom = update.VersionChoices[update.VersionChoices.Count - 1];

                            error = null;

                            panelDisplaying.UpdateItems[1].Status = UpdateItemStatus.Nothing;

                            update.CurrentlyUpdating = UpdateOn.DownloadingUpdate;

                            // download the catch-all update
                            ShowFrame(3);

                            return;
                        }

                        error = clientLang.GeneralUpdateError;
                        errorDetails = ex.Message;
                    }
                }

                ShowFrame(-1);
            }
        }

        private void SelfUpdateProgress(int percentDone, bool done, string extraStatus, Exception ex)
        {
            //update progress bar
            panelDisplaying.Progress = percentDone;

            //update bottom status
            if (extraStatus != panelDisplaying.ProgressStatus && extraStatus != "")
                panelDisplaying.ProgressStatus = extraStatus;

            if (done && ex == null)
            {
                if (isCancelled)
                    Close(); //close the form
                else if (frameOn == 1)
                {
                    clientSFLoc = downloader.DownloadingTo;

                    try
                    {
                        // client server file downloaded sucessfully
                        DownloadClientSFSuccess();
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }
                }
                else
                {
                    if (update.CurrentlyUpdating == UpdateOn.DownloadingClientUpdt)
                    {
                        panelDisplaying.UpdateItems[0].Status = UpdateItemStatus.Success;
                        
                        //set the filename of the downloaded client update file
                        updateFilename = downloader.DownloadingTo;
                        
                        //begin extracting and installing the update
                        update.CurrentlyUpdating = UpdateOn.SelfUpdating;
                        InstallUpdates(update.CurrentlyUpdating);
                    }
                    else if (update.CurrentlyUpdating == UpdateOn.SelfUpdating)
                    {
                        panelDisplaying.UpdateItems[1].Status = UpdateItemStatus.Success;

                        //start the newly installed client and resume "normal" downloading & updating
                        StartSelfElevated();
                    }
                }
            }
            
            
            if (ex != null)
            {
                // if a new client is *required* to install the update...
                if (VersionTools.Compare(VersionTools.FromExecutingAssembly(), update.MinClientVersion) == -1)
                {
                    //show an error and bail out
                    error = clientLang.SelfUpdateInstallError;
                    errorDetails = ex.Message;

                    ShowFrame(-1);
                }
                else //self update isn't necessary, so handle gracefully
                {
                    if (frameOn == 1)
                    {
                        //client server file failed to download, continue as usual:

                        willSelfUpdate = false;

                        //Show update info page
                        ShowFrame(2);
                    }
                    else
                    {
                        // if the exception was PatchApplicationException, then
                        //see if a catch-all update exists (and the catch-all update isn't the one that failed)
                        if (ex.GetType() == typeof(PatchApplicationException) &&
                            updateFrom != update.VersionChoices[update.VersionChoices.Count - 1] &&
                            update.VersionChoices[update.VersionChoices.Count - 1].Version == update.NewVersion)
                        {
                            updateFrom = update.VersionChoices[update.VersionChoices.Count - 1];

                            error = null;

                            panelDisplaying.UpdateItems[1].Status = UpdateItemStatus.Nothing;

                            update.CurrentlyUpdating = UpdateOn.DownloadingUpdate;

                            // download the catch-all update
                            ShowFrame(3);

                            return;
                        }


                        //self-update failed to download or install
                        //just relaunch old client and continue with update
                        StartSelfElevated();
                    }
                }
            }
        }

        private void UninstallProgress(int percentDone, int step, string extraStatus, Exception ex)
        {
            //update progress bar
            panelDisplaying.Progress = percentDone;

            //update bottom status
            if (extraStatus != panelDisplaying.ProgressStatus && extraStatus != "")
                panelDisplaying.ProgressStatus = extraStatus;

            //step: 0=working, 1=uninstalling registry, 2=done
            if (step == 1)
            {
                panelDisplaying.UpdateItems[0].Status = UpdateItemStatus.Success;
                SetStepStatus(1, clientLang.UninstallRegistry);
            }
            else if (step == 2 && ex == null)
            {
                //just bail out.
                Close();
            }
            else if (ex != null)
            {
                if (isSilent)
                    Close();
                else
                {
                    //Show the error (rollback has ocurred)
                    error = ex.Message;
                    ShowFrame(-1);
                }
            }
        }

        private void CheckProcess(List<FileInfo> files, bool done)
        {
            if (done)
            {
                if (files != null)//if there are some files needing closing
                {
                    Form proc = new frmProcesses(files, clientLang);
                    DialogResult result = proc.ShowDialog();

                    if (result == DialogResult.Cancel)
                    {
                        //cancel the update process
                        CancelUpdate(true);
                    }
                    else
                    {
                        //processes closed, continue on
                        update.CurrentlyUpdating += 1;
                        InstallUpdates(update.CurrentlyUpdating);
                    }
                }
                else
                {
                    //no processes need to be closed, continue on
                    update.CurrentlyUpdating += 1;
                    InstallUpdates(update.CurrentlyUpdating);
                }
            }
        }

        private void ChangeRollback(bool rbRegistry)
        {
            DisableCancel();

            // set the error icon to current progress item
            if(rbRegistry)
            {
                // error updating the registry
                panelDisplaying.UpdateItems[2].Status = UpdateItemStatus.Error;

                SetStepStatus(3, clientLang.RollingBackRegistry);
            }
            else if (panelDisplaying.UpdateItems[2].Status != UpdateItemStatus.Error)
            {
                // error updating the files
                panelDisplaying.UpdateItems[1].Status = UpdateItemStatus.Error;

                SetStepStatus(2, clientLang.RollingBackFiles);
            }
            else
            {
                SetStepStatus(3, clientLang.RollingBackFiles);
            }
        }

        #endregion Downloading, updating, and checking processes (async)

        #region Downloading methods (synchronous)

        //downlaod regular update files
        private void BeginDownload(List<string> sites, long adler32, bool relativeProgress)
        {
            if (downloader != null)
            {
                downloader.ProgressChanged -= ShowProgress;
                downloader.ProgressChanged -= SelfUpdateProgress;
            }

            downloader = new FileDownloader(sites, tempDirectory)
                             {
                                 Adler32 = adler32,
                                 UseRelativeProgress = relativeProgress
                             };

            downloader.ProgressChanged += ShowProgress;
            downloader.Download();
        }

        //download self update files (server file or update file)
        private void BeginSelfUpdateDownload(List<string> sites, long adler32)
        {
            if (downloader != null)
            {
                downloader.ProgressChanged -= ShowProgress;
                downloader.ProgressChanged -= SelfUpdateProgress;
            }

            downloader = new FileDownloader(sites, tempDirectory)
                             {
                                 Adler32 = adler32
                             };

            downloader.ProgressChanged += SelfUpdateProgress;
            downloader.Download();
        }

        //client server file downloaded
        private void DownloadClientSFSuccess()
        {
            //load the client server file, and see if a new version is availiable
            UpdateEngine clientSF = new UpdateEngine();

            LoadClientServerFile(clientSF);

            //check if the client is new enough.
            willSelfUpdate = VersionTools.Compare(VersionTools.FromExecutingAssembly(), clientSF.NewVersion) == -1;

            //Show update info page
            ShowFrame(2);
        }

        private void LoadClientServerFile(UpdateEngine updateEngine)
        {
            //load the client server file
            if (updateEngine == null)
            {
                update.LoadServerDatav2(clientSFLoc);

                //get the current version of the Client
                string currentClientVersion = VersionTools.FromExecutingAssembly();

                foreach (VersionChoice vChoice in update.VersionChoices)
                {
                    // select the correct delta-patch version choice
                    // using fuzzy equality (i.e. 1.1 == 1.1.0.0)
                    if (VersionTools.Compare(vChoice.Version, currentClientVersion) == 0)
                    {
                        updateFrom = vChoice;
                        break;
                    }
                }

                //if no delta-patch update has been selected, use the catch-all update
                if (updateFrom == null)
                    updateFrom = update.VersionChoices[update.VersionChoices.Count - 1];
            }
            else
                updateEngine.LoadServerDatav2(clientSFLoc);
        }

        //returns True if an update is necessary, otherwise false
        private void LoadServerFile(bool setChangesText)
        {
            //load the server file
            update.LoadServerDatav2(serverFileLoc);

            clientLang.NewVersion = update.NewVersion;

            // if no update is needed...
            if (VersionTools.Compare(update.InstalledVersion, update.NewVersion) > -1)
            {
                if (isWaitMode)
                {
                    // send reponse that there's no update available
                    updateHelper.SendSuccess();

                    // close this client
                    isCancelled = true;
                    Close();

                    return;
                }

                // Show "All Finished" page
                ShowFrame(5);
                return;
            }

            int i;

            for (i = 0; i < update.VersionChoices.Count; i++)
            {
                // select the correct delta-patch version choice
                if (VersionTools.Compare(update.VersionChoices[i].Version, update.InstalledVersion) == 0)
                {
                    updateFrom = update.VersionChoices[i];
                    break;
                }
            }


            //if no delta-patch update has been selected, use the catch-all update (if it exists)
            if (updateFrom == null && update.VersionChoices[update.VersionChoices.Count - 1].Version == update.NewVersion)
                updateFrom = update.VersionChoices[update.VersionChoices.Count - 1];

            if (updateFrom == null)
                throw new NoUpdatePathToNewestException();

            if (setChangesText)
            {
                //if there's a catch-all update start with one less than "update.VersionChoices.Count - 1"

                bool catchAllExists = update.VersionChoices[update.VersionChoices.Count - 1].Version == update.NewVersion;


                //build the changes from all previous versions
                for (int j = update.VersionChoices.Count - 1; j >= i; j--)
                {
                    //show the version number for previous updates we may have missed
                    if (j != update.VersionChoices.Count - 1 && (!catchAllExists || catchAllExists && j != update.VersionChoices.Count - 2))
                        panelDisplaying.AppendAndBoldText("\r\n\r\n" + update.VersionChoices[j + 1].Version + ":\r\n\r\n");

                    // append the changes to the total changes list
                    if (!catchAllExists || catchAllExists && j != update.VersionChoices.Count - 2)
                    {
                        if (update.VersionChoices[j].RTFChanges)
                            panelDisplaying.AppendRichText(update.VersionChoices[j].Changes);
                        else
                            panelDisplaying.AppendText(update.VersionChoices[j].Changes);
                    }
                }

                //download the client server file and see if the client is new enough
                BeginSelfUpdateDownload(update.ClientServerSites, 0);
            }
        }

        #endregion End of Downloading methods (synchronous)

        #region Updating methods (synchronous)

        private void ShowFrame(int frameNum)
        {
            switch (frameNum)
            {
                case 1: //Update checking screen
                    frameOn = 1;

                    panelDisplaying.ChangePanel(FrameType.Update,
                        clientLang.Checking.Title,
                        clientLang.Checking.SubTitle,
                        clientLang.Checking.Content,
                        "");

                    btnNext.Enabled = false;

                    if (!isWaitMode)
                    {
                        if(!string.IsNullOrEmpty(serverOverwrite))
                        {
                            // overrite server file
                            List<string> overwriteServer = new List<string> {serverOverwrite};
                            BeginDownload(overwriteServer, 0, false);
                        }
                        else
                        {
                            //download the server file
                            BeginDownload(update.ServerFileSites, 0, false);
                        }
                    }

                    break;
                case 2: //Update Info Screen
                    frameOn = 2;

                    panelDisplaying.ChangePanel(FrameType.TextInfo,
                        clientLang.UpdateInfo.Title,
                        clientLang.UpdateInfo.SubTitle,
                        clientLang.UpdateInfo.Content,
                        clientLang.UpdateBottom);

                    //check if elevation is needed
                    needElevation = NeedElevationToUpdate();

                    btnNext.Enabled = true;
                    btnNext.Text = clientLang.UpdateButton;

                    if(QuickCheck)
                    {
                        // show the update window
                        Visible = true;
                        TopMost = true;
                        TopMost = false;

                        QuickCheck = false;
                    }

                    break;
                case 3: //Download and Install Updates
                    frameOn = 3;

                    panelDisplaying.ShowChecklist = true;

                    panelDisplaying.ChangePanel(FrameType.Update,
                        clientLang.DownInstall.Title,
                        clientLang.DownInstall.SubTitle,
                        clientLang.DownInstall.Content,
                        "");

                    if (SelfUpdating)
                    {
                        //show status for downloading self
                        SetStepStatus(0, clientLang.DownloadingSelfUpdate);
                    }
                    else
                    {
                        //show status for the downloading update
                        SetStepStatus(0, clientLang.Download);
                    }

                    btnNext.Enabled = false;

                    if (SelfUpdating)
                    {
                        //download self update
                        update.CurrentlyUpdating = UpdateOn.DownloadingClientUpdt;
                        BeginSelfUpdateDownload(updateFrom.FileSites, updateFrom.Adler32);
                    }
                    else
                    {
                        //download the update file
                        BeginDownload(updateFrom.FileSites, updateFrom.Adler32, true);
                    }
                    break;
                case 4: //Display Congrats Window
                    frameOn = 4;

                    panelDisplaying.ChangePanel(FrameType.WelcomeFinish,
                        clientLang.SuccessUpdate.Title,
                        clientLang.SuccessUpdate.Content,
                        "",
                        clientLang.FinishBottom);

                    btnNext.Enabled = true;
                    btnCancel.Visible = false;
                    btnNext.Text = clientLang.FinishButton;

                    break;
                case 5: //Your Product is already up to date screen
                    frameOn = 5;

                    panelDisplaying.ChangePanel(FrameType.WelcomeFinish,
                        clientLang.AlreadyLatest.Title,
                        clientLang.AlreadyLatest.Content,
                        "",
                        clientLang.FinishBottom);

                    btnNext.Enabled = true;
                    btnCancel.Visible = false;
                    btnNext.Text = clientLang.FinishButton;

                    break;
                case 6: //No update to the latest version is available
                    frameOn = 6;

                    if (!string.IsNullOrEmpty(update.NoUpdateToLatestLinkText))
                        panelDisplaying.SetNoUpdateAvailableLink(update.NoUpdateToLatestLinkText, update.NoUpdateToLatestLinkURL);

                    panelDisplaying.ChangePanel(FrameType.WelcomeFinish,
                        clientLang.NoUpdateToLatest.Title,
                        clientLang.NoUpdateToLatest.Content,
                        "",
                        clientLang.FinishBottom);

                    btnNext.Enabled = true;
                    btnCancel.Visible = false;
                    btnNext.Text = clientLang.FinishButton;

                    break;
                case 7: //Uninstall screen
                    frameOn = 7;

                    panelDisplaying.ShowChecklist = true;

                    panelDisplaying.ChangePanel(FrameType.Update,
                        clientLang.Uninstall.Title,
                        clientLang.Uninstall.SubTitle,
                        clientLang.Uninstall.Content,
                        "");


                    //Show uninstalling status
                    SetStepStatus(0, clientLang.UninstallFiles);

                    btnNext.Enabled = false;

                    InstallUpdates(UpdateOn.Uninstalling);

                    break;
                case -1: //Display error screen
                    
                    //TODO: make the return codes error specific
                    ReturnCode = 1;

                    frameOn = -1;

                    // show details button to hide all the complex crap from users
                    panelDisplaying.ErrorDetails = errorDetails;
                    panelDisplaying.SetUpErrorDetails(clientLang.ShowDetails);

                    panelDisplaying.ChangePanel(FrameType.WelcomeFinish,
                        clientLang.UpdateError.Title,
                        error,
                        "",
                        clientLang.FinishBottom);

                    btnNext.Enabled = true;
                    btnCancel.Visible = false;
                    btnNext.Text = clientLang.FinishButton;

                    break;
            }

            //enable the close button on the Finish/Error screens
            if (frameNum == 4 || frameNum == 5 || frameNum == 6 || frameNum == -1)
            {
                // allow the user to forcefuly exit
                BlockLogOff(false);

                EnableCancel();
                
                //allow the user to exit by pressing ESC
                CancelButton = btnNext;

                //set the error return code (1) or success (0)
                ReturnCode = frameNum == -1 ? 1 : 0;

                if (QuickCheck)
                {
                    if (frameNum == -1 && !QuickCheckNoErr)
                    {
                        Visible = true;
                        TopMost = true;
                        TopMost = false;
                    }
                    else
                        Close();
                }
            }

            //if silent & if on one of the user interaction screens, then click next
            if (isSilent && (frameOn == 0 || frameOn == 2 || frameOn == 4 || frameOn == 5 || frameOn == 6 || frameOn == -1))
            {
                btnNext_Click(null, new EventArgs());
            }

            //so the user doesn't accidentally cancel update.
            btnNext.Focus();
        }

        private void InstallUpdates(UpdateOn CurrentlyUpdating)
        {
            ShowProgressDelegate showProgress = ShowProgress;

            Thread asyncThread = null;

            switch (CurrentlyUpdating)
            {
                case UpdateOn.SelfUpdating:
                    SetStepStatus(1, clientLang.SelfUpdate);

                    showProgress = SelfUpdateProgress;

                    installUpdate = new InstallUpdate(Path.Combine(tempDirectory, updateFilename),
                        tempDirectory, this, showProgress)
                                        {
                                            OldIUPClientLoc = oldClientLocation
                                        };

                    //location of old "self" to replace

                    asyncThread = new Thread(installUpdate.RunSelfUpdate);
                    break;
                case UpdateOn.Extracting:
                    SetStepStatus(1, clientLang.Extract);

                    installUpdate = new InstallUpdate(tempDirectory, baseDirectory, showProgress, this)
                                        {
                                            Filename = Path.Combine(tempDirectory, updateFilename),
                                            OutputDirectory = tempDirectory
                                        };

                    asyncThread = new Thread(installUpdate.RunUnzipProcess);
                    break;
                case UpdateOn.ClosingProcesses:
                    SetStepStatus(1, clientLang.Processes);

                    installUpdate = new InstallUpdate(tempDirectory, baseDirectory, 
                        new CheckProcessesDel(CheckProcess), this);

                    asyncThread = new Thread(installUpdate.RunProcessesCheck);
                    break;
                case UpdateOn.PreExecute:

                    // try to stop the user from forcefuly exiting
                    BlockLogOff(true);

                    SetStepStatus(1, clientLang.PreExec);

                    installUpdate = new InstallUpdate(tempDirectory, baseDirectory, showProgress, this)
                                        {
                                            UpdtDetails = updtDetails
                                        };

                    asyncThread = new Thread(installUpdate.RunPreExecute);
                    break;
                case UpdateOn.BackingUp:
                    SetStepStatus(1, clientLang.Files);

                    installUpdate = new InstallUpdate(tempDirectory, baseDirectory, showProgress, this)
                                        {
                                            UpdtDetails = updtDetails,
                                            RollbackDelegate = (ChangeRollbackDelegate)ChangeRollback
                                        };

                    asyncThread = new Thread(installUpdate.RunUpdateFiles);
                    break;
                case UpdateOn.ModifyReg:
                    SetStepStatus(2, clientLang.Registry);

                    installUpdate = new InstallUpdate(tempDirectory, baseDirectory, showProgress, this)
                                        {
                                            UpdtDetails = updtDetails,
                                            RollbackDelegate = (ChangeRollbackDelegate)ChangeRollback
                                        };

                    asyncThread = new Thread(installUpdate.RunUpdateRegistry);
                    break;
                case UpdateOn.OptimizeExecute:
                    SetStepStatus(3, clientLang.Optimize);

                    installUpdate = new InstallUpdate(tempDirectory, baseDirectory, showProgress, this)
                                        {
                                            UpdtDetails = updtDetails
                                        };

                    asyncThread = new Thread(installUpdate.RunOptimizeExecute);
                    break;
                case UpdateOn.WriteClientFile:
                    //don't show any status, but begin the thread to being updating the client file
                    
                    //Save the client file with the new version
                    update.InstalledVersion = update.NewVersion;

                    installUpdate = new InstallUpdate(tempDirectory, baseDirectory, showProgress, this)
                                        {
                                            Filename = clientFileLoc,
                                            UpdtDetails = updtDetails,
                                            ClientFileType = clientFileType,
                                            ClientFile = update,
                                            SkipProgressReporting = true
                                        };

                    asyncThread = new Thread(installUpdate.RunUpdateClientDataFile);
                    break;
                case UpdateOn.DeletingTemp:
                    SetStepStatus(3, clientLang.TempFiles);

                    installUpdate = new InstallUpdate(tempDirectory, clientFileLoc, showProgress, this);

                    asyncThread = new Thread(installUpdate.RunDeleteTemporary);
                    break;
                case UpdateOn.Uninstalling:
                    //need to pass: client data file loc, delegate, this
                    installUpdate = new InstallUpdate(clientFileLoc, new UninstallProgressDel(UninstallProgress), this);

                    asyncThread = new Thread(installUpdate.RunUninstall);
                    break;
                default:
                    break;
            }

            if (asyncThread != null)
            {
                //make them a daemon - prevent thread callback issues
                asyncThread.IsBackground = true; 
                asyncThread.Start();
            }
        }

        private void SetStepStatus(int stepNum, string stepText)
        {
            panelDisplaying.ProgressStatus = "";
            panelDisplaying.UpdateItems[stepNum].Status = UpdateItemStatus.Working;
            panelDisplaying.UpdateItems[stepNum].Text = stepText;
        }

        //update step completed, continue to next
        private void StepCompleted()
        {
            if (frameOn == 3) // update screen
            {
                if (update.CurrentlyUpdating == UpdateOn.DeletingTemp)
                {
                    //successfully deleted temporary files
                    panelDisplaying.UpdateItems[3].Status = UpdateItemStatus.Success;

                    //Successfully Updated
                    ShowFrame(4);

                    btnNext.Enabled = true;
                }
                else
                {
                    //show check mark to completed
                    switch (update.CurrentlyUpdating)
                    {
                        case UpdateOn.DownloadingUpdate: //done downloading
                            panelDisplaying.UpdateItems[0].Status = UpdateItemStatus.Success;
                            break;
                        case UpdateOn.Extracting:
                            //done extracting, load update details
                            updtDetails = installUpdate.UpdtDetails;
                            break;
                        case UpdateOn.BackingUp: //done backing up & installing files
                            panelDisplaying.UpdateItems[1].Status = UpdateItemStatus.Success;
                            break;
                        case UpdateOn.ModifyReg: //done modifying registry
                            panelDisplaying.UpdateItems[2].Status = UpdateItemStatus.Success;
                            break;
                    }

                    //Go to the next step
                    update.CurrentlyUpdating += 1;

                    //if there isn't an updateDetails file
                    if (updtDetails == null &&
                        (update.CurrentlyUpdating == UpdateOn.PreExecute ||
                        update.CurrentlyUpdating == UpdateOn.OptimizeExecute ||
                        update.CurrentlyUpdating == UpdateOn.ModifyReg))
                    {
                        update.CurrentlyUpdating += 1;

                        if (update.CurrentlyUpdating == UpdateOn.ModifyReg)
                            update.CurrentlyUpdating += 1;
                    }

                    InstallUpdates(update.CurrentlyUpdating);
                }
            }
        }

        #endregion Updating methods (synchronous)

        #region UpdateHelper functions (API)

        void UpdateHelper_RequestReceived(object sender, UpdateStep e)
        {
            switch (e)
            {
                case UpdateStep.CheckForUpdate:

                    if (!string.IsNullOrEmpty(serverOverwrite))
                    {
                        // overrite server file
                        List<string> overwriteServer = new List<string> { serverOverwrite };
                        BeginDownload(overwriteServer, 0, false);
                    }
                    else
                    {
                        //download the server file
                        BeginDownload(update.ServerFileSites, 0, false);
                    }

                    break;
                case UpdateStep.DownloadUpdate:

                    ShowFrame(3);

                    break;
                case UpdateStep.BeginExtraction:

                    update.CurrentlyUpdating = UpdateOn.Extracting;
                    InstallUpdates(update.CurrentlyUpdating);

                    break;
                case UpdateStep.PreInstallInfo:

                    //TODO: make a note of the pre-install info


                    // send a success signal.
                    sendGotPreInstallInfo.Start();


                    break;
                case UpdateStep.Install:

                    TopMost = true;

                    update.CurrentlyUpdating = UpdateOn.ClosingProcesses;
                    InstallUpdates(update.CurrentlyUpdating);

                    TopMost = false;

                    break;
            }
        }

        void UpdateHelper_SenderProcessClosed(object sender, EventArgs e)
        {
            //TODO: cleanup what currently doing (cancel any process)


            // exit the client
            if (!updateHelper.PreInstallInfoSent)
                Close();
        }

        void sendGotPreInstallInfo_Tick(object sender, EventArgs e)
        {
            sendGotPreInstallInfo.Enabled = false;

            updateHelper.SendSuccess();
        }

        #endregion UpdateHelper functions (API)


        #region Next, Back, Cancel, Options

        private void btnNext_Click(object sender, EventArgs e)
        {
            if (frameOn == 4 || frameOn == 5 || frameOn == 6 || frameOn == -1)
            {
                Close();
            }
            else
            {
                if (needElevation || willSelfUpdate)
                    StartSelfElevated();
                else
                    ShowFrame(frameOn + 1);
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            CancelUpdate();
        }

        
        private void CancelUpdate(bool ForceClose)
        {
            if ((frameOn == 1 || frameOn == 3) && !ForceClose) //if downloading or updating
            {
                DialogResult dResult = MessageBox.Show(clientLang.CancelDialog.Content, clientLang.CancelDialog.Title, 
                    MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2);

                if (dResult == DialogResult.Yes)
                {
                    //cancel the update
                    isCancelled = true;
                    if (IsDownloading())
                    {
                        downloader.Cancel(); //cancel any downloads

                        //TODO: I should give the 'downloader' a bit of time to clean up partial files

                        //Bail out quickly. Don't hang around for servers to lazily respond.
                        isCancelled = true;
                        Close();
                        return;
                    }
                    
                    if (frameOn == 3 && !IsDownloading())
                        installUpdate.Cancel(); //cancel updates

                    //disable the 'X' button & cancel button
                    DisableCancel();
                } //otherwise, do nothing
            }
            else
            {
                //either force closed, or not download/updating
                isCancelled = true;
                Close();
            }
        }

        private bool IsDownloading()
        {
            //if downloading in anything, return true
            return frameOn == 1 || frameOn == 3 && downloader != null && 
                (update.CurrentlyUpdating == UpdateOn.DownloadingUpdate || update.CurrentlyUpdating == UpdateOn.DownloadingClientUpdt);
        }


        private void CancelUpdate()
        {
            CancelUpdate(false);
        }

        private void DisableCancel()
        {
            if (btnCancel.Enabled)
                SystemMenu.DisableCloseButton(this);

            btnCancel.Enabled = false;
        }

        private void EnableCancel()
        {
            if (!btnCancel.Enabled)
                SystemMenu.EnableCloseButton(this);

            btnCancel.Enabled = true;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            // handle the effect of minimize/restore on 
            // disabling the "close" button & menu item
            if (!btnCancel.Enabled)
                SystemMenu.DisableCloseButton(this);

            base.OnSizeChanged(e);
        }

        private void SetButtonText()
        {
            btnNext.Text = clientLang.NextButton;
            btnCancel.Text = clientLang.CancelButton;
        }

        private void btnCancel_SizeChanged(object sender, EventArgs e)
        {
            btnNext.Left = btnCancel.Left - btnNext.Width - 6;
        }

        #endregion

        #region Self Update

        private void SaveSelfUpdateData(string fileName)
        {
            FileStream fs = new FileStream(fileName, FileMode.Create, FileAccess.Write);

            // Write any file-identification data you want to here
            fs.Write(System.Text.Encoding.UTF8.GetBytes("IUSUFV2"), 0, 7);

            //Client data file location
            WriteFiles.WriteString(fs, 0x01, clientFileLoc);

            //Server data file location
            WriteFiles.WriteString(fs, 0x02, serverFileLoc);

            //Client server file
            WriteFiles.WriteString(fs, 0x03, clientSFLoc);

            //Base Directory
            WriteFiles.WriteString(fs, 0x04, baseDirectory);

            //Temporary directory
            WriteFiles.WriteString(fs, 0x05, tempDirectory);

            //Old client file location (self)
            WriteFiles.WriteString(fs, 0x06, System.Reflection.Assembly.GetExecutingAssembly().Location);

            //self update needed
            WriteFiles.WriteBool(fs, 0x07, willSelfUpdate);

            //check if the new client really has been elevated
            WriteFiles.WriteBool(fs, 0x08, needElevation);

            if (!string.IsNullOrEmpty(serverOverwrite))
                WriteFiles.WriteString(fs, 0x09, serverOverwrite);

            fs.WriteByte(0xFF);
            fs.Close();
        }

        private void LoadSelfUpdateData(string fileName)
        {
            byte[] fileIDBytes = new byte[7];

            FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read);

            // Read back the file identification data, if any
            fs.Read(fileIDBytes, 0, 7);
            string fileID = System.Text.Encoding.UTF8.GetString(fileIDBytes);
            if (fileID != "IUSUFV2")
            {
                //handle self update from RC1 client
                if (fileID == "IUSUFV1")
                {
                    LoadSelfUpdateRC1Data(fs);
                    return;
                }

                //free up the file so it can be deleted
                fs.Close();
                throw new Exception("Self update fileID is wrong: " + fileID);
            }

            byte bType = (byte)fs.ReadByte();
            while (!ReadFiles.ReachedEndByte(fs, bType, 0xFF))
            {
                switch (bType)
                {
                    case 0x01://Read Client data file location
                        clientFileLoc = ReadFiles.ReadString(fs);

                        //TODO: Remove this hackish behavior to cope with pre-RC2 client data files
                        if (clientFileLoc.EndsWith("iuc", StringComparison.InvariantCultureIgnoreCase))
                            clientFileType = ClientFileType.PreRC2;
                        else if (clientFileLoc.EndsWith("iucz", StringComparison.InvariantCultureIgnoreCase))
                            clientFileType = ClientFileType.RC2;
                        else
                            clientFileType = ClientFileType.Final;

                        break;
                    case 0x02: //Read Server data file location
                        serverFileLoc = ReadFiles.ReadString(fs);
                        break;
                    case 0x03: //Client server file location
                        clientSFLoc = ReadFiles.ReadString(fs);
                        break;
                    case 0x04://Read Base Directory
                        baseDirectory = ReadFiles.ReadString(fs);
                        break;
                    case 0x05://Read Temporary directory
                        tempDirectory = ReadFiles.ReadString(fs);
                        break;
                    case 0x06://Read Old client file location
                        oldClientLocation = ReadFiles.ReadString(fs);
                        break;
                    case 0x07: //true=Self Update, false=Continue update
                       
                        if (ReadFiles.ReadBool(fs))
                            SelfUpdating = true;
                        else
                            continuingUpdate = true;

                        break;
                    case 0x08: //is elevation required
                        needElevation = ReadFiles.ReadBool(fs);
                        break;
                    case 0x09:
                        serverOverwrite = ReadFiles.ReadString(fs);
                        break;
                    default:
                        ReadFiles.SkipField(fs, bType);
                        break;
                }

                bType = (byte)fs.ReadByte();
            }

            fs.Close();
        }

        //Backwards compatability with 1.0 RC1
        private void LoadSelfUpdateRC1Data(Stream fs)
        {
            selfUpdateFromRC1 = true;

            //RC1 means it's guaranteed to be old-style client data file
            clientFileType = ClientFileType.PreRC2;

            byte bType = (byte)fs.ReadByte();

            while (!ReadFiles.ReachedEndByte(fs, bType, 0xFF))
            {
                switch (bType)
                {
                    case 0x01://Read Client data file location
                        clientFileLoc = ReadFiles.ReadString(fs);
                        break;
                    case 0x02: //Read Server data file location
                        serverFileLoc = ReadFiles.ReadString(fs);
                        break;
                    case 0x03://Read Base Directory
                        baseDirectory = ReadFiles.ReadString(fs);
                        break;
                    case 0x04://Read Temporary directory
                        tempDirectory = ReadFiles.ReadString(fs);
                        break;
                    case 0x05://Read Old client file location
                        oldClientLocation = ReadFiles.ReadString(fs);
                        break;
                    case 0x06://Read New client file location
                        newClientLocation = ReadFiles.ReadString(fs);
                        break;
                    case 0x07:
                        SelfUpdating = ReadFiles.ReadBool(fs);
                        break;
                    case 0x08:
                        needElevation = ReadFiles.ReadBool(fs);
                        break;
                    default:
                        ReadFiles.SkipField(fs, bType);
                        break;
                }

                bType = (byte)fs.ReadByte();
            }

            fs.Close();
        }

        #endregion Self Update

        #region User Elevation

        private void StartSelfElevated()
        {
            ProcessStartInfo psi = new ProcessStartInfo
                                       {
                                           ErrorDialog = true, 
                                           ErrorDialogParentHandle = Handle
                                       };

            if (willSelfUpdate)
            {
                //create the filename for the newly copied client
                psi.FileName = Path.Combine(tempDirectory, 
                        Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location));

                //copy self to the temp folder
                File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, psi.FileName, true);
            }
            else if (SelfUpdating)
            {
                //launch the newly updated self
                psi.FileName = oldClientLocation;
            }
            else
                psi.FileName = System.Reflection.Assembly.GetExecutingAssembly().Location;

            if (needElevation)
                psi.Verb = "runas"; //elevate to administrator

            try
            {
                //write necessary info (base/temp dirs, new client files, etc.) to a file
                SaveSelfUpdateData(Path.Combine(tempDirectory, "selfUpdate.sup"));

                psi.Arguments = "-supdf:\"" + Path.Combine(tempDirectory, "selfUpdate.sup") + "\"";

                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                //the process couldn't be started, throw an error  
                //Note: this error even occurs when the administrator is using
                // a blank password
                //Note2: Can't run as a Guest account
                error = clientLang.AdminError;
                errorDetails = ex.Message;

                ShowFrame(-1);
            }
        }

        private bool NeedElevationToUpdate()
        {
            //no elevation necessary if it's not overwriting important files
            if (IsAdmin || (updateFrom.InstallingTo == 0 && updateFrom.RegChanges.Count == 0))
                return false;

            try
            {
                // if only updating local user files, no elevation is needed
                if (OnlyUpdatingLocalUser())
                    return false;

                // UAC Shield on next button for Windows Vista+
                if (VistaTools.AtLeastVista())
                    VistaTools.SetButtonShield(btnNext, true);
            }
            catch { }

            return true;
        }

        private bool OnlyUpdatingLocalUser()
        {
            //Vista only check when the client isn't already 
            // running with Admin (and elevated) priviledges

            //Elevation is needed...

            //if updating any registry other than HKEY_CURRENT_USER
            foreach (RegChange reg in updateFrom.RegChanges)
                if (reg.RegBasekey != RegBasekeys.HKEY_CURRENT_USER) return false;

            //if installing to the system folder or one of the common folders
            if (updateFrom.InstallingTo != 0 && (updateFrom.InstallingTo & InstallingTo.BaseDir) == 0)
                return false;

            string userProfileFolder = Environment.GetEnvironmentVariable("userprofile");

            //if the basedirectory isn't in the userprofile folder (C:\Users\UserName)
            if ((updateFrom.InstallingTo & InstallingTo.BaseDir) != 0 && !IsDirInDir(userProfileFolder, baseDirectory))
                return false;

            //if the client data file isn't in the userprofile folder
            if (!IsFileInDirectory(userProfileFolder, clientFileLoc))
                return false;

            //when self-updating, if this client is'nt in the userprofile folder
            if ((willSelfUpdate || SelfUpdating) && !IsFileInDirectory(userProfileFolder, System.Reflection.Assembly.GetExecutingAssembly().Location))
                return false;

            //it's not changing anything outside the user profile folder
            return true;
        }

        private static bool IsFileInDirectory(string dir, string file)
        {
            StringBuilder strBuild = new StringBuilder(InstallUpdate.MAX_PATH);

            bool bRet = InstallUpdate.PathRelativePathTo(
                strBuild,
                dir, (uint)InstallUpdate.PathAttribute.Directory,
                file, (uint)InstallUpdate.PathAttribute.File
            );

            if (bRet && strBuild.Length >= 2)
            {
                //get the first two characters
                if (strBuild.ToString().Substring(0, 2) == @".\") 
                {
                    //if file is in the directory (or a subfolder)
                    return true;
                }
            }

            return false;
        }

        private static bool IsDirInDir(string dir, string checkDir)
        {
            StringBuilder strBuild = new StringBuilder(InstallUpdate.MAX_PATH);

            bool bRet = InstallUpdate.PathRelativePathTo(
                strBuild,
                dir, (uint)InstallUpdate.PathAttribute.Directory,
                checkDir, (uint)InstallUpdate.PathAttribute.Directory
            );

            if (bRet)
            {
                if (strBuild.Length == 1) //result is "."
                    return true;

                if (strBuild.Length >= 2
                    //get the first two characters
                    && strBuild.ToString().Substring(0, 2) == @".\")
                {
                    //if checkDir is the directory (or a subfolder)
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Logging off & Shutting Down

        [DllImport("user32.dll")]
        public extern static bool ShutdownBlockReasonCreate(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string pwszReason);

        [DllImport("user32.dll")]
        public extern static bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        private bool logOffBlocked;

        private void BlockLogOff(bool block)
        {
            logOffBlocked = block;

            try
            {
                if (block)
                    ShutdownBlockReasonCreate(Handle, clientLang.LogOffError);
                else
                    ShutdownBlockReasonDestroy(Handle);
            }
            catch { }
        }

        protected override void WndProc(ref Message aMessage)
        {
            //WM_QUERYENDSESSION = 0x0011
            //WM_ENDSESSION = 0x0016
            if (logOffBlocked && (aMessage.Msg == 0x0011 || aMessage.Msg == 0x0016))
                return;

            base.WndProc(ref aMessage);
        }

        #endregion
    }
}
