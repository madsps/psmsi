﻿// Cmdlet to get the storage class type for a file, optionally passing
// the PSObject back through the pipeline with a new NoteProperty.
//
// Author: Heath Stewart <heaths@microsoft.com>
// Created: Sat, 12 Jan 2008 17:09:56 GMT
//
// Copyright (C) Microsoft Corporation. All rights reserved.
//
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Management;
using System.Management.Automation;
using Microsoft.Windows.Installer;
using Microsoft.Windows.Installer.PowerShell;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Windows.Installer.PowerShell.Commands
{
    [Cmdlet(VerbsCommon.Get, "MSIFileType",
        DefaultParameterSetName = ParameterSet.Path)]
    public sealed class GetFileTypeCommand : CommandBase
    {
        protected override void ProcessRecord()
        {
            foreach (string _path in this.path)
            {
                // with seemingly no other way, check if the path is valid if literal
                if (this.literal && !this.SessionState.Path.IsValid(_path))
                {
                    continue;
                }

                Collection<PSObject> items = this.InvokeProvider.Item.Get(_path);
                foreach (PSObject item in items)
                {
                    string fileType = null;
                    string fsPath = Location.GetOneResolvedProviderPathFromPSObject(item, this);

                    if (null != fsPath && File.Exists(fsPath))
                    {
                        Guid clsid = Guid.Empty;

                        try
                        {
                            Storage stg = Storage.OpenStorage(fsPath, true);
                            clsid = stg.Clsid;
                        }
                        catch (IOException)
                        {
                            this.WriteDebug(string.Format(CultureInfo.InvariantCulture, Properties.Resources.File_NotStorage, fsPath));
                        }
                        catch (System.ComponentModel.Win32Exception ex)
                        {
                            // non-terminating error; continue to the next file
                            string message = ex.Message.Replace("%1", fsPath);
                            PSInvalidOperationException psex = new PSInvalidOperationException(message, ex);

                            this.WriteError(psex.ErrorRecord);
                        }

                        // set a friendly type name
                        if (NativeMethods.CLSID_MsiPackage == clsid)
                        {
                            fileType = Msi.MsiPackage;
                        }
                        else if (NativeMethods.CLSID_MsiPatch == clsid)
                        {
                            fileType = Msi.MsiPatch;
                        }
                        else if (NativeMethods.CLSID_MsiTransform == clsid)
                        {
                            fileType = Msi.MsiTransform;
                        }
                    }

                    // pass everything through so format cmdlets work properly
                    if (this.passThru)
                    {
                        item.Properties.Add(new PSNoteProperty("MSIFileType", fileType));

                        this.WriteObject(item);
                    }
                    else if (!this.Stopping)
                    {
                        this.WriteObject(fileType);
                    }
                }
            }
        }

        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        [Parameter(
                HelpMessageBaseName = "Microsoft.Windows.Installer.Properties.Resources",
                HelpMessageResourceId = "Location_Path",
                ParameterSetName = ParameterSet.Path,
                Mandatory = true,
                Position = 0,
                ValueFromPipeline = true,
                ValueFromPipelineByPropertyName = true)]
        public string[] Path
        {
            get { return this.path; }
            set
            {
                this.literal = false;
                this.path = value;
            }
        }
        string[] path;

        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays")]
        [Parameter(
                HelpMessageBaseName = "Microsoft.Windows.Installer.Properties.Resources",
                HelpMessageResourceId = "Location_LiteralPath",
                ParameterSetName = ParameterSet.LiteralPath,
                Mandatory = true,
                Position = 0,
                ValueFromPipeline = false,
                ValueFromPipelineByPropertyName = true)]
        [Alias("PSPath")]
        public string[] LiteralPath
        {
            get { return this.path; }
            set
            {
                this.literal = true;
                this.path = value;
            }
        }
        bool literal;

        [Parameter]
        public SwitchParameter PassThru
        {
            get { return passThru; }
            set { passThru = value; }
        }
        bool passThru;
    }
}