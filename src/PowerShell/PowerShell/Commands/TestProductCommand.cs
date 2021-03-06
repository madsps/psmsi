﻿// The MIT License (MIT)
//
// Copyright (c) Microsoft Corporation
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management.Automation;
using Microsoft.Deployment.WindowsInstaller;
using Microsoft.Deployment.WindowsInstaller.Package;
using Microsoft.Tools.WindowsInstaller.Properties;

namespace Microsoft.Tools.WindowsInstaller.PowerShell.Commands
{
    /// <summary>
    /// The Test-MSIProduct cmdlet.
    /// </summary>
    [Cmdlet(VerbsDiagnostic.Test, "MSIProduct", DefaultParameterSetName = ParameterSet.Path)]
    [OutputType(typeof(IceMessage))]
    public sealed class TestProductCommand : PackageCommandBase
    {
        private InstallUIOptions previousInternalUI = InstallUIOptions.Default;
        private ExternalUIRecordHandler previousExternalUI = null;

        // Used by nested classes.
        private Queue<Data> output = new Queue<Data>();
        private string currentPath = null;

        /// <summary>
        /// Gets or sets additional ICE .cub files to use for validation.
        /// </summary>
        [Parameter]
        [Alias("Cube")]
        [ValidateNotNullOrEmpty]
        public string[] AdditionalCube { get; set; }

        /// <summary>
        /// Gets or sets whether to include the default ICE .cub file installed by Orca or MsiVal2.
        /// </summary>
        [Parameter]
        public SwitchParameter NoDefault { get; set; }

        /// <summary>
        /// Gets or sets the wilcard patterns of ICEs to include. By default, all ICEs are included.
        /// </summary>
        [Parameter]
        [ValidateNotNullOrEmpty]
        public string[] Include { get; set; }

        /// <summary>
        /// Gets or sets the wilcard patterns of ICEs to exclude. By default, all ICEs are included.
        /// </summary>
        [Parameter]
        [ValidateNotNullOrEmpty]
        public string[] Exclude { get; set; }

        /// <summary>
        /// Gets a value indicating whether the standard Verbose parameter was set.
        /// </summary>
        private bool IsVerbose
        {
            get
            {
                var bound = this.MyInvocation.BoundParameters;
                return bound.ContainsKey("Verbose") && (bool)(SwitchParameter)bound["Verbose"];
            }
        }

        /// <summary>
        /// Sets up the user interface handlers.
        /// </summary>
        protected override void BeginProcessing()
        {
            // Set up the UI handlers.
            this.previousInternalUI = Installer.SetInternalUI(InstallUIOptions.Silent);
            this.previousExternalUI = Installer.SetExternalUI(this.OnMessage, InstallLogModes.FatalExit | InstallLogModes.Error | InstallLogModes.Warning | InstallLogModes.User);

            base.BeginProcessing();
        }

        /// <summary>
        /// Merges ICE cubes into the database <paramref name="item"/> and executes selected ICEs.
        /// </summary>
        /// <param name="item">The database to validate.</param>
        protected override void ProcessItem(PSObject item)
        {
            // Get the item path and set the current context.
            string path = item.GetPropertyValue<string>("PSPath");
            path = this.SessionState.Path.GetUnresolvedProviderPathFromPSPath(path);
            this.currentPath = path;

            // Copy the database to a writable location and open.
            string copy = this.Copy(path);
            using (var db = new InstallPackage(copy, DatabaseOpenMode.Direct))
            {
                // Apply any patches or transforms before otherwise modifying.
                this.ApplyTransforms(db);

                // Copy the ProductCode and drop the Property table to avoid opening an installed product.
                bool hasProperty = db.IsTablePersistent("Property");
                string productCode = null;

                if (hasProperty)
                {
                    productCode = db.ExecutePropertyQuery("ProductCode");
                }

                // Merge the ICE cubes and fix up the database if needed.
                this.MergeCubes(db);
                if (!hasProperty)
                {
                    db.Execute("DROP TABLE `Property`");
                }

                var included = new List<WildcardPattern>();
                if (null != this.Include)
                {
                    Array.ForEach(this.Include, pattern => included.Add(new WildcardPattern(pattern)));
                }

                var excluded = new List<WildcardPattern>();
                if (null != this.Exclude)
                {
                    Array.ForEach(this.Exclude, pattern => excluded.Add(new WildcardPattern(pattern)));
                }

                // Get all the ICE actions in the database that are not excluded.
                var actions = new List<string>();
                foreach (var action in db.ExecuteStringQuery("SELECT `Action` FROM `_ICESequence` ORDER BY `Sequence`"))
                {
                    if (!action.Match(excluded))
                    {
                        actions.Add(action);
                    }
                }

                // Remove any actions not explicitly included.
                if (0 < included.Count)
                {
                    for (int i = actions.Count - 1; 0 <= i; --i)
                    {
                        if (!actions[i].Match(included))
                        {
                            actions.RemoveAt(i);
                        }
                    }
                }

                // Open a session with the database.
                using (var session = Installer.OpenPackage(db, false))
                {
                    // Put the original ProductCode back.
                    if (!string.IsNullOrEmpty(productCode))
                    {
                        db.Execute("DELETE FROM `Property` WHERE `Property` = 'ProductCode'");
                        db.Execute("INSERT INTO `Property` (`Property`, `Value`) VALUES ('ProductCode', '{0}')", productCode);
                    }

                    // Now execute all the remaining actions in order.
                    foreach (string action in actions)
                    {
                        try
                        {
                            session.DoAction(action);
                            this.Flush();
                        }
                        catch (InstallerException ex)
                        {
                            using (var pse = new PSInstallerException(ex))
                            {
                                if (null != pse.ErrorRecord)
                                {
                                    this.WriteError(pse.ErrorRecord);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Restores the previous user interface handlers.
        /// </summary>
        protected override void EndProcessing()
        {
            Installer.SetInternalUI(this.previousInternalUI);
            Installer.SetExternalUI(this.previousExternalUI, InstallLogModes.None);

            base.EndProcessing();
        }

        private string Copy(string path)
        {
            string temp = System.IO.Path.GetTempPath();
            string name = System.IO.Path.GetFileName(path);
            string copy = System.IO.Path.Combine(temp, name);

            // Copy and overwrite the file into the TEMP directory.
            this.WriteDebug(string.Format(CultureInfo.CurrentCulture, Resources.Action_Copy, path, copy));
            File.Copy(path, copy, true);

            // Unset the read-only attribute.
            var attributes = File.GetAttributes(copy);
            File.SetAttributes(copy, attributes & ~System.IO.FileAttributes.ReadOnly);

            return copy;
        }

        private void MergeCube(Database db, string path)
        {
            using (var cube = new Database(path, DatabaseOpenMode.ReadOnly))
            {
                try
                {
                    this.WriteDebug(string.Format(CultureInfo.CurrentCulture, Resources.Action_Merge, path, db.FilePath));
                    db.Merge(cube, "MergeConflicts");
                }
                catch
                {
                }
            }
        }

        private void MergeCubes(InstallPackage db)
        {
            if (!this.NoDefault)
            {
                string darice = ComponentSearcher.Find(ComponentSearcher.KnownComponent.Darice);
                if (!string.IsNullOrEmpty(darice))
                {
                    this.MergeCube(db, darice);
                }
                else
                {
                    this.WriteWarning(Resources.Error_DefaultCubNotFound);
                }
            }

            if (null != this.AdditionalCube)
            {
                foreach (string cube in this.ResolveFiles(this.AdditionalCube))
                {
                    this.MergeCube(db, cube);
                }

                db.Commit();
            }
        }

        private MessageResult OnMessage(InstallMessage messageType, Deployment.WindowsInstaller.Record messageRecord, MessageButtons buttons, MessageIcon icon, MessageDefaultButton defaultButton)
        {
            switch (messageType)
            {
                case InstallMessage.FatalExit:
                case InstallMessage.Error:
                    return this.OnError(messageRecord);

                case InstallMessage.Warning:
                    return this.OnWarning(messageRecord);

                case InstallMessage.User:
                    return this.OnInformation(messageRecord);

                default:
                    return MessageResult.None;
            }
        }

        private MessageResult OnError(Deployment.WindowsInstaller.Record record)
        {
            if (null != record)
            {
                using (var ex = new PSInstallerException(record))
                {
                    if (null != ex.ErrorRecord)
                    {
                        var data = new Data(DataType.Error, ex.ErrorRecord);
                        this.output.Enqueue(data);
                    }
                }
            }

            return MessageResult.OK;
        }

        private MessageResult OnWarning(Deployment.WindowsInstaller.Record record)
        {
            if (null != record)
            {
                string message = record.ToString();

                var data = new Data(DataType.Warning, message);
                this.output.Enqueue(data);
            }

            return MessageResult.OK;
        }

        private MessageResult OnInformation(Deployment.WindowsInstaller.Record record)
        {
            if (null != record)
            {
                string message = record.ToString();
                if (!string.IsNullOrEmpty(message))
                {
                    var ice = new IceMessage(message);
                    var obj = PSObject.AsPSObject(ice);

                    if (!string.IsNullOrEmpty(this.currentPath))
                    {
                        ice.Path = this.currentPath;

                        // Set the PSPath for cmdlets that would use it.
                        string path = this.SessionState.Path.GetUnresolvedPSPathFromProviderPath(this.currentPath);
                        obj.SetPropertyValue<string>("PSPath", path);
                    }

                    var data = new Data(DataType.Information, obj);
                    this.output.Enqueue(data);
                }
            }

            return MessageResult.OK;
        }

        private void Flush()
        {
            // Since the session runs in a separate thread, data enqueued in an output queue
            // and must be dequeued in the pipeline execution thread.
            while (0 < this.output.Count)
            {
                var data = this.output.Dequeue();
                switch (data.Type)
                {
                    case DataType.Error:
                        this.WriteError((ErrorRecord)data.Output);
                        break;

                    case DataType.Warning:
                        this.WriteWarning((string)data.Output);
                        break;

                    case DataType.Information:
                        var obj = data.Output as PSObject;
                        if (null != obj && obj.BaseObject is IceMessage)
                        {
                            this.WriteIceMessage(obj);
                        }
                        else if (null != data.Output)
                        {
                            this.WriteVerbose(data.Output.ToString());
                        }

                        break;
                }
            }
        }

        private void WriteIceMessage(PSObject obj)
        {
            var ice = obj.BaseObject as IceMessage;
            if (null != ice)
            {
                if (IceMessageType.Information == ice.Type)
                {
                    if (this.IsVerbose)
                    {
                        this.WriteObject(ice);
                    }
                }
                else
                {
                    this.WriteObject(ice);
                }
            }
        }

        private class Data
        {
            internal Data(DataType type, object output)
            {
                this.Type = type;
                this.Output = output;
            }

            internal DataType Type { get; private set; }
            internal object Output { get; private set; }
        }

        private enum DataType
        {
            Error,
            Warning,
            Information,
        }
    }
}
