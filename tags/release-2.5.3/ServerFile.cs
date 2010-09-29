﻿using System;
using System.Collections.Generic;

namespace wyUpdate.Common
{
    public class VersionChoice
    {
        public string Version;
        public string Changes;
        public bool RTFChanges;
        public List<string> FileSites = new List<string>();
        public long FileSize;
        public long Adler32;

        //Determine if client elevation is needed (Vista & non-admin users)
        public InstallingTo InstallingTo = 0;
        public List<RegChange> RegChanges = new List<RegChange>();
    }

    [Flags]
    public enum InstallingTo { BaseDir = 1, SysDirx86 = 2, CommonDesktop = 4, CommonStartMenu = 8, CommonAppData = 16, SysDirx64 = 32, WindowsRoot = 64 }

    public class NoUpdatePathToNewestException : Exception { }

    public class PatchApplicationException : Exception
    {
        public PatchApplicationException(string message) : base(message) { }
    }

    public partial class ServerFile
    {
        public ServerFile()
        {
            ServerFileSites = new List<string>(1);
            ClientServerSites = new List<string>(1);
        }

        public string NewVersion { get; set; }

        //Server Side Information
        public List<VersionChoice> VersionChoices = new List<VersionChoice>();

        public string MinClientVersion { get; set; }


        public string NoUpdateToLatestLinkText { get; set; }

        public string NoUpdateToLatestLinkURL { get; set; }

        public List<string> ClientServerSites { get; set; }

        public List<string> ServerFileSites { get; set; }
    }
}